// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics;
using Dse.Data;
using Dse.ES;
using Dse.Shared;
using Dse.Sources;
using Elastic.Channels;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Ingest.Elasticsearch;
using Elastic.Mapping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dse.Ingestion;

/// <summary>
///     Source-specific runner: instantiates an <see cref="IngestChannel{TDoc}" />, drives the
///     <see cref="IIngest{TDoc}" /> pipeline, and translates each phase into an
///     <see cref="IngestEventPayload" /> persisted via <see cref="IngestRunWriter.AppendAsync" />.
/// </summary>
public sealed class IngestRunner<TDoc>(
    DataContext db,
    IIngest<TDoc> ingest,
    ElasticStartupService elasticStartup,
    ElasticsearchClient client,
    IngestProgressBroadcaster broadcaster,
    ILoggerFactory loggerFactory,
    IServiceProvider services) : IIngestRunner where TDoc : class
{
    // Guards _currentIngestingPayload — the latest snapshot for the in-flight Ingesting tick. The per-second
    // timer and the main pipeline both mutate it from different threads, so a single locked field is safer than
    // a plain reference.
    private readonly object _gate = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger($"{typeof(TDoc).GetAssemblySourceKey()}Ingestor");
    private readonly Stopwatch _stopwatch = new();

    private readonly ElasticsearchTypeContext _typeContext = services
        .GetRequiredKeyedService<ElasticsearchTypeContext>(typeof(TDoc).GetAssemblySourceKey());

    private IngestEventPayload? _currentIngestingPayload;

    // Non-zero if any document failed to index. A non-zero count fails the run and skips alias promotion so an
    // incomplete index is never served behind the live search alias.
    private long _failedDocuments;

    private long _produced;

    public SourceKey SourceKey => typeof(TDoc).GetAssemblySourceKey();

    public long Produced => Interlocked.Read(ref _produced);

    public TimeSpan Elapsed => _stopwatch.Elapsed;
    public long TotalToProduce { get; private set; }
    public double PercentComplete => TotalToProduce > 0 ? Math.Min(100d, (double)Produced / TotalToProduce * 100d) : 0d;
    public double DocsPerSecond => Elapsed.TotalSeconds <= 0 ? 0d : Produced / Elapsed.TotalSeconds;

    public TimeSpan EstimatedRemaining => DocsPerSecond switch
    {
        <= 0 => TimeSpan.MaxValue,
        _ when Produced >= TotalToProduce => TimeSpan.Zero,
        var dps => TimeSpan.FromSeconds((TotalToProduce - Produced) / dps),
    };

    public async Task RunAsync(IngestRun run, CancellationToken ct)
    {
        if (!run.SourceKey.Equals(SourceKey))
        {
            throw new InvalidOperationException(
                $"IngestRunner<{typeof(TDoc).Name}> was invoked for a run of source '{run.SourceKey}', " +
                $"but this runner serves '{SourceKey}'.");
        }

        bool dryRun = run.DryRun;
        ElasticStartupData esData = elasticStartup.Data;

        try
        {
            BufferOptions bufferOptions = ingest.BufferOptions;
            bufferOptions.ExportMaxConcurrency = bufferOptions.ExportMaxConcurrency is { } exportMaxConcurrency
                ? Math.Min(esData.MaxChannelConcurrency, exportMaxConcurrency)
                : esData.MaxChannelConcurrency;

            using IngestChannel<TDoc> channel = new(new IngestChannelOptions<TDoc>(
                client.Transport,
                _typeContext,
                _typeContext.IndexPatternUseBatchDate ? DateTimeOffset.UtcNow : null)
            {
                BufferOptions = bufferOptions,
                ExportItemsAttemptCallback = (retries, count) =>
                {
                    if (retries == 0)
                    {
                        Interlocked.Add(ref _produced, count);
                    }
                },
                // Without these, ES bulk errors / dropped docs are invisible and the run would still alias and
                // report Succeeded over an incomplete index. Any failure here fails the run (see the gate below).
                ExportResponseCallback = (response, _) =>
                {
                    if (response is { Items: { } items }
                        && items.Count(i => i.Error is not null) is var itemErrors and > 0)
                    {
                        Interlocked.Add(ref _failedDocuments, itemErrors);
                        _logger.LogError("Bulk export reported {ErrorCount} item error(s) (e.g. '{Reason}')",
                            itemErrors, items.FirstOrDefault(i => i.Error is not null)?.Error?.Reason);
                    }

                    if (response.Error is not null)
                    {
                        Interlocked.Increment(ref _failedDocuments);
                        _logger.LogError("Bulk export returned a top-level error: {Reason}", response.Error.Reason);
                    }
                },
                ExportMaxRetriesCallback = droppedItems =>
                {
                    Interlocked.Add(ref _failedDocuments, droppedItems.Count);
                    _logger.LogError("{Count} document(s) dropped after exhausting export retries", droppedItems.Count);
                },
                ExportExceptionCallback = exportException =>
                {
                    Interlocked.Increment(ref _failedDocuments);
                    _logger.LogError(exportException, "Bulk export threw an exception");
                },
            });

            await channel.BootstrapElasticsearchAsync(BootstrapMethod.Failure, ct);
            await Advance(run, new IngestEventPayload.Bootstrapped(
                channel.IndexName,
                channel.BatchExportSize,
                channel.DrainSize,
                channel.MaxConcurrency), ct);

            long totalToProduce = await ingest.GetDesiredTotalToProduceAsync(ct);
            TotalToProduce = dryRun ? Math.Clamp(totalToProduce, 0, 1) : totalToProduce;
            await Advance(run, new IngestEventPayload.TotalMeasured(TotalToProduce), ct);

            IngestContext<TDoc> context = new(channel, TotalToProduce);
            _stopwatch.Start();

            await Advance(run, new IngestEventPayload.Ingesting(Snapshot()), ct);

            await using (TimerWorker.StartNew(
                             async () =>
                             {
                                 IngestEventPayload.Ingesting? tick = null;
                                 lock (_gate)
                                 {
                                     // Re-check under the lock so a tick can't overwrite a newer (e.g. Draining) state.
                                     if (_currentIngestingPayload is IngestEventPayload.Ingesting)
                                     {
                                         var next = new IngestEventPayload.Ingesting(Snapshot());
                                         _currentIngestingPayload = next;
                                         tick = next;
                                     }
                                 }

                                 if (tick is not null)
                                 {
                                     await PersistAndPublish(run, tick, ct);
                                 }
                             },
                             ex => _logger.LogError(ex, "Progress reporting tick failed; continuing without it")))
            {
                await ingest.IngestAsync(context, ct);
            }

            await Advance(run, new IngestEventPayload.Draining(Snapshot()), ct);

            if (!await channel.WaitForDrainAsync(null, ct))
            {
                await Advance(run, new IngestEventPayload.Failed("Channel timed out while waiting for drain"), ct);
                return;
            }

            if (dryRun)
            {
                try
                {
                    DeleteIndexResponse deleteResponse = await client.Indices.DeleteAsync(channel.IndexName, ct);
                    if (!deleteResponse.IsSuccess())
                    {
                        throw new InvalidOperationException(
                            $"Elasticsearch responded with {deleteResponse.DebugInformation} " +
                            $"when attempting to delete transient index: {channel.IndexName}");
                    }
                }
                catch (OperationCanceledException)
                {
                    /* transient-index delete is best-effort; cancellation is expected */
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to delete transient index '{IndexName}'; manual cleanup may be required",
                        channel.IndexName);
                }
            }

            // Gate: if any document failed to index, fail the run. For a full run this means the freshly built
            // index is NOT promoted to the live alias — we never serve search over an incomplete index.
            long failedDocuments = Interlocked.Read(ref _failedDocuments);
            if (failedDocuments > 0)
            {
                await Advance(run,
                    new IngestEventPayload.Failed($"{failedDocuments} document(s) failed to index; alias not promoted."),
                    ct);
                return;
            }

            if (!dryRun)
            {
                await Advance(run, new IngestEventPayload.Aliasing(Snapshot()), ct);
                if (!await channel.ApplyAliasesAsync(channel.IndexName, ct))
                {
                    await Advance(run, new IngestEventPayload.Failed("Alias application failed"), ct);
                    return;
                }
            }

            await Advance(run, new IngestEventPayload.Succeeded(Snapshot()), ct);
        }
        catch (OperationCanceledException e)
        {
            // Use CancellationToken.None for the terminal write so the cancel reason actually lands in the log.
            await Advance(run, new IngestEventPayload.Canceled("Canceled: " + e.Message), CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Ingest run failed with an exception");
            await Advance(run, new IngestEventPayload.Faulted(ExceptionDto.From(e)), CancellationToken.None);
        }
    }

    private async Task Advance(IngestRun run, IngestEventPayload payload, CancellationToken ct)
    {
        lock (_gate)
        {
            _currentIngestingPayload = payload is IngestEventPayload.Ingesting ? payload : null;
        }

        await PersistAndPublish(run, payload, ct);
    }

    private async Task PersistAndPublish(IngestRun run, IngestEventPayload payload, CancellationToken ct)
    {
        _logger.LogInformation("{@IngestEvent}", payload);

        if (_logger.IsEnabled(LogLevel.Debug) && payload is IngestEventPayload.IWithSnapshot withSnapshot)
        {
            _logger.LogDebug("{IngestReportSnapshotPretty}", withSnapshot.Snapshot.PrettyPrint());
        }

        IngestRunEvent evt = await db.AppendAsync(run, payload, ct);
        await db.SaveChangesAsync(ct);

        broadcaster.Publish(new IngestProgressEvent(run.Id, evt.Seq, evt.At, payload));
    }

    private IngestReportSnapshot Snapshot() => new(
        TotalToProduce,
        Elapsed,
        PercentComplete,
        DocsPerSecond,
        EstimatedRemaining,
        Produced,
        GC.GetTotalMemory(false),
        Environment.WorkingSet,
        DateTimeOffset.UtcNow);
}
