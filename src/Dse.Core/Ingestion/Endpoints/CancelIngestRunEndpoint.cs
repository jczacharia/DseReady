// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
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

public static class CancelIngestRunEndpoint
{
    public static RouteHandlerBuilder MapCancelIngestRunEndpoint(
        this SourcePipelineBuilder builder,
        [StringSyntax("Route")] string pattern = "ingest/{runId:guid}/cancel") =>
        builder.MapPost(pattern, async Task<Results<Accepted, ProblemHttpResult>> (
                Guid runId,
                HttpContext context,
                DataContext db,
                IIngestRunControl control,
                CancellationToken ct) =>
            {
                if (await db.IngestRuns.FirstOrDefaultAsync(r => r.Id == runId, ct) is not { } run)
                {
                    return context.ProblemHttpResult(HttpStatusCode.NotFound, "Not Found", $"IngestRun {runId} not found");
                }

                if (run.IsTerminal)
                {
                    return context.ProblemHttpResult(HttpStatusCode.Conflict,
                        "Conflict", $"IngestRun {runId} already finished; nothing to cancel.");
                }

                // Authoritative + force: record the terminal Canceled first so the source's single-flight slot is
                // freed even if the pipeline is wedged, then signal cooperative cancellation so a responsive run
                // unwinds promptly.
                run.Advance(IngestProgress.At(IngestCheckpoint.Canceled, new { reason = "Canceled via API." }));
                await db.SaveChangesAsync(ct);
                control.SignalCancellation(runId);

                return TypedResults.Accepted((string?)null);
            })
            .RequireAuthorization(p => p.RequireKibanaAdminEntitlement());
}
