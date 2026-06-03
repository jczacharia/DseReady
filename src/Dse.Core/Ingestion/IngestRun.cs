// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Data;
using Dse.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dse.Ingestion;

public sealed class IngestRun : Entity
{
    private IngestRun() { }

    public Source Source { get; init; } = null!;
    public SourceKey SourceKey { get; init; } = null!;

    public bool DryRun { get; init; }
    public List<IngestRunProgress> Progress { get; init; } = [];

    public static IngestRun Create(Source source, bool dryRun = false) => Create(source.Id, dryRun);

    public static IngestRun Create(SourceKey sourceKey, bool dryRun = false) => new()
    {
        SourceKey = sourceKey,
        DryRun = dryRun,
        Progress = [new IngestRunProgress.Queued()],
    };
}

public sealed class IngestRunConfiguration : IEntityTypeConfiguration<IngestRun>
{
    public void Configure(EntityTypeBuilder<IngestRun> builder)
    {
        builder.ToTable(nameof(IngestRun));
        builder.Navigation(r => r.Progress).AutoInclude();
        builder.HasOne(r => r.Source)
            .WithMany()
            .HasPrincipalKey(s => s.Id)
            .HasForeignKey(r => r.SourceKey)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed record IngestRunCreated(Guid RunId);

public sealed record IngestRunProgressed(Guid ProgressId);
