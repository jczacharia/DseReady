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
    private const int ExecutionTimeoutSeconds = 6 * 60 * 60;

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
        DataContext db = sp.GetRequiredService<DataContext>();

        if (await db.IngestRuns.FirstOrDefaultAsync(r => r.Id == message.RunId, ct) is not { } run)
        {
            logger.LogWarning("IngestRunCreated for unknown run {RunId}; ignoring", message.RunId);
            return;
        }

        // Execute at most once. A terminal run is already done (or canceled while queued); a non-Queued, non-terminal
        // run was started by a prior attempt and never finished — runs never resume, so finalize it as interrupted.
        if (run.IsTerminal)
        {
            return;
        }

        if (run.CurrentProgress.Checkpoint is not IngestCheckpoint.Queued)
        {
            run.Advance(IngestProgress.At(IngestCheckpoint.Interrupted,
                new { reason = "Re-delivered after a prior attempt had started; runs do not resume." }));
            await db.SaveChangesAsync(ct);
            return;
        }

        var runner = sp.GetRequiredKeyedService<IIngestRunner>(run.SourceKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using IDisposable _ = control.Register(run.Id, runner, cts);

        await runner.RunAsync(run, cts.Token);
    }
}
