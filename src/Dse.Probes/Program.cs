// Copyright (c) PNC Financial Services. All rights reserved.


using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dse.ES;
using Dse.Sources.Confluence;
using Elastic.Channels;
using Elastic.Clients.Elasticsearch;
using Elastic.Ingest.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ───────────────────────────── TUNING — sweep these ─────────────────────────────
const int CrawlConcurrency = 64; // concurrent Confluence searches — SWEEP 16/64/128/256 to find the ceiling
const int PageSize = 50; // results per page (server caps body-expanded at 50) — SWEEP to expose fixed per-request overhead
const long MaxDocs = 5_000; // cap for fast iteration; use >= CrawlConcurrency*PageSize*5 for a true ceiling; 0 = whole corpus
const long StartOffset = 0; // page from here — set high (e.g. 400_000) to measure DEEP-pagination cost
const string ContentCql = "type in (page,blogpost) order by lastModified desc";

// The expand set is the prime throughput lever: each expansion is rendered per result, server-side.
// Drop entries (especially body.storage) and re-run to measure the win.
string[] Expand = ["ancestors", "body.storage", "history", "metadata.labels", "space", "version"];

const string ProbeIndex = "confluence-probe"; // throwaway target index; no alias promoted, safe to delete afterwards
const int ExportMaxItems = 5_000; // BufferOptions.OutboundBufferMaxSize (docs per bulk)
const long ExportMaxBytes = 10L * 1024 * 1024; // BufferOptions.OutboundBufferMaxBytes (byte budget per bulk)
const int ExportConcurrency = 8; // BufferOptions.ExportMaxConcurrency (concurrent bulk requests)
// ─────────────────────────────────────────────────────────────────────────────────

var configManager = new ConfigurationManager();
configManager.AddEnvironmentVariables().AddUserSecrets("dse");

HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
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

log.LogInformation(
    "PROBE confluence={Base} crawlConcurrency={Crawl} pageSize={Page} maxDocs={Max} index={Index}",
    confluence.BaseAddress, CrawlConcurrency, PageSize, MaxDocs, ProbeIndex);

// ── Confluence client: explicit, no resilience pipeline so latency is raw ──
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

string cql = Uri.EscapeDataString(ContentCql);
string expand = Uri.EscapeDataString(string.Join(separator: ',', Expand));

// record(ttfbTicks, bodyTicks, parseTicks, bytes): server-think vs wire-transfer vs client-CPU.
async Task<ConfluenceSearchResponse> FetchAsync(string url, Action<long, long, long, int>? record, CancellationToken token)
{
    var sw = Stopwatch.StartNew();
    using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
    long ttfbTicks = sw.ElapsedTicks; // request accepted -> response headers = Confluence server processing
    resp.EnsureSuccessStatusCode();

    sw.Restart();
    byte[] bytes = await resp.Content.ReadAsByteArrayAsync(token);
    long bodyTicks = sw.ElapsedTicks; // body download (+ decompression) = wire/bandwidth

    sw.Restart();
    ConfluenceSearchResponse page = JsonSerializer.Deserialize<ConfluenceSearchResponse>(
        bytes, ConfluenceDocJsonConverter.JsonSerializerOptions) ?? throw new InvalidOperationException("null response");
    long parseTicks = sw.ElapsedTicks; // deserialize + HTML clean = client CPU

    record?.Invoke(ttfbTicks, bodyTicks, parseTicks, bytes.Length);
    return page;
}

// ── How many docs to crawl ──
ConfluenceSearchResponse head = await FetchAsync($"/rest/api/content/search?cql={cql}&start=0&limit=0", record: null, ct);
long total = MaxDocs > 0 ? Math.Min(MaxDocs, head.TotalSize) : head.TotalSize;
log.LogInformation("Cluster reports {Total} matching docs; probing {Probe}", head.TotalSize, total);
if (total == 0)
{
    return;
}

// ── Elasticsearch write path: real IngestChannel, explicit BufferOptions. Ingestion is always ON —
//    we write for real, but never promote an alias, so the throwaway index is simply deleted after. ──
Uri[] uris = elastic.Endpoints().ToArray();
NodePool pool = uris.Length == 1 ? new SingleNodePool(uris[0]) : new StaticNodePool(uris);
var settings = new ElasticsearchClientSettings(pool);
if (elastic.Username is { Length: > 0 } eu && elastic.Password is { Length: > 0 } ep)
{
    settings = settings.Authentication(new BasicAuthentication(eu, ep));
}

if (!string.IsNullOrWhiteSpace(elastic.CertificateFingerprint))
{
    settings = settings.CertificateFingerprint(elastic.CertificateFingerprint);
}

if (elastic.AllowUntrustedCertificates)
{
    settings = settings.ServerCertificateValidationCallback(static (_, _, _, _) => true);
}

if (elastic.EnableHttpCompression)
{
    settings = settings.EnableHttpCompression();
}

var esClient = new ElasticsearchClient(settings);
var channel = new IngestChannel<ConfluenceDoc>(new IngestChannelOptions<ConfluenceDoc>(
    esClient.Transport, new Confluence().GetTypeContext(env), DateTimeOffset.UtcNow, ProbeIndex)
{
    BufferOptions = new BufferOptions
    {
        OutboundBufferMaxSize = ExportMaxItems,
        OutboundBufferMaxBytes = ExportMaxBytes,
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

// ── The crawl: offset-paged, mirrors today's ConfluenceIngest ──
IEnumerable<(long Start, long Limit)> partitions = Partitioner
    .Create(StartOffset, StartOffset + total, PageSize)
    .GetDynamicPartitions()
    .Select(p => (Start: p.Item1, Limit: p.Item2 - p.Item1));

try
{
    await Parallel.ForEachAsync(partitions,
        new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = CrawlConcurrency },
        async (part, token) =>
        {
            // CRITICAL: the back-pressure gate is the FIRST thing in the body, BEFORE the Confluence
            // request. If the channel is full (ES behind), we block here rather than fetch a page we'd
            // then have to hold in memory. Checking after the fetch would let pages pile up unbounded.
            // (Mirrors the WaitToWriteDocAsync gate in ConfluenceIngest — see the comment there.)
            var gate = Stopwatch.StartNew();
            if (!await channel.WaitToWriteAsync(token).ConfigureAwait(false))
            {
                log.LogWarning("Partition {Start}-{Limit} skipped waiting for channel", part.Start, part.Limit);
                return;
            }

            long blockedTicks = gate.ElapsedTicks;

            string url = $"/rest/api/content/search?cql={cql}&start={part.Start}&limit={part.Limit}&expand={expand}";
            ConfluenceSearchResponse page;
            try
            {
                page = await FetchAsync(url, (ttfb, body, parse, bytes) =>
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
                // Count, don't crash — a 429/503 here is the signal that Confluence is saturated/rate-limiting.
                Interlocked.Increment(ref m.FetchErrors);
                log.LogWarning("Fetch failed at start={Start}: {Status} {Message}", part.Start, ex.StatusCode, ex.Message);
                return;
            }

            Interlocked.Add(ref m.DocsRead, page.Results.Count);

            foreach (ConfluenceDoc doc in page.Results)
            {
                // WriteAsync also applies back-pressure (blocks while full); count that time too.
                var write = Stopwatch.StartNew();
                await channel.WriteAsync(doc, token).ConfigureAwait(false);
                blockedTicks += write.ElapsedTicks;
            }

            Interlocked.Add(ref m.WriteBlockedTicks, blockedTicks);
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
Console.WriteLine(
    $"  per page         : server(TTFB) avg {avgServerMs:F0}ms | transfer avg {avgTransferMs:F0}ms | parse avg {avgParseMs:F1}ms");
Console.WriteLine(
    $"  server(TTFB) dist: p50 {Percentile(m.TtfbMs, p: 0.50):F0}ms  p95 {Percentile(m.TtfbMs, p: 0.95):F0}ms  p99 {Percentile(m.TtfbMs, p: 0.99):F0}ms  max {Percentile(m.TtfbMs, p: 1.0):F0}ms");
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
        ? "  READ: Confluence SERVER time (TTFB) dominates — it's compute, not transfer. Trim the Expand set (drop body.storage) and re-run; that's the prime lever."
        : "  READ: BODY TRANSFER dominates — response size/bandwidth bound. Trimming expansions cuts bytes/doc; gzip is already on."
    : "  READ: parse/CPU time is significant — client CPU (JSON + HTML clean) is a real factor (distributing across pods can help).");
if (m.FetchErrors > 0)
{
    Console.WriteLine(
        $"  NOTE: {m.FetchErrors} fetch error(s) — likely Confluence rate-limiting/saturation at this concurrency. Back off CrawlConcurrency.");
}

if (m.Pages < CrawlConcurrency)
{
    Console.WriteLine(
        $"  NOTE: only {m.Pages} pages fetched (< CrawlConcurrency {CrawlConcurrency}) — concurrency was STARVED; raise MaxDocs (≥ {CrawlConcurrency * PageSize * 5}) to measure the true throughput ceiling.");
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
    public long FetchErrors; // non-2xx / transport failures (saturation / rate-limit signal)
    public long Pages;
    public long ParseTicks; // deserialize + HTML clean (client CPU)
    public long TtfbTicks; // Confluence server-think time
    public long WriteBlockedTicks;
    public long WriteErrors;
}
