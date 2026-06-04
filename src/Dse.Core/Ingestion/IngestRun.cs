// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Data;
using Dse.Ingestion.Events;
using Dse.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dse.Ingestion;

/// <summary>
///     An ingest run. The aggregate owns its ordered <see cref="Phases" /> — the history is the state. Callers build
///     an <see cref="IngestProgress" /> and <see cref="Advance" /> the run with it; reaching a terminal checkpoint
///     releases the source's single-flight slot. Creating a run raises <see cref="IngestRunCreatedEvent" />, which
///     the EF transactional middleware publishes through Wolverine on save.
/// </summary>
public sealed class IngestRun : Entity
{
    private IngestRun() { }

    public Source Source { get; init; } = null!;
    public SourceKey SourceKey { get; init; } = null!;
    public bool DryRun { get; init; }

    // Mirrors SourceKey while active, NULL once terminal. A filtered unique index on this column enforces one
    // active run per source; the endpoint maps the violation to 409.
    public SourceKey? ActiveSourceKey { get; private set; }

    public List<IngestProgress> Phases { get; private init; } = [];

    public IngestProgress CurrentProgress => Phases.OrderBy(p => p.CreatedAt).Last();
    public bool IsTerminal => CurrentProgress.IsTerminal;

    public static IngestRun Create(SourceKey sourceKey, bool dryRun = false)
    {
        IngestRun run = new()
        {
            SourceKey = sourceKey,
            ActiveSourceKey = sourceKey,
            DryRun = dryRun,
            Phases = [new IngestProgress { Checkpoint = IngestCheckpoint.Queued }],
        };

        run.Publish(new IngestRunCreatedEvent(run.Id));
        return run;
    }

    // Once terminal a run never moves again, and it never moves backward — late or out-of-order transitions
    // (e.g. the runner finishing after an API cancel) are no-ops rather than errors.
    public void Advance(IngestProgress progress)
    {
        if (IsTerminal)
        {
            return;
        }

        Phases.Add(progress);
        if (progress.IsTerminal)
        {
            ActiveSourceKey = null;
        }
    }
}

public sealed class IngestRunConfiguration : IEntityTypeConfiguration<IngestRun>
{
    public void Configure(EntityTypeBuilder<IngestRun> builder)
    {
        builder.ToTable(nameof(IngestRun));

        builder
            .HasOne(r => r.Source)
            .WithMany()
            .HasPrincipalKey(s => s.Id)
            .HasForeignKey(r => r.SourceKey)
            .OnDelete(DeleteBehavior.Restrict);

        // One active run per source. NULL (terminal) rows are excluded, so completed runs never collide; a
        // concurrent second insert trips the unique index and the endpoint maps it to 409 Conflict.
        builder
            .HasIndex(r => r.ActiveSourceKey)
            .IsUnique()
            .HasFilter($"\"{nameof(IngestRun.ActiveSourceKey)}\" IS NOT NULL");

        // The history always travels with the run.
        builder.Navigation(r => r.Phases).AutoInclude();
    }
}
