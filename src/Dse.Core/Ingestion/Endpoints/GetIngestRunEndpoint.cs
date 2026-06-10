// Copyright (c) PNC Financial Services. All rights reserved.


using System.Net;
using Dse.Auth;
using Dse.Data;
using Dse.Shared;
using Dse.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Dse.Ingestion.Endpoints;

/// <summary>Status of a run: its identity, its full phase history, and a live snapshot while it is running.</summary>
public sealed record IngestRunStatus(
    Guid Id,
    SourceKey SourceKey,
    bool DryRun,
    IReadOnlyList<IngestProgress> Phases,
    IngestProgress? LiveSnapshot);

public static class GetIngestRunEndpoint
{
    public static RouteHandlerBuilder MapGetIngestRunEndpoint(this SourcePipelineBuilder builder)
    {
        SourceKey sourceKey = builder.SourceKey;

        return builder.MapGet("ingest/{runId:guid}", async Task<Results<Ok<IngestRunStatus>, ProblemHttpResult>> (
                Guid runId,
                HttpContext context,
                DataContext db,
                IIngestRunControl control,
                CancellationToken ct) =>
            {
                if (await db.IngestRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct) is not { } run)
                {
                    return context.ProblemHttpResult(HttpStatusCode.NotFound, "Not Found", $"IngestRun {runId} not found");
                }

                // Live numbers exist only while the run is executing on this node; a terminal run's final figures
                // live in its terminal phase's snapshot.
                IngestProgress? live = run.IsTerminal ? null : control.LiveSnapshot(runId);
                return TypedResults.Ok(new IngestRunStatus(run.Id, run.SourceKey, run.DryRun, run.Phases, live));
            })
            .WithName($"{sourceKey}-GetIngestRun")
            .RequireAuthorization(p => p.RequireKibanaReadonlyEntitlement());
    }
}
