// Copyright (c) PNC Financial Services. All rights reserved.


using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
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
const int CrawlConcurrency = 64; // concurrent SPACES crawled at once — SWEEP 16/64/128
const int PageSize = 50; // results per page (server caps body-expanded at 50)
const long MaxDocs = 5_000; // global cap for fast iteration; 0 = whole corpus
const int MaxSpaces = 0; // cap spaces probed for fast iteration; 0 = all
const string SpaceType = "global"; // "global" (enterprise spaces) or "" for all incl. personal (~user)
const string ContentCql = "type in (page,blogpost)"; // NO `order by lastModified` — mutates mid-crawl, breaks paging

// body.storage is the only thing indexed; version drives the change-detection hash. Space comes free (per-space crawl).
string[] Expand = ["body.storage", "version"];

// Empty => crawl through the F5 VIP (confluence.BaseAddress). Non-empty => pin each space to a node
// (round-robin) to keep cursors valid and bypass the LB. e.g. ["http://lcfl328a:8090", "http://lcfl329a:8090", ...]
// string[] ConfluenceNodes = ["http://lcfl328a:8090","http://lcfl329a:8090","http://lcfl330a:8090","http://lcfl331a:8090"];
string[] ConfluenceNodes = [];

const string ProbeIndex = "confluence-probe"; // throwaway target index; no alias promoted, safe to delete afterwards
const int ExportMaxItems = 48_000; // BufferOptions.OutboundBufferMaxSize (docs per bulk)
const long ExportMaxBytes = 10L * 1024 * 1024; // BufferOptions.OutboundBufferMaxBytes (byte budget per bulk)
const int ExportConcurrency = 64; // BufferOptions.ExportMaxConcurrency (concurrent bulk requests)
const int InboundBufferMaxSize = 250;
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
    MaxDocs,
    MaxSpaces,
    SpaceType,
    ContentCql,
    Expand,
    Nodes = ConfluenceNodes.Length == 0 ? "VIP" : string.Join(separator: ',', ConfluenceNodes),
    ProbeIndex,
    ExportMaxItems,
    ExportMaxBytes,
    ExportConcurrency,
    InboundBufferMaxSize,
});

// ── Confluence client(s): explicit, no resilience pipeline so latency is raw. One handler, one client per target. ──
using var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = CrawlConcurrency,
    AutomaticDecompression = DecompressionMethods.All,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(10),
};

string? basicAuth = confluence is { Username.Length: > 0, Password.Length: > 0 }
    ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{confluence.Username}:{confluence.Password}"))
    : null;

HttpClient CreateClient(string baseUrl)
{
    var c = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(baseUrl), Timeout = Timeout.InfiniteTimeSpan };
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    if (basicAuth is not null)
    {
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
    }

    return c;
}

HttpClient vip = CreateClient(confluence.BaseAddress);
HttpClient[] nodeClients = ConfluenceNodes.Length == 0 ? [vip] : [.. ConfluenceNodes.Select(CreateClient)];

string expand = Uri.EscapeDataString(string.Join(separator: ',', Expand));

// Fetch one search page; split server(TTFB) vs transfer vs parse. Returns docs + the next cursor link (or null).
async Task<SearchPage> FetchSearchAsync(HttpClient client,
                                        string url,
                                        Action<long, long, long, int>? record,
                                        CancellationToken token)
{
    var sw = Stopwatch.StartNew();
    using HttpResponseMessage resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
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

// A _links.next may be absolute (would defeat node pinning) — keep only path+query so it stays on `client`.
static string Relativize(string next) =>
    next.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(next).PathAndQuery : next;

// ── Enumerate spaces (cheap; paged via _links.next) ──
async Task<List<SpaceRef>> EnumerateSpacesAsync()
{
    var spaces = new List<SpaceRef>();
    string typeFilter = string.IsNullOrEmpty(SpaceType) ? "" : $"&type={SpaceType}";
    string? url = $"/rest/api/space?limit=100{typeFilter}";
    while (url is not null)
    {
        using HttpResponseMessage resp = await vip.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        SpaceListPage page = JsonSerializer.Deserialize<SpaceListPage>(bytes, ConfluenceDocJsonConverter.JsonSerializerOptions)
                             ?? throw new InvalidOperationException("null space response");
        spaces.AddRange(page.Results);
        url = page.Links?.Next is { } n ? Relativize(n) : null;
    }

    return spaces;
}

SearchPage headCount = await FetchSearchAsync(vip,
    $"/rest/api/content/search?cql={Uri.EscapeDataString(ContentCql)}&limit=0", record: null, ct);
List<SpaceRef> spaces = await EnumerateSpacesAsync();
if (MaxSpaces > 0 && spaces.Count > MaxSpaces)
{
    spaces = spaces.GetRange(index: 0, MaxSpaces);
}

log.LogInformation("Corpus ~{Total} docs across {SpaceCount} spaces ({Type}); crawling {Crawl} spaces at a time",
    headCount.TotalSize, spaces.Count, string.IsNullOrEmpty(SpaceType) ? "all" : SpaceType, CrawlConcurrency);
if (spaces.Count == 0)
{
    return;
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
        log.LogInformation("  read {Read,7}/s ({ReadTot} total) | write {Write,7}/s ({WriteTot} total) | spaces {Done}/{Total}",
            read - lastRead, read, written - lastWritten, written, Interlocked.Read(ref m.SpacesDone), spaces.Count);
        lastRead = read;
        lastWritten = written;
    }
}, ct);

bool CapReached() => MaxDocs > 0 && Interlocked.Read(ref m.DocsRead) >= MaxDocs;

try
{
    await Parallel.ForEachAsync(spaces.Select((s, i) => (Space: s, Index: i)),
        new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = CrawlConcurrency },
        async (item, token) =>
        {
            HttpClient client = nodeClients[item.Index % nodeClients.Length]; // pin this space's chain to one node
            var spaceSw = Stopwatch.StartNew();

            string spaceCql = Uri.EscapeDataString($"space=\"{item.Space.Key}\" and {ContentCql}");
            string? url = $"/rest/api/content/search?cql={spaceCql}&limit={PageSize}&expand={expand}";

            while (url is not null && !CapReached())
            {
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
                    page = await FetchSearchAsync(client, url, (ttfb, body, parse, bytes) =>
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
                    // Count, don't crash — a 429/503 is the saturation/rate-limit signal; a broken cursor through
                    // the VIP would surface here too (try ConfluenceNodes to pin chains to a node).
                    Interlocked.Increment(ref m.FetchErrors);
                    log.LogWarning("Fetch failed in space {Space}: {Status} {Message}", item.Space.Key, ex.StatusCode,
                        ex.Message);
                    return;
                }

                Interlocked.Add(ref m.DocsRead, page.Results.Count);

                foreach (ConfluenceDoc doc in page.Results)
                {
                    // Space for free — injected from the listing instead of expand=space.
                    doc.Space = new ConfluenceDoc.SpaceRecord
                    {
                        Id = item.Space.Id, Key = item.Space.Key, Name = item.Space.Name, Link = null,
                    };

                    var write = Stopwatch.StartNew();
                    await channel.WriteAsync(doc, token).ConfigureAwait(false);
                    blockedTicks += write.ElapsedTicks;
                }

                Interlocked.Add(ref m.WriteBlockedTicks, blockedTicks);
                url = page.Links?.Next is { } n ? Relativize(n) : null;
            }

            Interlocked.Increment(ref m.SpacesDone);
            m.SpaceMs.Add(spaceSw.Elapsed.TotalMilliseconds);
        });

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
double bytesPerDoc = m.DocsRead == 0 ? 0 : (double)m.BytesRead / m.DocsRead;

var proc = Process.GetCurrentProcess();
proc.Refresh();

Console.WriteLine();
Console.WriteLine("──────────────── PROBE SUMMARY ────────────────");
Console.WriteLine($"  wall time        : {wallSec:F1}s");
Console.WriteLine($"  docs read        : {m.DocsRead}  ({readPerSec:F0}/s)");
Console.WriteLine(
    $"  bytes read       : {m.BytesRead / 1024.0 / 1024.0:F1} MiB  ({m.BytesRead / 1024.0 / 1024.0 / wallSec:F1} MiB/s) | {bytesPerDoc / 1024.0:F1} KiB/doc");
Console.WriteLine($"  pages            : {m.Pages}  (fetch errors {m.FetchErrors})");
Console.WriteLine($"  spaces crawled   : {m.SpacesDone}/{spaces.Count}");
Console.WriteLine(
    $"  per page         : server(TTFB) avg {avgServerMs:F0}ms | transfer avg {avgTransferMs:F0}ms | parse avg {avgParseMs:F1}ms");
Console.WriteLine(
    $"  server(TTFB) dist: p50 {Percentile(m.TtfbMs, p: 0.50):F0}ms  p95 {Percentile(m.TtfbMs, p: 0.95):F0}ms  p99 {Percentile(m.TtfbMs, p: 0.99):F0}ms  max {Percentile(m.TtfbMs, p: 1.0):F0}ms");
Console.WriteLine(
    $"  space wall       : p50 {Percentile(m.SpaceMs, p: 0.50) / 1000:F1}s  p95 {Percentile(m.SpaceMs, p: 0.95) / 1000:F1}s  max {Percentile(m.SpaceMs, p: 1.0) / 1000:F1}s  (straggler skew)");
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
        ? "  READ: Confluence SERVER time (TTFB) dominates — it's compute, not transfer. The Expand set is the prime lever."
        : "  READ: BODY TRANSFER dominates — response size/bandwidth bound."
    : "  READ: parse/CPU time is significant — client CPU (JSON + HTML clean) is a real factor.");
if (m.FetchErrors > 0)
{
    Console.WriteLine(
        $"  NOTE: {m.FetchErrors} fetch error(s) — Confluence saturation/rate-limit, OR cursors bouncing nodes through the VIP. Try ConfluenceNodes to pin chains.");
}

double maxSpaceS = Percentile(m.SpaceMs, p: 1.0) / 1000;
if (maxSpaceS > wallSec * 0.5 && m.SpacesDone > 1)
{
    Console.WriteLine(
        $"  NOTE: one space took {maxSpaceS:F0}s of {wallSec:F0}s wall — STRAGGLER skew. Sub-partition big spaces (date buckets) for more parallelism.");
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
    public readonly ConcurrentBag<double> SpaceMs = [];
    public readonly ConcurrentBag<double> TtfbMs = [];
    public long BodyTicks; // body transfer + decompression
    public long BulkCount;
    public long BytesRead;
    public long DocsRead;
    public long DocsWritten;
    public long FetchErrors; // non-2xx / transport failures (saturation / rate-limit / broken cursor)
    public long Pages;
    public long ParseTicks; // deserialize + HTML clean (client CPU)
    public long SpacesDone;
    public long TtfbTicks; // Confluence server-think time
    public long WriteBlockedTicks;
    public long WriteErrors;
}

internal sealed record SearchPage(
    [property: JsonPropertyName("results")] IReadOnlyList<ConfluenceDoc> Results,
    [property: JsonPropertyName("totalSize")] long TotalSize,
    [property: JsonPropertyName("_links")] Links? Links);

internal sealed record SpaceListPage(
    [property: JsonPropertyName("results")] IReadOnlyList<SpaceRef> Results,
    [property: JsonPropertyName("_links")] Links? Links);

internal sealed record SpaceRef(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record Links([property: JsonPropertyName("next")] string? Next);
