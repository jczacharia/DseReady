// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dse.Ingestion;

/// <summary>
///     Entry point for an ingestion run. Loads the freshly-created <see cref="IngestRun" />, resolves the
///     source-specific <see cref="IIngestRunner" /> by <see cref="IngestRun.SourceKey" />, and runs it.
///     Each phase transition persists an <see cref="IngestRunEvent" /> and broadcasts a live update.
/// </summary>
public sealed class IngestRunCreatedHandler
{
    public static async Task Handle(
        IngestRunCreated message,
        DataContext db,
        IServiceProvider services,
        ILogger<IngestRunCreatedHandler> logger,
        CancellationToken ct)
    {
        if (await db.IngestRuns.FirstOrDefaultAsync(r => r.Id == message.RunId, ct) is not { } run)
        {
            throw new InvalidOperationException($"Ingest run with ID {message.RunId} not found");
        }

        if (run.IsTerminal)
        {
            logger.LogInformation("Skipping IngestRunCreated for terminal run {RunId} (phase={Phase})",
                run.Id, run.Phase);
            return;
        }

        if (services.GetKeyedService<IIngestRunner>(run.SourceKey) is not { } runner)
        {
            await db.AppendAsync(run,
                new IngestEventPayload.Failed($"No IIngestRunner registered for source '{run.SourceKey}'."), ct);
            return;
        }

        await runner.RunAsync(run, ct);
    }
}
