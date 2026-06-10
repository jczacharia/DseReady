// Copyright (c) PNC Financial Services. All rights reserved.


using System.Net;
using Dse.Auth;
using Dse.Data;
using Dse.Shared;
using Dse.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace Dse.Ingestion.Endpoints;

public static class IngestRunEndpoint
{
    public static RouteHandlerBuilder MapIngestEndpoint(this SourcePipelineBuilder builder) =>
        builder.MapPost("ingest", async Task<Results<AcceptedAtRoute<EntityResponse<Guid>>, ProblemHttpResult>> (
                IDbContextOutbox<DataContext> outlet,
                HttpContext context,
                CancellationToken ct) => await Map(builder, dryRun: false, outlet, context, ct))
            .RequireAuthorization(p => p.RequireAnyEntitlements(DseEntitlements.KibanaAdminOudDn));

    public static RouteHandlerBuilder MapDryIngestEndpoint(this SourcePipelineBuilder builder) =>
        builder.MapPost("ingest/dry", async Task<Results<AcceptedAtRoute<EntityResponse<Guid>>, ProblemHttpResult>> (
                IDbContextOutbox<DataContext> outlet,
                HttpContext context,
                CancellationToken ct) => await Map(builder, dryRun: true, outlet, context, ct))
            .RequireAuthorization(p => p.RequireAuthenticatedUser());

    private static async Task<Results<AcceptedAtRoute<EntityResponse<Guid>>, ProblemHttpResult>> Map(
        SourcePipelineBuilder builder,
        bool dryRun,
        IDbContextOutbox<DataContext> outlet,
        HttpContext context,
        CancellationToken ct)
    {
        var run = IngestRun.Create(builder.SourceKey, dryRun);
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
    }
}
