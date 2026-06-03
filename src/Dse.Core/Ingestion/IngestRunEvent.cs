// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using Dse.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dse.Ingestion;

/// <summary>Append-only transition log; one row per phase change. <see cref="Seq" /> is monotonic per <see cref="RunId" />.</summary>
public sealed class IngestRunEvent : Entity
{
    public Guid RunId { get; init; }

    public long Seq { get; init; }

    public DateTimeOffset At { get; init; }

    public string Type { get; init; } = "";

    public string Payload { get; init; } = "";

    public string? CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public IngestEventPayload Deserialize() =>
        JsonSerializer.Deserialize<IngestEventPayload>(Payload, IngestEventPayloadJson.Options)
        ?? throw new InvalidOperationException($"IngestRunEvent {Id} payload deserialized to null.");
}

public sealed class IngestRunEventConfiguration : IEntityTypeConfiguration<IngestRunEvent>
{
    public void Configure(EntityTypeBuilder<IngestRunEvent> builder)
    {
        builder.ToTable(nameof(IngestRunEvent));

        builder.Property(e => e.Type).HasMaxLength(64);

        builder.Property(e => e.Payload);

        builder.HasIndex(e => new { e.RunId, e.Seq }).IsUnique();
        builder.HasIndex(e => e.RunId);
    }
}
