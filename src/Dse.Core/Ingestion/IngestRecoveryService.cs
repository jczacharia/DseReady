// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dse.Ingestion;

/// <summary>
///     On startup, finalizes any run left active by a previous process — runs never resume, so an interrupted run
///     is terminal. This frees the source's single-flight slot and corrects the read model. The pre-alias index of
///     an interrupted run is left in place (only dry runs delete their own index); nothing else is touched.
/// </summary>
public sealed class IngestRecoveryService(
    IServiceProvider services,
    ILogger<IngestRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            DataContext db = scope.ServiceProvider.GetRequiredService<DataContext>();

            // ActiveSourceKey is non-null iff the run is non-terminal.
            List<IngestRun> stranded = await db.IngestRuns.Where(r => r.ActiveSourceKey != null).ToListAsync(ct);
            if (stranded.Count == 0)
            {
                return;
            }

            foreach (IngestRun run in stranded)
            {
                run.Advance(IngestProgress.At(IngestCheckpoint.Interrupted,
                    new { reason = "Process restarted while the run was active; runs do not resume." }));
            }

            await db.SaveChangesAsync(ct);
            logger.LogWarning("Recovered {Count} interrupted ingest run(s) on startup", stranded.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a stale 'active' row is harmless (the alias still points at the last good index) and the
            // next run for that source overwrites it. Never let recovery block host startup.
            logger.LogError(ex, "Ingest run recovery failed on startup; continuing");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
