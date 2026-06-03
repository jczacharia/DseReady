// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using Dse.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dse.Ingestion;

/// <summary>
///     Append-only event log row. One row per state transition for a given <see cref="IngestRun" />.
///     <see cref="Seq" /> is monotonically increasing per <see cref="RunId" />; <see cref="Payload" /> is the
///     JSON-serialized <see cref="IngestEventPayload" /> for that transition.
/// </summary>
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

        // nvarchar(max) on SQL Server (planned prod), TEXT on SQLite today. Keep it untyped and let the
        // provider pick its native large-string column type. If/when payload querying becomes a real need on
        // SQL Server, switch to native json (SQL Server 2025+) or expose JSON_VALUE computed columns.
        builder.Property(e => e.Payload);

        builder.HasIndex(e => new { e.RunId, e.Seq }).IsUnique();
        builder.HasIndex(e => e.RunId);
    }
}
