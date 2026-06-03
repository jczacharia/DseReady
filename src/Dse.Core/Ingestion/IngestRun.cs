// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Data;
using Dse.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dse.Ingestion;

/// <summary>
///     Flat aggregate carrying the current state of an ingestion run.
///     History lives in <see cref="IngestRunEvent" />; this type is the read-optimized summary.
/// </summary>
public sealed class IngestRun : Entity
{
    private IngestRun() { }

    public Source Source { get; init; } = null!;
    public SourceKey SourceKey { get; init; } = null!;

    public bool DryRun { get; init; }

    public IngestPhase Phase { get; set; } = IngestPhase.Queued;

    public string? TargetIndex { get; set; }
    public string? PreviousIndex { get; set; }

    public long TotalItems { get; set; }
    public long ItemsIngested { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    public string? FailureReason { get; set; }

    public bool IsTerminal =>
        Phase is IngestPhase.Succeeded or IngestPhase.Failed or IngestPhase.Faulted or IngestPhase.Canceled;

    public static IngestRun Create(Source source, bool dryRun = false) => Create(source.Id, dryRun);

    public static IngestRun Create(SourceKey sourceKey, bool dryRun = false) => new()
    {
        SourceKey = sourceKey,
        DryRun = dryRun,
        Phase = IngestPhase.Queued,
    };
}

public sealed class IngestRunConfiguration : IEntityTypeConfiguration<IngestRun>
{
    public void Configure(EntityTypeBuilder<IngestRun> builder)
    {
        builder.ToTable(nameof(IngestRun));

        builder.Property(r => r.Phase).HasConversion<string>();

        builder.HasOne(r => r.Source)
            .WithMany()
            .HasPrincipalKey(s => s.Id)
            .HasForeignKey(r => r.SourceKey)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed record IngestRunCreated(Guid RunId);
