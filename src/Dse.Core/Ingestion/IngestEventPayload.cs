// Copyright (c) PNC Financial Services. All rights reserved.


using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dse.Shared;
using Humanizer;
using UnitsNet;

namespace Dse.Ingestion;

/// <summary>
///     Typed payload carried inside an <see cref="IngestRunEvent.Payload" /> JSON column.
///     One concrete record per transition. The <c>"$type"</c> discriminator is preserved across the
///     wire so SignalR / HTTP stream consumers can pattern-match on the concrete shape.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
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
public abstract record IngestEventPayload
{
    public sealed record Queued : IngestEventPayload;

    public sealed record Started(bool DryRun) : IngestEventPayload;

    public sealed record Bootstrapped(string IndexName, int BatchExportSize, int DrainSize, int MaxConcurrency)
        : IngestEventPayload;

    public sealed record TotalMeasured(long Total) : IngestEventPayload;

    public sealed record Ingesting(IngestReportSnapshot Snapshot) : IngestEventPayload, IWithSnapshot;

    public sealed record Draining(IngestReportSnapshot Snapshot) : IngestEventPayload, IWithSnapshot;

    public sealed record Aliasing(IngestReportSnapshot Snapshot) : IngestEventPayload, IWithSnapshot;

    public sealed record Succeeded(IngestReportSnapshot Snapshot) : IngestEventPayload, IWithSnapshot;

    public sealed record Failed(string Reason) : IngestEventPayload;

    /// <summary>
    ///     Deserialization path: the primary ctor parameters must bind to <see cref="ExceptionDto" />,
    ///     otherwise System.Text.Json cannot round-trip this state and any stream consumer breaks on an error.
    /// </summary>
    public sealed record Faulted(ExceptionDto Exception) : IngestEventPayload;

    public sealed record Canceled(string Reason) : IngestEventPayload;

    /// <summary>Marker for variants that carry a mid-run snapshot.</summary>
    public interface IWithSnapshot
    {
        IngestReportSnapshot Snapshot { get; }
    }
}

[ExcludeFromCodeCoverage]
public sealed record IngestReportSnapshot(
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

internal static class IngestEventPayloadJson
{
    public static readonly JsonSerializerOptions Options = JsonDefaults.Web;
}
