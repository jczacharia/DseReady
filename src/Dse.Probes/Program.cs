// Copyright (c) PNC Financial Services. All rights reserved.
//
// Confluence ingest bottleneck probe — V2: declarative PULL-BASED async-enumerable crawl (Dse4 style).
//
// Compare against Program_v1.cs (work-queue + lazy offset fan-out). Same endpoint, expand, and config —
// the only difference is the orchestration:
//   * Per-(space,type) page streams (IAsyncEnumerable), composed with GateBeforePull so each pull from
//     Confluence is preceded by a channel-readiness check — pure pull-based back-pressure (no page is
//     fetched unless ES will accept it). Two levels: gate the chain intake AND each page pull.
//   * Parallel.ForEachAsync over the gated chain stream at CrawlConcurrency.
//   * NO fan-out (pull streams are linear), so a mega-space is a sequential straggler — that's the
//     deliberate contrast with v1. We're comparing design ergonomics + throughput, endpoint held constant.
//
// Run:  dotnet run --project src/Dse.Probes -c Release

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dse.ES;
using Dse.Sources.Confluence;
using Elastic.Channels;
using Elastic.Ingest.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ───────────────────────────── TUNING — sweep these ─────────────────────────────
const int CrawlConcurrency = 32;     // concurrent (space,type) page streams — SWEEP 24/32/48 to find Confluence's knee
const int PageSize = 100;            // results per page — the space content endpoint caps at 100
const long MaxDocs = 50_000;         // global cap for fast iteration; 0 = whole corpus
const int MaxSpaces = 0;             // 0 = all spaces
const string SpaceType = "global";   // "global" (enterprise spaces) or "" for all incl. personal (~user)
string[] ContentTypes = ["page", "blogpost"]; // one page-stream per (space, type)

// body.storage is the only thing indexed; version drives the change-detection hash. Space comes free (per-space crawl).
string[] Expand = ["body.storage", "version"];

// ES is NOT the bottleneck (read is). These are sized for the read feed with headroom, not stress.
const string ProbeIndex = "confluence-probe"; // throwaway target index; no alias promoted, safe to delete afterwards
const long ExportMaxBytes = 10L * 1024 * 1024; // byte budget per bulk — binds first (~770 docs/bulk at 13 KiB/doc)
const int ExportMaxItems = 2_000;    // item ceiling per bulk (bytes bind before this)
const int ExportConcurrency = 8;     // concurrent bulk requests
const int InboundBufferMaxSize = 3_500; // large in-flight queue (Dse4's value) — lets readers burst ahead of ES
const double OutboundBufferMaxLifetimeSeconds = 1; // flush partial bulks every 1s (Dse4) — keeps the pipe moving
// ─────────────────────────────────────────────────────────────────────────────────

var configManager = new ConfigurationManager();
configManager.AddEnvironmentVariables().AddUserSecrets("dse");

HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    EnvironmentName = "Development",
    ApplicationName = "Dse.Probes",
    Args = args,
    Configuration = configManager,
});
IConfiguration config = builder.Configuration;
IHostEnvironment env = builder.Environment;

using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b
    .AddConfiguration(config.GetSection("Logging"))
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    }));
ILogger log = loggerFactory.CreateLogger("ConfluenceProbe");

ConfluenceOptions confluence = config.GetSection("Confluence").Get<ConfluenceOptions>() ?? new ConfluenceOptions();
ElasticOptions elastic = config.GetSection("Elastic").Get<ElasticOptions>() ?? new ElasticOptions();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
CancellationToken ct = cts.Token;

var m = new Metrics();
using var meter = new Meter("Dse.Probes.ConfluenceIngest");
meter.CreateObservableCounter("confluence.docs.read", () => m.DocsRead);
meter.CreateObservableCounter("confluence.bytes.read", () => m.BytesRead);
meter.CreateObservableCounter("confluence.docs.written", () => m.DocsWritten);
meter.CreateObservableCounter("confluence.write.errors", () => m.WriteErrors);

log.LogInformation("Consts (V2 pull-based): {@Consts}", new
{
    CrawlConcurrency,
    PageSize,
    MaxDocs,
    MaxSpaces,
    SpaceType,
    ContentTypes,
    Expand,
    ProbeIndex,
    ExportMaxItems,
    ExportMaxBytes,
    ExportConcurrency,
    InboundBufferMaxSize,
});

// ── Confluence client: explicit, no resilience pipeline so latency is raw. Through the F5 VIP so least-connections
//    routes more to faster nodes; MaxConnectionsPerServer caps concurrency at the socket layer. ──
using var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = CrawlConcurrency,
    AutomaticDecompression = DecompressionMethods.All,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(10),
};

using var http = new HttpClient(handler) { BaseAddress = new Uri(confluence.BaseAddress), Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
if (confluence is { Username.Length: > 0, Password.Length: > 0 })
{
    string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{confluence.Username}:{confluence.Password}"));
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
}

string expand = Uri.EscapeDataString(string.Join(separator: ',', Expand));

static string Relativize(string next) =>
    next.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(next).PathAndQuery : next;

// Fetch one content page; split server(TTFB) vs transfer vs parse.
async Task<SearchPage> FetchAsync(string url, Action<long, long, long, int>? record, CancellationToken token)
{
    var sw = Stopwatch.StartNew();
    using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
    long ttfbTicks = sw.ElapsedTicks;
    resp.EnsureSuccessStatusCode();

    sw.Restart();
    byte[] bytes = await resp.Content.ReadAsByteArrayAsync(token);
    long bodyTicks = sw.ElapsedTicks;

    sw.Restart();
    SearchPage page = JsonSerializer.Deserialize<SearchPage>(bytes, ConfluenceDocJsonConverter.JsonSerializerOptions)
        ?? throw new InvalidOperationException("null response");
    long parseTicks = sw.ElapsedTicks;

    record?.Invoke(ttfbTicks, bodyTicks, parseTicks, bytes.Length);
    return page;
}

// ── Streams (declarative): spaces, then (space,type) chains, then pages-per-chain — all via _links.next ──
async IAsyncEnumerable<SpaceRef> SpacesStream([EnumeratorCancellation] CancellationToken token)
{
    string typeFilter = string.IsNullOrEmpty(SpaceType) ? "" : $"&type={SpaceType}";
    string? url = $"/rest/api/space?limit=1000{typeFilter}";
    int yielded = 0;
    while (url is not null)
    {
        using HttpResponseMessage resp = await http.GetAsync(url, token);
        resp.EnsureSuccessStatusCode();
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(token);
        SpaceListPage page = JsonSerializer.Deserialize<SpaceListPage>(bytes, ConfluenceDocJsonConverter.JsonSerializerOptions)
            ?? throw new InvalidOperationException("null space response");

        foreach (SpaceRef space in page.Results)
        {
            if (MaxSpaces > 0 && yielded >= MaxSpaces)
            {
                yield break;
            }

            yielded++;
            yield return space;
        }

        url = page.Links?.Next is { } n ? Relativize(n) : null;
    }
}

async IAsyncEnumerable<(SpaceRef Space, string Type)> ChainsStream([EnumeratorCancellation] CancellationToken token)
{
    await foreach (SpaceRef space in SpacesStream(token).ConfigureAwait(false))
    {
        foreach (string type in ContentTypes)
        {
            yield return (space, type);
        }
    }
}

async IAsyncEnumerable<SearchPage> PagesStream(string spaceKey, string type, [EnumeratorCancellation] CancellationToken token)
{
    string? url = $"/rest/api/space/{Uri.EscapeDataString(spaceKey)}/content/{type}?limit={PageSize}&expand={expand}";
    while (url is not null)
    {
        SearchPage page = await FetchAsync(url, (ttfb, body, parse, bytes) =>
        {
            Interlocked.Add(ref m.TtfbTicks, ttfb);
            Interlocked.Add(ref m.BodyTicks, body);
            Interlocked.Add(ref m.ParseTicks, parse);
            Interlocked.Add(ref m.BytesRead, bytes);
            Interlocked.Increment(ref m.Pages);
            m.TtfbMs.Add(Stopwatch.GetElapsedTime(startingTimestamp: 0, ttfb).TotalMilliseconds);
        }, token);

        yield return page;
        url = page.Links?.Next is { } n ? Relativize(n) : null;
    }
}

// ── Elasticsearch write path: real IngestChannel, explicit BufferOptions. Ingestion is always ON —
//    we write for real, but never promote an alias, so the throwaway index is simply deleted after. ──
using var channel = new IngestChannel<ConfluenceDoc>(new IngestChannelOptions<ConfluenceDoc>(
    new DistributedTransport(new TransportConfigurationDescriptor(new StaticNodePool(elastic.Endpoints()))
        .Authentication(new ApiKey(elastic.ApiKey))
        .RequestTimeout(TimeSpan.FromSeconds(30))
        .ServerCertificateValidationCallback(CertificateValidations.AllowAll)
        .EnableDebugMode()
        .EnableThreadPoolStats(false)
        .EnableTcpStats(false)),
    new Confluence().GetTypeContext(env), DateTimeOffset.UtcNow, ProbeIndex)
{
    BufferOptions = new BufferOptions
    {
        OutboundBufferMaxSize = ExportMaxItems,
        OutboundBufferMaxBytes = ExportMaxBytes,
        OutboundBufferMaxLifetime = TimeSpan.FromSeconds(OutboundBufferMaxLifetimeSeconds),
        InboundBufferMaxSize = InboundBufferMaxSize,
        ExportMaxConcurrency = ExportConcurrency,
    },
    ExportItemsAttemptCallback = (retries, count) =>
    {
        if (retries == 0)
        {
            Interlocked.Add(ref m.DocsWritten, count);
        }
    },
    ExportResponseCallback = (response, _) =>
    {
        Interlocked.Increment(ref m.BulkCount);
        int errors = response.Items?.Count(i => i.Error is not null) ?? 0;
        if (response.Error is not null)
        {
            errors++;
        }

        if (errors > 0)
        {
            Interlocked.Add(ref m.WriteErrors, errors);
        }
    },
});

await channel.BootstrapElasticsearchAsync(BootstrapMethod.Failure, ct);
log.LogInformation("Ingesting into throwaway index '{Index}' — no alias is promoted; delete it when done.", ProbeIndex);

// ── Live throughput reporter (per second) ──
var totalSw = Stopwatch.StartNew();
Task reporter = Task.Run(async () =>
{
    long lastRead = 0, lastWritten = 0;
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
    {
        long read = Interlocked.Read(ref m.DocsRead), written = Interlocked.Read(ref m.DocsWritten);
        log.LogInformation("  read {Read,7}/s ({ReadTot} total) | write {Write,7}/s ({WriteTot} total)",
            read - lastRead, read, written - lastWritten, written);
        lastRead = read;
        lastWritten = written;
    }
}, ct);

// Pull-based back-pressure gate: each pull waits until the channel will accept a write. Returns false to STOP
// the stream entirely (channel closed, or the doc cap reached) — pure pull semantics, no fetch happens past it.
async ValueTask<bool> ReadyForMore(CancellationToken token)
{
    if (MaxDocs > 0 && Interlocked.Read(ref m.DocsRead) >= MaxDocs)
    {
        return false;
    }

    var sw = Stopwatch.StartNew();
    bool ok = await channel.WaitToWriteAsync(token).ConfigureAwait(false);
    Interlocked.Add(ref m.WriteBlockedTicks, sw.ElapsedTicks);
    return ok;
}

try
{
    await Parallel.ForEachAsync(
        ChainsStream(ct).GateBeforePull(ReadyForMore, ct), // gate the CHAIN intake on channel readiness
        new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = CrawlConcurrency },
        async (chain, token) =>
        {
            // gate each PAGE pull too — nothing is fetched from Confluence unless ES will accept it
            await foreach (SearchPage page in PagesStream(chain.Space.Key, chain.Type, token)
                               .GateBeforePull(ReadyForMore, token).ConfigureAwait(false))
            {
                Interlocked.Add(ref m.DocsRead, page.Results.Count);

                foreach (ConfluenceDoc doc in page.Results)
                {
                    doc.Space = new ConfluenceDoc.SpaceRecord // space for free, from the listing
                    {
                        Id = chain.Space.Id, Key = chain.Space.Key, Name = chain.Space.Name, Link = null,
                    };

                    var write = Stopwatch.StartNew();
                    await channel.WriteAsync(doc, token).ConfigureAwait(false);
                    Interlocked.Add(ref m.WriteBlockedTicks, write.ElapsedTicks);
                }
            }
        });

    log.LogInformation("Draining channel to Elasticsearch...");
    await channel.WaitForDrainAsync(TimeSpan.FromMinutes(10), ct);
}
catch (HttpRequestException ex)
{
    Interlocked.Increment(ref m.FetchErrors);
    log.LogWarning("Fetch failed: {Status} {Message}", ex.StatusCode, ex.Message);
}
finally
{
    cts.Cancel();
    await reporter.ContinueWith(_ => { }, TaskScheduler.Default);
    channel.Dispose();
}

// ── Summary + verdict ──
double wallSec = totalSw.Elapsed.TotalSeconds;
double readPerSec = m.DocsRead / wallSec;
double avgServerMs = m.Pages == 0 ? 0 : Stopwatch.GetElapsedTime(startingTimestamp: 0, m.TtfbTicks).TotalMilliseconds / m.Pages;
double avgTransferMs = m.Pages == 0 ? 0 : Stopwatch.GetElapsedTime(startingTimestamp: 0, m.BodyTicks).TotalMilliseconds / m.Pages;
double avgParseMs = m.Pages == 0 ? 0 : Stopwatch.GetElapsedTime(startingTimestamp: 0, m.ParseTicks).TotalMilliseconds / m.Pages;
double perDocMs = m.DocsRead == 0 ? 0 : Stopwatch.GetElapsedTime(startingTimestamp: 0, m.TtfbTicks).TotalMilliseconds / m.DocsRead;
double bytesPerDoc = m.DocsRead == 0 ? 0 : (double)m.BytesRead / m.DocsRead;

var proc = Process.GetCurrentProcess();
proc.Refresh();

Console.WriteLine();
Console.WriteLine("──────────────── PROBE SUMMARY (V2 pull-based) ────────────────");
Console.WriteLine($"  wall time        : {wallSec:F1}s");
Console.WriteLine($"  docs read        : {m.DocsRead}  ({readPerSec:F0}/s)");
Console.WriteLine(
    $"  bytes read       : {m.BytesRead / 1024.0 / 1024.0:F1} MiB  ({m.BytesRead / 1024.0 / 1024.0 / wallSec:F1} MiB/s) | {bytesPerDoc / 1024.0:F1} KiB/doc");
Console.WriteLine($"  pages            : {m.Pages}  (fetch errors {m.FetchErrors})");
Console.WriteLine(
    $"  per page         : server(TTFB) avg {avgServerMs:F0}ms | transfer avg {avgTransferMs:F0}ms | parse avg {avgParseMs:F1}ms");
Console.WriteLine($"  per doc (server) : {perDocMs:F1}ms  (CQL was ~500ms/doc)");
Console.WriteLine(
    $"  server(TTFB) dist: p50 {Percentile(m.TtfbMs, p: 0.50):F0}ms  p95 {Percentile(m.TtfbMs, p: 0.95):F0}ms  max {Percentile(m.TtfbMs, p: 1.0):F0}ms");
Console.WriteLine(
    $"  peak memory      : {proc.PeakWorkingSet64 / 1024 / 1024} MiB working set | {GC.GetTotalAllocatedBytes() / 1024 / 1024} MiB allocated total");

double writePerSec = m.DocsWritten / wallSec;
double blockSec = Stopwatch.GetElapsedTime(startingTimestamp: 0, m.WriteBlockedTicks).TotalSeconds;
Console.WriteLine($"  docs written     : {m.DocsWritten}  ({writePerSec:F0}/s)  bulks {m.BulkCount}  errors {m.WriteErrors}");
Console.WriteLine($"  producer blocked : {blockSec:F1}s by back-pressure ({blockSec / wallSec * 100:F0}% of wall)");
Console.WriteLine(blockSec / wallSec > 0.25
    ? "  VERDICT: Elasticsearch WRITE is the bottleneck (producer spends much of its time blocked on back-pressure)."
    : "  VERDICT: write keeps up — bottleneck is on the READ side, see below.");
double readMs = avgServerMs + avgTransferMs;
Console.WriteLine(readMs > avgParseMs * 3
    ? avgServerMs > avgTransferMs * 3
        ? "  READ: Confluence SERVER time (TTFB) dominates — body.storage rendering."
        : "  READ: BODY TRANSFER dominates — response size/bandwidth bound."
    : "  READ: parse/CPU time is significant — client CPU (JSON + HTML clean) is a real factor.");
if (m.FetchErrors > 0)
{
    Console.WriteLine($"  NOTE: {m.FetchErrors} fetch error(s) — Confluence saturation/rate-limit, or a mega-space exceeded the offset cap.");
}

Console.WriteLine("────────────────────────────────────────────────");

return;

static double Percentile(ConcurrentBag<double> samples, double p)
{
    double[] sorted = [.. samples];
    if (sorted.Length == 0)
    {
        return 0;
    }

    Array.Sort(sorted);
    int idx = Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, min: 0, sorted.Length - 1);
    return sorted[idx];
}

internal static class AsyncExtensions
{
    /// <summary>Wraps <paramref name="source"/> so each pull (MoveNextAsync) is preceded by <paramref name="ready"/>.
    /// When ready resolves false, enumeration completes — the underlying source is not touched again.</summary>
    public static async IAsyncEnumerable<T> GateBeforePull<T>(
        this IAsyncEnumerable<T> source,
        Func<CancellationToken, ValueTask<bool>> ready,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using IAsyncEnumerator<T> e = source.GetAsyncEnumerator(ct);
        while (true)
        {
            if (!await ready(ct).ConfigureAwait(false))
            {
                yield break;
            }

            if (!await e.MoveNextAsync().ConfigureAwait(false))
            {
                yield break;
            }

            yield return e.Current;
        }
    }
}

internal sealed class Metrics
{
    public readonly ConcurrentBag<double> TtfbMs = [];
    public long BodyTicks; // body transfer + decompression
    public long BulkCount;
    public long BytesRead;
    public long DocsRead;
    public long DocsWritten;
    public long FetchErrors; // non-2xx / transport failures (saturation / rate-limit signal)
    public long Pages;
    public long ParseTicks; // deserialize + HTML clean (client CPU)
    public long TtfbTicks; // Confluence server-think time
    public long WriteBlockedTicks;
    public long WriteErrors;
}

internal sealed record SearchPage(
    [property: JsonPropertyName("results")] IReadOnlyList<ConfluenceDoc> Results,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("totalSize")] long? TotalSize,
    [property: JsonPropertyName("_links")] Links? Links);

internal sealed record SpaceListPage(
    [property: JsonPropertyName("results")] IReadOnlyList<SpaceRef> Results,
    [property: JsonPropertyName("_links")] Links? Links);

internal sealed record SpaceRef(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record Links([property: JsonPropertyName("next")] string? Next);
