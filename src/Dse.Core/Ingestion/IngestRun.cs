// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Data;
using Dse.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dse.Ingestion;

public sealed class IngestRun : Entity
{
    public required SourceKey SourceKey { get; init; }
    public required bool DryRun { get; init; }
    public List<IngestRunProgress> Progress { get; init; } = [];

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
    }
}

public sealed record IngestRunCreated(Guid RunId);
