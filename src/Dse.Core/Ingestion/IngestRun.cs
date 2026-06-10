// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Data;
using Dse.Ingestion.Events;
using Dse.Sources;
using EntityFrameworkCore.Projectables;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dse.Ingestion;

/// <summary>
///     An ingest run. The aggregate owns its ordered <see cref="Phases" /> — the history is the state. Callers build
///     an <see cref="IngestProgress" /> and <see cref="Advance" /> the run with it; reaching a terminal checkpoint
///     releases the source's single-flight slot. Creating a run raises <see cref="IngestRunCreatedEvent" />, which
///     the EF transactional middleware publishes through Wolverine on save.
/// </summary>
public sealed class IngestRun : AggregateRoot<Guid>
{
    // The history is the state. Exposed read-only and mutated only through Advance, so the terminal/no-backward
    // invariant lives in exactly one place; EF maps the navigation via the backing field.
    private readonly List<IngestProgress> _phases = [];
    private IngestRun() { }
    private IngestRun(Guid id) : base(id) { }

    public SourceKey SourceKey { get; init; } = null!;
    public bool DryRun { get; init; }

    // Mirrors SourceKey while active, NULL once terminal. A filtered unique index on this column enforces one
    // active run per source; the endpoint maps the violation to 409.
    public SourceKey? ActiveSourceKey { get; private set; }
    public IReadOnlyList<IngestProgress> Phases => _phases;

    [Projectable]
    public IngestProgress CurrentProgress => Phases.OrderBy(p => p.CreatedAt).Last();

    [Projectable]
    public bool IsTerminal => CurrentProgress.IsTerminal;

    public static IngestRun Create(SourceKey sourceKey, bool dryRun = false)
    {
        var run = new IngestRun(Guid.NewGuid())
        {
            SourceKey = sourceKey,
            ActiveSourceKey = sourceKey,
            DryRun = dryRun,
        };

        run._phases.Add(new IngestProgress(Guid.NewGuid()) { Checkpoint = IngestCheckpoint.Queued });
        run.RaiseDomainEvent(new IngestRunCreatedEvent(run.Id));
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

        _phases.Add(progress);
        if (progress.IsTerminal)
        {
            ActiveSourceKey = null;
        }
    }

    /// <summary>
    ///     Claims a queued run for execution. A run only ever starts from <see cref="IngestCheckpoint.Queued" />: an
    ///     already-terminal run is finished, and a re-delivered run that had already started is recorded as
    ///     <see cref="IngestCheckpoint.Interrupted" /> — runs never resume. Returns <c>true</c> only when the caller
    ///     should proceed to execute; a <c>false</c> result may have advanced the run, so the caller must persist.
    /// </summary>
    public bool TryClaimForExecution()
    {
        if (IsTerminal)
        {
            return false;
        }

        if (CurrentProgress.Checkpoint is not IngestCheckpoint.Queued)
        {
            Advance(IngestProgress.At(IngestCheckpoint.Interrupted,
                new { reason = "Re-delivered after a prior attempt had started; runs do not resume." }));
            return false;
        }

        return true;
    }

    /// <summary>Records the terminal Canceled checkpoint, releasing the source's single-flight slot.</summary>
    public void Cancel(string reason) => Advance(IngestProgress.At(IngestCheckpoint.Canceled, new { reason }));
}

public sealed class IngestRunConfiguration : IEntityTypeConfiguration<IngestRun>
{
    public void Configure(EntityTypeBuilder<IngestRun> builder)
    {
        builder.ToTable(nameof(IngestRun));

        // FK and referential integrity to the Source aggregate by key only — no CLR navigation across the boundary.
        builder
            .HasOne<Source>()
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

        // The history always travels with the run; the read-only navigation is read/written through its backing field.
        builder.Navigation(r => r.Phases).AutoInclude().UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
