// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text.Json;
using System.Text.Json.Serialization;
using Dse.Data;
using Dse.Shared;
using EntityFrameworkCore.Projectables;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using UnitsNet;

namespace Dse.Ingestion;

[JsonConverter(typeof(JsonStringEnumConverter<IngestCheckpoint>))]
public enum IngestCheckpoint
{
    Queued,
    Started,
    Bootstrapped,
    TotalMeasured,
    Ingesting,
    Draining,
    Aliasing,
    Succeeded,
    Failed,
    Faulted,
    Canceled,
    Interrupted,
}

/// <summary>
///     One transition in an <see cref="IngestRun" />'s life: a phase, an optional progress snapshot, and a free-form
///     metadata bag for phase-specific detail (index name, totals, failure reason, ...). A run owns an ordered list
///     of these; the history is the state.
/// </summary>
public sealed class IngestProgress : Entity<Guid>, IDisposable
{
    private IngestProgress() { }
    public IngestProgress(Guid id) : base(id) { }
    public IngestCheckpoint Checkpoint { get; init; } = IngestCheckpoint.Queued;
    public JsonDocument Metadata { get; init; } = JsonDocument.Parse("{}");

    // Back-reference for the EF relationship only; never serialized — the run owns the phases, so serializing
    // the phase's parent would cycle (Run → Phases → Run → …). RunId carries the association in responses.
    [JsonIgnore]
    public IngestRun Run { get; init; } = null!;

    public Guid RunId { get; init; }

    public long TotalToProduce { get; init; }
    public TimeSpan Elapsed { get; init; }
    public double PercentComplete { get; init; }
    public double DocsPerSecond { get; init; }
    public TimeSpan EstimatedRemaining { get; init; }
    public long Produced { get; init; }
    public long ManagedMemoryBytes { get; init; }
    public long WorkingMemoryBytes { get; init; }

    [Projectable]
    public bool IsTerminal =>
        Checkpoint == IngestCheckpoint.Canceled || // Do not merge into pattern for [Projectable]
        Checkpoint == IngestCheckpoint.Interrupted ||
        Checkpoint == IngestCheckpoint.Failed ||
        Checkpoint == IngestCheckpoint.Faulted ||
        Checkpoint == IngestCheckpoint.Succeeded;

    public void Dispose() => Metadata.Dispose();

    /// <summary>A checkpoint carrying no live counters — for control-plane transitions (cancel, interrupt, ...).</summary>
    public static IngestProgress At(IngestCheckpoint checkpoint, object? metadata = null) => new(Guid.NewGuid())
    {
        Checkpoint = checkpoint,
        Metadata = metadata is null ? JsonDocument.Parse("{}") : JsonSerializer.SerializeToDocument(metadata, JsonDefaults.Web),
    };

    public override string ToString() =>
        $"""
         runId:       {RunId}
         phase:       {Checkpoint:G}
         completion:  {PercentComplete:0.00}%
         elapsed:     {Elapsed.Humanize(2)}
         remaining:   {EstimatedRemaining.Humanize(2)}
         throughput:  {DocsPerSecond:N0} docs/sec
         produced:    {Produced:N0} / {TotalToProduce:N0} docs
         managed mem: {Information.FromBytes(ManagedMemoryBytes).Humanize()}
         working mem: {Information.FromBytes(WorkingMemoryBytes).Humanize()}
         timestamp:   {CreatedAt:s}
         metadata:    {JsonSerializer.Serialize(Metadata, JsonDefaults.Pretty)}
         """;
}

public sealed class IngestProgressConfiguration : IEntityTypeConfiguration<IngestProgress>
{
    public void Configure(EntityTypeBuilder<IngestProgress> builder)
    {
        builder.ToTable(nameof(IngestProgress));

        builder
            .HasOne(p => p.Run)
            .WithMany(r => r.Phases)
            .HasForeignKey(p => p.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .Property(p => p.Checkpoint)
            .HasConversion<EnumToStringConverter<IngestCheckpoint>>();

        builder
            .Property(p => p.Metadata)
            .HasConversion<JsonDocumentValueConverter>();
    }
}
