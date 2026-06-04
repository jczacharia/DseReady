// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json.Serialization;
using Dse.Auth;
using Dse.Data;
using Dse.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Dse.Ingestion.Endpoints;

/// <summary>Where a source sits right now: nothing in flight, queued for pickup, or actively crawling.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IngestState>))]
public enum IngestState
{
    Idle,
    Queued,
    Running,
}

/// <summary>A build-board view of the whole ingestion subsystem: every source, its in-flight run, and its history.</summary>
public sealed record IngestionOverview(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<SourceIngestionStatus> Sources);

public sealed record SourceIngestionStatus(
    SourceKey SourceKey,
    IngestState State,
    IngestRunSummary? Current,
    IReadOnlyList<IngestRunSummary> History);

/// <summary>A run flattened to the figures a status board cares about — the live snapshot while running.</summary>
public sealed record IngestRunSummary(
    Guid RunId,
    bool DryRun,
    IngestCheckpoint Checkpoint,
    bool IsTerminal,
    long Produced,
    long TotalToProduce,
    double PercentComplete,
    double DocsPerSecond,
    TimeSpan Elapsed,
    TimeSpan EstimatedRemaining,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

public static class IngestionOverviewEndpoint
{
    private const int HistoryLimit = 10;

    public static RouteHandlerBuilder MapIngestionOverviewEndpoint(this IEndpointRouteBuilder builder) =>
        builder.MapGet("ingestion", async Task<Ok<IngestionOverview>> (
                IEnumerable<SourceModule> modules,
                DataContext db,
                IIngestRunControl control,
                CancellationToken ct) =>
            {
                List<SourceIngestionStatus> sources = [];
                foreach (SourceModule module in modules.OrderBy(m => m.SourceKey.ToString(), StringComparer.Ordinal))
                {
                    SourceKey key = module.SourceKey;

                    IngestRun? active = await db.IngestRuns
                        .AsNoTracking()
                        .FirstOrDefaultAsync(r => r.ActiveSourceKey == key, ct);

                    // The runner keeps the live counters in memory; the persisted phase only has the figures as of
                    // its last checkpoint save. Prefer the live snapshot when the run is executing on this node.
                    IngestRunSummary? current = active is null
                        ? null
                        : Summarize(active, control.LiveSnapshot(active.Id) ?? active.CurrentProgress);

                    List<IngestRun> history = await db.IngestRuns
                        .AsNoTracking()
                        .Where(r => r.SourceKey == key && r.ActiveSourceKey == null)
                        .OrderByDescending(r => r.CreatedAt)
                        .Take(HistoryLimit)
                        .ToListAsync(ct);

                    sources.Add(new SourceIngestionStatus(
                        key,
                        StateOf(current),
                        current,
                        [.. history.Select(r => Summarize(r, r.CurrentProgress))]));
                }

                return TypedResults.Ok(new IngestionOverview(DateTimeOffset.UtcNow, sources));
            })
            .WithName("IngestionOverview")
            .RequireAuthorization(p => p.RequireKibanaReadonlyEntitlement());

    private static IngestState StateOf(IngestRunSummary? current) => current?.Checkpoint switch
    {
        null => IngestState.Idle,
        IngestCheckpoint.Queued => IngestState.Queued,
        _ => IngestState.Running,
    };

    private static IngestRunSummary Summarize(IngestRun run, IngestProgress p) => new(
        run.Id,
        run.DryRun,
        p.Checkpoint,
        p.IsTerminal,
        p.Produced,
        p.TotalToProduce,
        p.PercentComplete,
        p.DocsPerSecond,
        p.Elapsed,
        p.EstimatedRemaining,
        run.CreatedAt,
        p.CreatedAt);
}
