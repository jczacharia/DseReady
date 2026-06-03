// Copyright (c) PNC Financial Services. All rights reserved.


using System.Net;
using Dse.Auth;
using Dse.Data;
using Dse.Shared;
using Dse.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace Dse.Ingestion;

/// <summary>Read DTO for an <see cref="IngestRunEvent" /> row — payload is the typed polymorphic record.</summary>
public sealed record IngestRunEventDto(long Seq, DateTimeOffset At, IngestEventPayload Payload);

public sealed class IngestRunEndpoints(IEnumerable<SourceModule> sourceModules) : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder builder)
    {
        RouteGroupBuilder group = builder.MapGroup("sources");

        foreach (SourceModule module in sourceModules)
        {
            SourceKey sourceKey = module.SourceKey;

            group.MapPost($"{sourceKey}/ingest/full", async (
                    IDbContextOutbox<DataContext> outbox,
                    HttpContext context,
                    CancellationToken ct) =>
                {
                    var run = IngestRun.Create(sourceKey, dryRun: false);
                    outbox.DbContext.IngestRuns.Add(run);
                    await outbox.DbContext.AppendAsync(run, new IngestEventPayload.Queued(), ct);

                    await outbox.PublishAsync(new IngestRunCreated(run.Id));
                    await outbox.SaveChangesAndFlushMessagesAsync(ct);

                    return context.EntityAccepted<Guid, IngestRun>(run, $"{sourceKey}-GetIngestRun", new { runId = run.Id });
                })
                .RequireAuthorization(p => p.RequireKibanaAdminEntitlement());


            group.MapPost($"{sourceKey}/ingest/dry", async (
                    IDbContextOutbox<DataContext> outbox,
                    HttpContext context,
                    CancellationToken ct) =>
                {
                    var run = IngestRun.Create(sourceKey, dryRun: true);
                    outbox.DbContext.IngestRuns.Add(run);
                    await outbox.DbContext.AppendAsync(run, new IngestEventPayload.Queued(), ct);

                    await outbox.PublishAsync(new IngestRunCreated(run.Id));
                    await outbox.SaveChangesAndFlushMessagesAsync(ct);

                    return context.EntityAccepted<Guid, IngestRun>(run, $"{sourceKey}-GetIngestRun", new { runId = run.Id });
                })
                .RequireAuthorization(p => p.RequireKibanaReadonlyEntitlement());


            group.MapGet($"{sourceKey}/ingest/{{runId:guid}}", async Task<Results<ProblemHttpResult, Ok<IngestRun>>> (
                    Guid runId,
                    HttpContext context,
                    IDbContextOutbox<DataContext> outbox,
                    CancellationToken ct) =>
                {
                    if (await outbox.DbContext.IngestRuns.FirstOrDefaultAsync(r => r.Id == runId, ct) is not { } run)
                    {
                        return context.ProblemHttpResult(HttpStatusCode.NotFound,
                            "Not Found", $"IngestRun with id {runId} not found");
                    }

                    return TypedResults.Ok(run);
                })
                .WithName($"{sourceKey}-GetIngestRun")
                .RequireAuthorization(p => p.RequireKibanaReadonlyEntitlement());

            group.MapGet($"{sourceKey}/ingest/{{runId:guid}}/events",
                    async Task<Results<ProblemHttpResult, Ok<IReadOnlyList<IngestRunEventDto>>>> (
                        Guid runId,
                        HttpContext context,
                        IDbContextOutbox<DataContext> outbox,
                        CancellationToken ct) =>
                    {
                        if (!await outbox.DbContext.IngestRuns.AnyAsync(r => r.Id == runId, ct))
                        {
                            return context.ProblemHttpResult(HttpStatusCode.NotFound,
                                "Not Found", $"IngestRun with id {runId} not found");
                        }

                        List<IngestRunEvent> rows = await outbox.DbContext.IngestRunEvents
                            .Where(e => e.RunId == runId)
                            .OrderBy(e => e.Seq)
                            .ToListAsync(ct);

                        IReadOnlyList<IngestRunEventDto> dtos = rows
                            .ConvertAll(e => new IngestRunEventDto(e.Seq, e.At, e.Deserialize()));

                        return TypedResults.Ok(dtos);
                    })
                .WithName($"{sourceKey}-GetIngestRunEvents")
                .RequireAuthorization(p => p.RequireKibanaReadonlyEntitlement());
        }
    }
}
