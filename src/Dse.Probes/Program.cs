// Copyright (c) PNC Financial Services. All rights reserved.
//
// Confluence ingest bottleneck probe.
//
// The crawl pipeline has three stages in series:
//
//     Confluence read  ->  client CPU (JSON parse + HTML clean)  ->  Elasticsearch write
//
// This probe measures each stage independently so you can see which one is the wall:
//   * READ side  : per-page network time vs. parse time  (Confluence-bound vs. CPU-bound)
//   * WRITE side : optional — pushes docs through the real IngestChannel and measures how
//                  long the producer spends BLOCKED waiting for the channel to accept writes.
//                  High block time => Elasticsearch is the bottleneck. ~Zero => read is.
//
// Deliberately NOT using the production resilience pipeline (Polly retries would distort raw
// latency) nor the post-configure/IngestionProfile layer (explicit BufferOptions below instead).
//
// Run:  dotnet run --project src/Dse.Probes -c Release
// Connection settings come from appsettings.json + user-secrets ("dse") + env vars.
// The knobs you actually want to sweep are all in TUNING, right here:

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
const int CrawlConcurrency = 64;     // concurrent Confluence search requests
const int PageSize = 50;             // results per search page (Confluence caps body-expanded at 50)
const long MaxDocs = 5_000;          // cap the probe run for fast iteration; 0 = whole corpus
const string ContentCql = "type in (page,blogpost) order by lastModified desc";

const bool WriteToElastic = false;   // false = pure read ceiling (start here). true = full pipeline.
const string ProbeIndex = "confluence-probe"; // throwaway target index; safe to delete afterwards
const int ExportMaxItems = 5_000;    // BufferOptions.OutboundBufferMaxSize (docs per bulk)
const long ExportMaxBytes = 10L * 1024 * 1024; // BufferOptions.OutboundBufferMaxBytes (byte budget per bulk)
const int ExportConcurrency = 8;     // BufferOptions.ExportMaxConcurrency (concurrent bulk requests)
// ─────────────────────────────────────────────────────────────────────────────────

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
IConfiguration config = builder.Configuration;
IHostEnvironment env = builder.Environment;

using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b
    .AddConfiguration(config.GetSection("Logging"))
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
ILogger log = loggerFactory.CreateLogger("ConfluenceProbe");

ConfluenceOptions confluence = config.GetSection("Confluence").Get<ConfluenceOptions>() ?? new ConfluenceOptions();
ElasticOptions elastic = config.GetSection("Elastic").Get<ElasticOptions>() ?? new ElasticOptions();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
CancellationToken ct = cts.Token;

var m = new Metrics();
using var meter = new Meter("Dse.Probes.ConfluenceIngest");
meter.CreateObservableCounter("confluence.docs.read", () => m.DocsRead);
meter.CreateObservableCounter("confluence.bytes.read", () => m.BytesRead);
meter.CreateObservableCounter("confluence.docs.written", () => m.DocsWritten);
meter.CreateObservableCounter("confluence.write.errors", () => m.WriteErrors);

log.LogInformation(
    "PROBE confluence={Base} crawlConcurrency={Crawl} pageSize={Page} maxDocs={Max} writeToElastic={Write}",
    confluence.BaseAddress, CrawlConcurrency, PageSize, MaxDocs, WriteToElastic);

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
string expand = Uri.EscapeDataString(string.Join(',', confluence.ContentExpand));

async Task<ConfluenceSearchResponse> FetchAsync(string url, Action<long, long, int>? record, CancellationToken token)
{
    var sw = Stopwatch.StartNew();
    using HttpResponseMessage resp = await http.GetAsync(url, token);
    resp.EnsureSuccessStatusCode();
    byte[] bytes = await resp.Content.ReadAsByteArrayAsync(token);
    long networkTicks = sw.ElapsedTicks;

    sw.Restart();
    ConfluenceSearchResponse page = JsonSerializer.Deserialize<ConfluenceSearchResponse>(
        bytes, ConfluenceDocJsonConverter.JsonSerializerOptions) ?? throw new InvalidOperationException("null response");
    long parseTicks = sw.ElapsedTicks;

    record?.Invoke(networkTicks, parseTicks, bytes.Length);
    return page;
}

// ── How many docs to crawl ──
ConfluenceSearchResponse head = await FetchAsync($"/rest/api/content/search?cql={cql}&start=0&limit=0", null, ct);
long total = MaxDocs > 0 ? Math.Min(MaxDocs, head.TotalSize) : head.TotalSize;
log.LogInformation("Cluster reports {Total} matching docs; probing {Probe}", head.TotalSize, total);
if (total == 0)
{
    return;
}

// ── Optional Elasticsearch write path: real IngestChannel, explicit BufferOptions ──
IngestChannel<ConfluenceDoc>? channel = null;
if (WriteToElastic)
{
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
    channel = new IngestChannel<ConfluenceDoc>(new IngestChannelOptions<ConfluenceDoc>(
        esClient.Transport, new Confluence().GetTypeContext(env), DateTimeOffset.UtcNow, indexNameOverride: ProbeIndex)
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
    log.LogInformation("Writing to throwaway index '{Index}' (delete it when done)", ProbeIndex);
}

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
var partitions = System.Collections.Concurrent.Partitioner
    .Create(0L, total, PageSize)
    .GetDynamicPartitions()
    .Select(p => (Start: p.Item1, Limit: p.Item2 - p.Item1));

try
{
    await Parallel.ForEachAsync(partitions,
        new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = CrawlConcurrency },
        async (part, token) =>
        {
            string url = $"/rest/api/content/search?cql={cql}&start={part.Start}&limit={part.Limit}&expand={expand}";
            ConfluenceSearchResponse page = await FetchAsync(url, (net, parse, bytes) =>
            {
                Interlocked.Add(ref m.NetworkTicks, net);
                Interlocked.Add(ref m.ParseTicks, parse);
                Interlocked.Add(ref m.BytesRead, bytes);
                Interlocked.Increment(ref m.Pages);
                m.NetworkMs.Add(Stopwatch.GetElapsedTime(0, net).TotalMilliseconds);
            }, token);

            Interlocked.Add(ref m.DocsRead, page.Results.Count);

            if (channel is not null)
            {
                foreach (ConfluenceDoc doc in page.Results)
                {
                    var wait = Stopwatch.StartNew();
                    if (!await channel.WaitToWriteAsync(token).ConfigureAwait(false))
                    {
                        return;
                    }
                    Interlocked.Add(ref m.WriteBlockedTicks, wait.ElapsedTicks);
                    await channel.WriteAsync(doc, token).ConfigureAwait(false);
                }
            }
        });

    if (channel is not null)
    {
        log.LogInformation("Draining channel to Elasticsearch...");
        await channel.WaitForDrainAsync(TimeSpan.FromMinutes(10), ct);
    }
}
finally
{
    cts.Cancel();
    await reporter.ContinueWith(_ => { }, TaskScheduler.Default);
    channel?.Dispose();
}

// ── Summary + verdict ──
double wallSec = totalSw.Elapsed.TotalSeconds;
double readPerSec = m.DocsRead / wallSec;
double avgNetMs = m.Pages == 0 ? 0 : Stopwatch.GetElapsedTime(0, m.NetworkTicks).TotalMilliseconds / m.Pages;
double avgParseMs = m.Pages == 0 ? 0 : Stopwatch.GetElapsedTime(0, m.ParseTicks).TotalMilliseconds / m.Pages;
double p95NetMs = Percentile(m.NetworkMs, 0.95);

Console.WriteLine();
Console.WriteLine("──────────────── PROBE SUMMARY ────────────────");
Console.WriteLine($"  wall time        : {wallSec:F1}s");
Console.WriteLine($"  docs read        : {m.DocsRead}  ({readPerSec:F0}/s)");
Console.WriteLine($"  bytes read       : {m.BytesRead / 1024.0 / 1024.0:F1} MiB  ({m.BytesRead / 1024.0 / 1024.0 / wallSec:F1} MiB/s)");
Console.WriteLine($"  pages            : {m.Pages}");
Console.WriteLine($"  per page         : network avg {avgNetMs:F0}ms (p95 {p95NetMs:F0}ms) | parse avg {avgParseMs:F1}ms");
if (WriteToElastic)
{
    double writePerSec = m.DocsWritten / wallSec;
    double blockSec = Stopwatch.GetElapsedTime(0, m.WriteBlockedTicks).TotalSeconds;
    Console.WriteLine($"  docs written     : {m.DocsWritten}  ({writePerSec:F0}/s)  bulks {m.BulkCount}  errors {m.WriteErrors}");
    Console.WriteLine($"  producer blocked : {blockSec:F1}s waiting on the channel ({blockSec / wallSec * 100:F0}% of wall)");
    Console.WriteLine(blockSec / wallSec > 0.25
        ? "  VERDICT: Elasticsearch WRITE is the bottleneck (producer spends much of its time blocked)."
        : "  VERDICT: write keeps up — bottleneck is on the READ side, see below.");
}
Console.WriteLine(avgNetMs > avgParseMs * 3
    ? "  READ: dominated by Confluence NETWORK time — the source/server is the wall (more pods won't help)."
    : "  READ: parse/CPU time is significant — client CPU (JSON + HTML clean) is a real factor (distributing across pods can help).");
Console.WriteLine("────────────────────────────────────────────────");

return;

static double Percentile(System.Collections.Concurrent.ConcurrentBag<double> samples, double p)
{
    double[] sorted = [.. samples];
    if (sorted.Length == 0)
    {
        return 0;
    }
    Array.Sort(sorted);
    int idx = Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
    return sorted[idx];
}

internal sealed class Metrics
{
    public long DocsRead;
    public long BytesRead;
    public long Pages;
    public long NetworkTicks;
    public long ParseTicks;
    public long DocsWritten;
    public long BulkCount;
    public long WriteErrors;
    public long WriteBlockedTicks;
    public readonly System.Collections.Concurrent.ConcurrentBag<double> NetworkMs = [];
}
