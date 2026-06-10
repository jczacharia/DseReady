// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics;
using System.Text.Json;
using Dse.Data;
using Dse.Shared;
using Dse.Sources;
using Elastic.Channels;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Ingest.Elasticsearch;
using Elastic.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dse.Ingestion;

public sealed class IngestRunner<TDoc>(
    DataContext db,
    IIngest<TDoc> ingest,
    ElasticsearchClient client,
    ILoggerFactory loggerFactory,
    IOptionsMonitor<BufferOptions> bufferOptions,
    IServiceProvider services) : IIngestRunner where TDoc : class
{
    private readonly ILogger _logger = loggerFactory.CreateLogger($"{typeof(TDoc).GetRequiredSourceKey()}Ingestor");
    private readonly Stopwatch _stopwatch = new();

    private readonly ElasticsearchTypeContext _typeContext = services
        .GetRequiredKeyedService<ElasticsearchTypeContext>(typeof(TDoc).GetRequiredSourceKey());

    private IngestCheckpoint _checkpoint = IngestCheckpoint.Queued;

    // Non-zero fails the run and skips alias promotion so an incomplete index is never aliased.
    private long _failedDocuments;
    private long _produced;

    public long Produced => Interlocked.Read(ref _produced);
    public TimeSpan Elapsed => _stopwatch.Elapsed;
    public long TotalToProduce { get; private set; }
    public double PercentComplete => TotalToProduce > 0 ? Math.Min(val1: 100d, (double)Produced / TotalToProduce * 100d) : 0d;
    public double DocsPerSecond => Elapsed.TotalSeconds <= 0 ? 0d : Produced / Elapsed.TotalSeconds;

    public TimeSpan EstimatedRemaining => DocsPerSecond switch
    {
        <= 0 => TimeSpan.MaxValue,
        _ when Produced >= TotalToProduce => TimeSpan.Zero,
        var dps => TimeSpan.FromSeconds((TotalToProduce - Produced) / dps),
    };

    public SourceKey SourceKey => typeof(TDoc).GetRequiredSourceKey();

    // Read live by the status endpoint while the run is in-flight; counters are Interlocked/Stopwatch, safe to read.
    public IngestProgress CurrentSnapshot => Progress(_checkpoint);

    public async Task RunAsync(IngestRun run, CancellationToken ct)
    {
        if (!run.SourceKey.Equals(SourceKey))
        {
            throw new InvalidOperationException(
                $"IngestRunner<{typeof(TDoc).Name}> was invoked for a run of source '{run.SourceKey}', " +
                $"but this runner serves '{SourceKey}'.");
        }

        try
        {
            // Claim: moves the run off Queued so a post-crash redelivery is recognized as interrupted, not restarted.
            await AdvanceAsync(run, IngestCheckpoint.Started, ct, new { run.DryRun });

            BufferOptions bufOpts = bufferOptions.Get(SourceKey);

            using IngestChannel<TDoc> channel = new(new IngestChannelOptions<TDoc>(
                client.Transport,
                _typeContext,
                _typeContext.IndexPatternUseBatchDate ? DateTimeOffset.UtcNow : null)
            {
                BufferOptions = bufOpts,
                ExportItemsAttemptCallback = (retries, count) =>
                {
                    if (retries == 0)
                    {
                        Interlocked.Add(ref _produced, count);
                    }
                },
                // Surface bulk/per-item errors so the gate below fails the run instead of aliasing junk.
                ExportResponseCallback = (response, _) =>
                {
                    if (response is { Items: { } items } && items.Count(i => i.Error is not null) is var itemErrors and > 0)
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
            await AdvanceAsync(run, IngestCheckpoint.Bootstrapped, ct,
                new { channel.IndexName, channel.BatchExportSize, channel.DrainSize, channel.MaxConcurrency });

            long totalToProduce = await ingest.GetDesiredTotalToProduceAsync(ct);
            TotalToProduce = run.DryRun ? Math.Clamp(totalToProduce, min: 0, max: 1) : totalToProduce;
            await AdvanceAsync(run, IngestCheckpoint.TotalMeasured, ct, new { Total = TotalToProduce });

            IngestContext<TDoc> context = new(channel, TotalToProduce);
            _stopwatch.Start();

            await AdvanceAsync(run, IngestCheckpoint.Ingesting, ct);
            await ingest.IngestAsync(context, ct);

            await AdvanceAsync(run, IngestCheckpoint.Draining, ct);
            if (!await channel.WaitForDrainAsync(TimeSpan.FromHours(1), ct))
            {
                await AdvanceAsync(run, IngestCheckpoint.Failed, ct,
                    new { reason = "Channel timed out while waiting for drain" });
                return;
            }

            if (run.DryRun)
            {
                await DeleteTransientIndexAsync(channel.IndexName, ct);
            }

            if (Interlocked.Read(ref _failedDocuments) is var failed and > 0)
            {
                await AdvanceAsync(run, IngestCheckpoint.Failed, ct,
                    new { reason = $"{failed} document(s) failed to index; alias not promoted." });
                return;
            }

            // Last gate before the only visible side effect: if the run was canceled out from under us, never
            // promote the alias. Searchers keep seeing the previous good index.
            if (!run.DryRun)
            {
                if (!await StillActiveAsync(run))
                {
                    _logger.LogWarning("Run {RunId} was finalized before aliasing; skipping alias promotion", run.Id);
                    return;
                }

                await AdvanceAsync(run, IngestCheckpoint.Aliasing, ct);
                if (!await channel.ApplyAliasesAsync(channel.IndexName, ct))
                {
                    await AdvanceAsync(run, IngestCheckpoint.Failed, ct, new { reason = "Alias application failed" });
                    return;
                }

                try
                {
                    await channel.RefreshAsync(CancellationToken.None);
                }
                catch
                {
                    // ignored
                }
            }

            await AdvanceAsync(run, IngestCheckpoint.Succeeded, ct);
        }
        catch (OperationCanceledException)
        {
            // Interrupted, not Canceled: a user cancel records its own terminal via the API; reaching here means
            // shutdown or the execution timeout fired. Defer to the API if it already finalized the run.
            if (await StillActiveAsync(run))
            {
                await AdvanceAsync(run, IngestCheckpoint.Interrupted, ct,
                    new { reason = "Ingestion was interrupted (shutdown or execution timeout)." });
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Ingest run failed with an exception");
            if (await StillActiveAsync(run))
            {
                await AdvanceAsync(run, IngestCheckpoint.Faulted, ct, new { exception = ExceptionDto.From(e) });
            }
        }
    }

    private async Task DeleteTransientIndexAsync(string indexName, CancellationToken ct)
    {
        try
        {
            DeleteIndexResponse deleteResponse = await client.Indices.DeleteAsync(indexName, ct);
            if (!deleteResponse.IsSuccess())
            {
                throw new InvalidOperationException(
                    $"Elasticsearch responded with {deleteResponse.DebugInformation} " +
                    $"when attempting to delete transient index: {indexName}");
            }
        }
        catch (OperationCanceledException)
        {
            /* transient-index delete is best-effort; cancellation is expected */
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete transient dry-run index '{IndexName}'; manual cleanup may be required", indexName);
        }
    }

    // Asks the database directly whether this run still owns its single-flight slot, so the runner defers to a
    // terminal another writer (the cancel endpoint) may have just committed instead of overwriting it. A
    // no-tracking query reads committed state without depending on refreshing the tracked graph or its
    // navigations; ActiveSourceKey is nulled the instant any terminal is reached.
    private Task<bool> StillActiveAsync(IngestRun run) =>
        db.IngestRuns.AsNoTracking().AnyAsync(r => r.Id == run.Id && r.ActiveSourceKey != null);

    private async Task SaveAsync(IngestRun run, CancellationToken ct)
    {
        _logger.LogInformation("Ingest {RunId}: {Phase}", run.Id, run.CurrentProgress.GetType().Name);
        await db.SaveChangesAsync(ct);
    }

    // Advance the run one checkpoint and persist it. A terminal checkpoint saves with a fresh token: the run may be
    // reaching that terminal *because* it was canceled, and the outcome must still be recorded.
    private async Task AdvanceAsync(IngestRun run, IngestCheckpoint checkpoint, CancellationToken ct, object? metadata = null)
    {
        _checkpoint = checkpoint;
        IngestProgress progress = Progress(checkpoint, metadata);
        run.Advance(progress);
        await SaveAsync(run, progress.IsTerminal ? CancellationToken.None : ct);
    }

    private IngestProgress Progress(IngestCheckpoint checkpoint, object? metadata = null) => new(Guid.NewGuid())
    {
        Checkpoint = checkpoint,
        TotalToProduce = TotalToProduce,
        Elapsed = Elapsed,
        PercentComplete = PercentComplete,
        DocsPerSecond = DocsPerSecond,
        EstimatedRemaining = EstimatedRemaining,
        Produced = Produced,
        ManagedMemoryBytes = GC.GetTotalMemory(forceFullCollection: false),
        WorkingMemoryBytes = Environment.WorkingSet,
        Metadata = metadata is null
            ? JsonDocument.Parse("{}")
            : JsonSerializer.SerializeToDocument(metadata, JsonDefaults.Web),
    };
}
