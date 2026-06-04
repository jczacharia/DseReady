// Copyright (c) PNC Financial Services. All rights reserved.


using AwesomeAssertions;
using Dse.Data;
using Dse.Ingestion;
using Dse.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dse.Tests.Ingestion;

/// <summary>
///     The aggregate's EF round-trip in isolation — no Elasticsearch, no Wolverine. Reproduces the persistence
///     path the runner walks (load → advance → save, repeatedly, through a terminal checkpoint) so a mapping
///     fault surfaces here, plainly, instead of buried under the durable pipeline.
/// </summary>
public sealed class IngestRunPersistenceTests(ITestOutputHelper toh, TestFixture fixture) : TestBed(toh, fixture)
{
    private SourceKey Confluence => Sources.Single(m => m.SourceKey.ToString() == "confluence").SourceKey;

    [Fact]
    public async Task Advancing_a_loaded_run_through_a_terminal_checkpoint_persists()
    {
        Guid id;
        await using (AsyncServiceScope scope = Services.CreateAsyncScope())
        {
            DataContext db = scope.ServiceProvider.GetRequiredService<DataContext>();
            IngestRun run = IngestRun.Create(Confluence);
            db.IngestRuns.Add(run);
            await db.SaveChangesAsync(Ct);
            id = run.Id;
        }

        await using (AsyncServiceScope scope = Services.CreateAsyncScope())
        {
            DataContext db = scope.ServiceProvider.GetRequiredService<DataContext>();
            IngestRun run = await db.IngestRuns.FirstAsync(r => r.Id == id, Ct);

            run.Advance(IngestProgress.At(IngestCheckpoint.Started));
            await SaveDiagnosed(db);

            run.Advance(IngestProgress.At(IngestCheckpoint.Succeeded));
            await SaveDiagnosed(db);
        }

        await using (AsyncServiceScope scope = Services.CreateAsyncScope())
        {
            DataContext db = scope.ServiceProvider.GetRequiredService<DataContext>();
            IngestRun run = await db.IngestRuns.AsNoTracking().FirstAsync(r => r.Id == id, Ct);
            run.CurrentProgress.Checkpoint.Should().Be(IngestCheckpoint.Succeeded);
            run.IsTerminal.Should().BeTrue();
            run.ActiveSourceKey.Should().BeNull();
        }
    }

    private async Task SaveDiagnosed(DataContext db)
    {
        try
        {
            await db.SaveChangesAsync(Ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                Out.WriteLine($"CONFLICT entity={entry.Entity.GetType().Name} state={entry.State}");
                foreach (var p in entry.Properties)
                {
                    Out.WriteLine(
                        $"  {p.Metadata.Name}: current='{p.CurrentValue}' original='{p.OriginalValue}' modified={p.IsModified} token={p.Metadata.IsConcurrencyToken}");
                }
            }

            throw;
        }
    }
}
