// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;

namespace Dse.Ingestion.Events;

/// <summary>Raised by <see cref="IngestRun.Create" />; drives the run to completion on the exclusive ingest queue.</summary>
public sealed record IngestRunCreatedEvent(Guid RunId) : IDomainEvent;

public sealed class IngestRunCreatedHandler
{
    // The default 60s handler timeout would kill any real crawl. This is the hard ceiling for a single run.
    private const int ExecutionTimeoutSeconds = 6 * 60 * 60; // 6 hours

    [MessageTimeout(ExecutionTimeoutSeconds)]
    public static async Task Handle(
        IngestRunCreatedEvent message,
        IServiceProvider services,
        IIngestRunControl control,
        ILogger<IngestRunCreatedHandler> logger,
        CancellationToken ct)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<DataContext>();

        if (await db.IngestRuns.FirstOrDefaultAsync(r => r.Id == message.RunId, ct) is not { } run)
        {
            logger.LogWarning("IngestRunCreated for unknown run {RunId}; ignoring", message.RunId);
            return;
        }

        // Execute at most once: the aggregate decides whether this delivery may run, recording an interrupted
        // terminal for a re-delivered run that had already started (runs never resume).
        if (!run.TryClaimForExecution())
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var runner = sp.GetRequiredKeyedService<IIngestRunner>(run.SourceKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using IDisposable _ = control.Register(run.Id, runner, cts);

        await runner.RunAsync(run, cts.Token);
    }
}
