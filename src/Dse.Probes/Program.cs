// Copyright (c) PNC Financial Services. All rights reserved.
//
// Confluence ingest bottleneck probe — per-space chains with lazy offset fan-out for big spaces.
//
// Design (informed by Elastic's official Confluence connector + our measurements):
//   * Crawl /rest/api/space/{key}/content/{page|blogpost} (direct list, ~120ms/doc — 4x cheaper than CQL),
//     following _links.next. No totalSize needed for correctness — page until next is gone.
//   * Work-queue + N workers (== connector's ConcurrentTasks) + IngestChannel back-pressure (== its MemQueue).
//   * Straggler fix the connector lacks: a chain still going after FanThresholdPages does ONE CQL count
//     (the only source of totalSize) and fans its REMAINING offsets out as parallel work items. Only the
//     few mega-spaces pay a count; small spaces never do.
//   * Through the F5 VIP: stateless offset/next requests, least-connections favors faster nodes.
//
// Run:  dotnet run --project src/Dse.Probes -c Release

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Dse.ES;
using Dse.Sources.Confluence;
using Elastic.Channels;
using Elastic.Ingest.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ───────────────────────────── TUNING — sweep these ─────────────────────────────
const int CrawlConcurrency = 32;     // concurrent page fetches — the #1 throughput lever. SWEEP 24/32/48 to find Confluence's knee
                                     // (Dse4's 400/s ran the crawl ~cluster-write-pool-wide, not 16). Watch fetch errors/TTFB.
const int PageSize = 100;            // results per page — the space content endpoint caps at 100
const int FanThresholdPages = 3;     // after this many sequential pages, CQL-count the space and fan offsets out
const long MaxDocs = 50_000;         // big enough to engage fan-out on a real mega-space; 0 = whole corpus
const int MaxSpaces = 0;             // 0 = all spaces — needed so the real mega-space is in play
const string SpaceType = "global";   // "global" (enterprise spaces) or "" for all incl. personal (~user)
string[] ContentTypes = ["page", "blogpost"]; // one seed chain per (space, type)

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

log.LogInformation("Consts: {@Consts}", new
{
    CrawlConcurrency,
    PageSize,
    FanThresholdPages,
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

string ContentUrl(string spaceKey, string type, long start) =>
    $"/rest/api/space/{Uri.EscapeDataString(spaceKey)}/content/{type}?start={start}&limit={PageSize}&expand={expand}";

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

// The ONLY source of a grand total is the CQL search (limit=0, no expand -> no body rendering). Used lazily, big spaces only.
async Task<long> CqlCountAsync(string spaceKey, string type, CancellationToken token)
{
    string url = $"/rest/api/content/search?cql={Uri.EscapeDataString($"space=\"{spaceKey}\" and type={type}")}&limit=0";
    SearchPage page = await FetchAsync(url, record: null, token);
    return page.TotalSize ?? 0;
}

// ── Enumerate spaces (cheap; paged via _links.next; NOT CQL) ──
async Task<List<SpaceRef>> EnumerateSpacesAsync()
{
    var spaces = new List<SpaceRef>();
    string typeFilter = string.IsNullOrEmpty(SpaceType) ? "" : $"&type={SpaceType}";
    string? url = $"/rest/api/space?limit=100{typeFilter}";
    while (url is not null)
    {
        using HttpResponseMessage resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        SpaceListPage page = JsonSerializer.Deserialize<SpaceListPage>(bytes, ConfluenceDocJsonConverter.JsonSerializerOptions)
            ?? throw new InvalidOperationException("null space response");
        spaces.AddRange(page.Results);
        url = page.Links?.Next is { } n ? (n.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(n).PathAndQuery : n) : null;
    }

    return spaces;
}

List<SpaceRef> spaces = await EnumerateSpacesAsync();
if (MaxSpaces > 0 && spaces.Count > MaxSpaces)
{
    spaces = spaces.GetRange(0, MaxSpaces);
}

if (spaces.Count == 0)
{
    return;
}

int seedCount = spaces.Count * ContentTypes.Length;
log.LogInformation("Crawling {Spaces} spaces ({Seeds} seed chains) via space content endpoint, limit {Page}, concurrency {Crawl}",
    spaces.Count, seedCount, PageSize, CrawlConcurrency);

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
        log.LogInformation("  read {Read,7}/s ({ReadTot} total) | write {Write,7}/s ({WriteTot} total) | fanned {Fan}",
            read - lastRead, read, written - lastWritten, written, Interlocked.Read(ref m.LazyCounts));
        lastRead = read;
        lastWritten = written;
    }
}, ct);

bool CapReached() => MaxDocs > 0 && Interlocked.Read(ref m.DocsRead) >= MaxDocs;

// ── Work queue: seed (space,type,start=0) chains; big chains fan their remaining offsets out as parallel items ──
var work = Channel.CreateUnbounded<WorkItem>();
long outstanding = 0;

void Post(WorkItem w)
{
    Interlocked.Increment(ref outstanding);
    work.Writer.TryWrite(w);
}

void Complete()
{
    if (Interlocked.Decrement(ref outstanding) == 0)
    {
        work.Writer.TryComplete();
    }
}

async Task ProcessAsync(WorkItem w, CancellationToken token)
{
    if (CapReached())
    {
        return;
    }

    // Back-pressure gate FIRST — block here if ES is behind rather than fetch a page into memory.
    var gate = Stopwatch.StartNew();
    if (!await channel.WaitToWriteAsync(token).ConfigureAwait(false))
    {
        return;
    }

    long blockedTicks = gate.ElapsedTicks;

    SearchPage page;
    try
    {
        page = await FetchAsync(ContentUrl(w.Space.Key, w.Type, w.Start), (ttfb, body, parse, bytes) =>
        {
            Interlocked.Add(ref m.TtfbTicks, ttfb);
            Interlocked.Add(ref m.BodyTicks, body);
            Interlocked.Add(ref m.ParseTicks, parse);
            Interlocked.Add(ref m.BytesRead, bytes);
            Interlocked.Increment(ref m.Pages);
            m.TtfbMs.Add(Stopwatch.GetElapsedTime(startingTimestamp: 0, ttfb).TotalMilliseconds);
        }, token);
    }
    catch (HttpRequestException ex)
    {
        Interlocked.Increment(ref m.FetchErrors);
        log.LogWarning("Fetch failed in space {Space} ({Type}) @ {Start}: {Status} {Message}",
            w.Space.Key, w.Type, w.Start, ex.StatusCode, ex.Message);
        return;
    }

    Interlocked.Add(ref m.DocsRead, page.Results.Count);
    if (page.TotalSize is not null)
    {
        Interlocked.Exchange(ref m.SawTotalSize, 1);
    }

    foreach (ConfluenceDoc doc in page.Results)
    {
        doc.Space = new ConfluenceDoc.SpaceRecord // space for free, from the listing
        {
            Id = w.Space.Id, Key = w.Space.Key, Name = w.Space.Name, Link = null,
        };

        var write = Stopwatch.StartNew();
        await channel.WriteAsync(doc, token).ConfigureAwait(false);
        blockedTicks += write.ElapsedTicks;
    }

    Interlocked.Add(ref m.WriteBlockedTicks, blockedTicks);

    // Fanned items are terminal. Sequential chains continue until there's no next page.
    if (w.Fanned || page.Links?.Next is null)
    {
        return;
    }

    int nextPage = w.ChainPage + 1;
    if (nextPage < FanThresholdPages)
    {
        Post(w with { Start = w.Start + PageSize, ChainPage = nextPage }); // keep walking sequentially
        return;
    }

    // Big space: one CQL count, then fan the remaining offsets out across the pool (parallel).
    long total = 0;
    try
    {
        total = await CqlCountAsync(w.Space.Key, w.Type, token);
    }
    catch (HttpRequestException)
    {
        // ignore; fall back to sequential below
    }

    if (total > w.Start + PageSize)
    {
        Interlocked.Increment(ref m.LazyCounts);
        for (long s = w.Start + PageSize; s < total; s += PageSize)
        {
            Post(w with { Start = s, Fanned = true });
        }
    }
    else
    {
        Post(w with { Start = w.Start + PageSize, ChainPage = nextPage }); // count failed/unknown -> keep chaining
    }
}

foreach (SpaceRef space in spaces)
{
    foreach (string type in ContentTypes)
    {
        Post(new WorkItem(space, type, Start: 0, ChainPage: 0, Fanned: false));
    }
}

async Task WorkerAsync()
{
    await foreach (WorkItem w in work.Reader.ReadAllAsync(ct).ConfigureAwait(false))
    {
        try
        {
            await ProcessAsync(w, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Worker error on space {Space} ({Type}) @ {Start}", w.Space.Key, w.Type, w.Start);
        }
        finally
        {
            Complete();
        }
    }
}

try
{
    Task[] workers = [.. Enumerable.Range(0, CrawlConcurrency).Select(_ => Task.Run(WorkerAsync, ct))];
    await Task.WhenAll(workers);

    log.LogInformation("Draining channel to Elasticsearch...");
    await channel.WaitForDrainAsync(TimeSpan.FromMinutes(10), ct);
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
Console.WriteLine("──────────────── PROBE SUMMARY ────────────────");
Console.WriteLine($"  wall time        : {wallSec:F1}s");
Console.WriteLine($"  docs read        : {m.DocsRead}  ({readPerSec:F0}/s)");
Console.WriteLine(
    $"  bytes read       : {m.BytesRead / 1024.0 / 1024.0:F1} MiB  ({m.BytesRead / 1024.0 / 1024.0 / wallSec:F1} MiB/s) | {bytesPerDoc / 1024.0:F1} KiB/doc");
Console.WriteLine($"  pages            : {m.Pages}  (fetch errors {m.FetchErrors})  | big spaces fanned {m.LazyCounts}");
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

internal sealed class Metrics
{
    public readonly ConcurrentBag<double> TtfbMs = [];
    public long BodyTicks; // body transfer + decompression
    public long BulkCount;
    public long BytesRead;
    public long DocsRead;
    public long DocsWritten;
    public long FetchErrors; // non-2xx / transport failures (saturation / rate-limit / offset-cap signal)
    public long LazyCounts; // big spaces that triggered a CQL count + offset fan-out
    public long Pages;
    public long ParseTicks; // deserialize + HTML clean (client CPU)
    public long SawTotalSize; // 1 if the content endpoint ever returned a totalSize field
    public long TtfbTicks; // Confluence server-think time
    public long WriteBlockedTicks;
    public long WriteErrors;
}

internal sealed record WorkItem(SpaceRef Space, string Type, long Start, int ChainPage, bool Fanned);

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
