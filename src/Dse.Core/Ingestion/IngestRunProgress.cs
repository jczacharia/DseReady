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
public sealed class IngestReportSnapshot
{
    public required long TotalToProduce { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required double PercentComplete { get; init; }
    public required double DocsPerSecond { get; init; }
    public required TimeSpan EstimatedRemaining { get; init; }
    public required long Produced { get; init; }
    public required long ManagedMemoryBytes { get; init; }
    public required long WorkingMemoryBytes { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    public string PrettyPrint() =>
        $"""
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
public abstract partial class IngestRunProgress : IEntity<Guid>
{
    public IngestRun IngestRun { get; init; } = null!;
    public Guid IngestRunId { get; init; }

    public bool IsTerminal => this is Succeeded or Failed or Faulted or Canceled;
    public IngestReportSnapshot? SnapshotOrNull => this is IWithSnapshot s ? s.Snapshot : null;
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Marker for variants that carry a mid-run snapshot.</summary>
    public interface IWithSnapshot
    {
        IngestReportSnapshot Snapshot { get; }
    }

    // ── Lifecycle markers — past tense ("this just happened") ──
    public sealed class Queued : IngestRunProgress;

    public sealed class Started : IngestRunProgress
    {
        public required bool DryRun { get; init; }
    }

    public sealed class Bootstrapped : IngestRunProgress
    {
        public required string IndexName { get; init; }
        public required int BatchExportSize { get; init; }
        public required int DrainSize { get; init; }
        public required int MaxConcurrency { get; init; }
    }

    public sealed class TotalMeasured : IngestRunProgress
    {
        public required long Total { get; init; }
    }

    // ── Mid-phase ticks — gerund ("currently in phase X; here's the latest snapshot") ──
    public sealed class Ingesting : IngestRunProgress, IWithSnapshot
    {
        public required IngestReportSnapshot Snapshot { get; init; }
    }

    public sealed class Draining : IngestRunProgress, IWithSnapshot
    {
        public required IngestReportSnapshot Snapshot { get; init; }
    }

    public sealed class Aliasing : IngestRunProgress, IWithSnapshot
    {
        public required IngestReportSnapshot Snapshot { get; init; }
    }

    // ── Terminal states — past tense ──
    public sealed class Succeeded : IngestRunProgress, IWithSnapshot
    {
        public required IngestReportSnapshot Snapshot { get; init; }
    }

    public sealed class Failed : IngestRunProgress
    {
        public required string Reason { get; init; }
    }

    /// <summary>
    ///     Deserialization path: the primary ctor parameters must bind to <see cref="Exception" />,
    ///     otherwise System.Text.Json cannot round-trip this state and any stream consumer breaks on an error.
    /// </summary>
    public sealed class Faulted : IngestRunProgress
    {
        public required ExceptionDto Exception { get; init; }
    }

    public sealed class Canceled : IngestRunProgress
    {
        public required string Reason { get; init; }
    }
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
    public void Configure(EntityTypeBuilder<IngestRunProgress.Aliasing> builder)
    {
        ConfigureSnapshot(builder);
    }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Bootstrapped> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Canceled> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Draining> builder)
    {
        ConfigureSnapshot(builder);
    }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Failed> builder) { }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Faulted> builder)
    {
        builder.ComplexProperty(p => p.Exception);
    }

    public void Configure(EntityTypeBuilder<IngestRunProgress.Ingesting> builder)
    {
        ConfigureSnapshot(builder);
    }

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

    public void Configure(EntityTypeBuilder<IngestRunProgress.Succeeded> builder)
    {
        ConfigureSnapshot(builder);
    }

    public void Configure(EntityTypeBuilder<IngestRunProgress.TotalMeasured> builder) { }

    public static void ConfigureSnapshot<T>(EntityTypeBuilder<T> builder) where T : class, IngestRunProgress.IWithSnapshot
    {
        builder.ComplexProperty(b => b.Snapshot);
    }
}
