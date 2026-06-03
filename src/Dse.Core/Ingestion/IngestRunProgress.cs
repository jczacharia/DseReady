// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Dse.Data;
using Dse.Shared;
using Dse.Sources;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Thinktecture;
using UnitsNet;

namespace Dse.Ingestion;

[ExcludeFromCodeCoverage]
public readonly record struct IngestReportSnapshot(
    SourceKey SourceKey,
    long TotalToProduce,
    TimeSpan Elapsed,
    double PercentComplete,
    double DocsPerSecond,
    TimeSpan EstimatedRemaining,
    long Produced,
    long ManagedMemoryBytes,
    long WorkingMemoryBytes,
    DateTimeOffset Timestamp)
{
    public string PrettyPrint() =>
        $"""
         [{SourceKey}]
         completion:  {PercentComplete:0.00}%
         elapsed:     {Elapsed.Humanize(2)}
         remaining:   {EstimatedRemaining.Humanize(2)}
         throughput:  {DocsPerSecond:N0} docs/sec
         produced:    {Produced:N0} / {TotalToProduce:N0} docs
         managed mem: {Information.FromBytes(ManagedMemoryBytes).Humanize()}
         working mem: {Information.FromBytes(WorkingMemoryBytes).Humanize()}
         timestamp:   {Timestamp:s}
         """;
}

[JsonDerivedType(typeof(Queued), "queued")]
[JsonDerivedType(typeof(Started), "started")]
[JsonDerivedType(typeof(Bootstrapped), "bootstrapped")]
[JsonDerivedType(typeof(TotalMeasured), "totalMeasured")]
[JsonDerivedType(typeof(Ingesting), "ingesting")]
[JsonDerivedType(typeof(Draining), "draining")]
[JsonDerivedType(typeof(Aliasing), "aliasing")]
[JsonDerivedType(typeof(Succeeded), "succeeded")]
[JsonDerivedType(typeof(Failed), "failed")]
[JsonDerivedType(typeof(Faulted), "faulted")]
[JsonDerivedType(typeof(Canceled), "canceled")]
[Union]
public abstract partial record IngestRunProgress : IEntity<Guid>
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public IngestRun IngestRun { get; init; }
    public Guid IngestRunId { get; init; }

    /// <summary>Marker for variants that carry a mid-run snapshot.</summary>
    public interface IWithSnapshot
    {
        IngestReportSnapshot Snapshot { get; }
    }

    // ── Lifecycle markers — past tense ("this just happened") ──
    public sealed record Queued : IngestRunProgress;

    public sealed record Started(bool DryRun) : IngestRunProgress;

    public sealed record Bootstrapped(
        string IndexName,
        int BatchExportSize,
        int DrainSize,
        int MaxConcurrency) : IngestRunProgress;

    public sealed record TotalMeasured(long Total) : IngestRunProgress;

    // ── Mid-phase ticks — gerund ("currently in phase X; here's the latest snapshot") ──
    public sealed record Ingesting(IngestReportSnapshot Snapshot) : IngestRunProgress, IWithSnapshot;

    public sealed record Draining(IngestReportSnapshot Snapshot) : IngestRunProgress, IWithSnapshot;

    public sealed record Aliasing(IngestReportSnapshot Snapshot) : IngestRunProgress, IWithSnapshot;

    // ── Terminal states — past tense ──
    public sealed record Succeeded(IngestReportSnapshot Snapshot) : IngestRunProgress, IWithSnapshot;

    public sealed record Failed(string Reason) : IngestRunProgress;

    /// <summary>
    ///     Deserialization path: the primary ctor parameters must bind to <see cref="Exception" />,
    ///     otherwise System.Text.Json cannot round-trip this state and any stream consumer breaks on an error.
    /// </summary>
    [method: JsonConstructor]
    public sealed record Faulted(ExceptionDto Exception) : IngestRunProgress
    {
        public Faulted(Exception ex) : this(ExceptionDto.From(ex)) { }
    }

    public sealed record Canceled(string Reason) : IngestRunProgress;

    public bool IsTerminal => this is Succeeded or Failed or Faulted or Canceled;
    public IngestReportSnapshot? SnapshotOrNull => this is IWithSnapshot s ? s.Snapshot : null;
}

internal sealed class IngestRunProgressConfiguration :
    IEntityTypeConfiguration<IngestRunProgress>,
    IEntityTypeConfiguration<IngestRunProgress.Queued>,
    IEntityTypeConfiguration<IngestRunProgress.Started>,
    IEntityTypeConfiguration<IngestRunProgress.Bootstrapped>,
    IEntityTypeConfiguration<IngestRunProgress.TotalMeasured>,
    IEntityTypeConfiguration<IngestRunProgress.Ingesting>,
    IEntityTypeConfiguration<IngestRunProgress.Draining>,
    IEntityTypeConfiguration<IngestRunProgress.Aliasing>,
    IEntityTypeConfiguration<IngestRunProgress.Succeeded>,
    IEntityTypeConfiguration<IngestRunProgress.Failed>,
    IEntityTypeConfiguration<IngestRunProgress.Faulted>,
    IEntityTypeConfiguration<IngestRunProgress.Canceled>
{
    public void Configure(EntityTypeBuilder<IngestRunProgress> builder)
    {
        builder.ToTable(nameof(IngestRunProgress));

        builder
            .HasOne(p => p.IngestRun)
            .WithMany(r => r.Progress)
            .HasForeignKey(p => p.IngestRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasDiscriminator<string>("Type")
            .HasValue<IngestRunProgress.Queued>(nameof(IngestRunProgress.Queued))
            .HasValue<IngestRunProgress.Started>(nameof(IngestRunProgress.Started))
            .HasValue<IngestRunProgress.Bootstrapped>(nameof(IngestRunProgress.Bootstrapped))
            .HasValue<IngestRunProgress.TotalMeasured>(nameof(IngestRunProgress.TotalMeasured))
            .HasValue<IngestRunProgress.Ingesting>(nameof(IngestRunProgress.Ingesting))
            .HasValue<IngestRunProgress.Draining>(nameof(IngestRunProgress.Draining))
            .HasValue<IngestRunProgress.Aliasing>(nameof(IngestRunProgress.Aliasing))
            .HasValue<IngestRunProgress.Succeeded>(nameof(IngestRunProgress.Succeeded))
            .HasValue<IngestRunProgress.Failed>(nameof(IngestRunProgress.Failed))
            .HasValue<IngestRunProgress.Faulted>(nameof(IngestRunProgress.Faulted))
            .HasValue<IngestRunProgress.Canceled>(nameof(IngestRunProgress.Canceled));
    }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Queued> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Started> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Bootstrapped> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.TotalMeasured> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Ingesting> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Draining> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Aliasing> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Succeeded> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Failed> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Faulted> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Canceled> builder) { }
}
