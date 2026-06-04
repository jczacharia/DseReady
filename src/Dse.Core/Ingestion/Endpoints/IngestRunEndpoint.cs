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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace Dse.Ingestion.Endpoints;

public static class IngestRunEndpoint
{
    public static RouteHandlerBuilder MapIngestEndpoints(
        this SourcePipelineBuilder builder,
        [StringSyntax("Route")] string pattern = "ingest")
    {
        return builder.MapPost(pattern, async Task<Results<AcceptedAtRoute<EntityResponse<Guid>>, ProblemHttpResult>> (
            [FromQuery] bool? dryRun,
            IDbContextOutbox<DataContext> outlet,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (dryRun == true)
            {
                if (!context.User.IsInRole(DseEntitlements.KibanaAdminOudDn) &&
                    !context.User.IsInRole(DseEntitlements.KibanaReadonlyOudDn))
                {
                    return TypedResults.Problem(AuthExtensions.InsufficientEntitlementsProblem());
                }
            }
            else
            {
                if (!context.User.IsInRole(DseEntitlements.KibanaAdminOudDn))
                {
                    return TypedResults.Problem(AuthExtensions.InsufficientEntitlementsProblem());
                }
            }

            IngestRun run = IngestRun.Create(builder.SourceKey, dryRun == true);
            outlet.DbContext.IngestRuns.Add(run);

            // SQLite SQLITE_CONSTRAINT_UNIQUE; the filtered unique index on ActiveSourceKey is what trips it.
            const int sqliteUnique = 2067;

            try
            {
                await outlet.SaveChangesAndFlushMessagesAsync(ct);
            }
            catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteExtendedErrorCode: sqliteUnique })
            {
                return context.ProblemHttpResult(HttpStatusCode.Conflict, "Active Ingest Run",
                    $"An ingest run is already active for source '{builder.SourceKey}'. Cancel it or wait for it to finish.");
            }

            return context.EntityAccepted(run.Id, $"{builder.SourceKey}-GetIngestRun", new { runId = run.Id });
        });
    }
}
