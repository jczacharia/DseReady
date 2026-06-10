// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Sources;
using Elastic.Ingest.Elasticsearch;

namespace Dse.Ingestion;

public interface IIngest;

public interface IIngest<out TDoc> : IIngest where TDoc : class
{
    Task<long> GetDesiredTotalToProduceAsync(CancellationToken cancellationToken);
    Task IngestAsync(IIngestContext<TDoc> channel, CancellationToken cancellationToken);
}

public interface IIngestContext<in TDoc> where TDoc : class
{
    public int MaxConcurrency { get; }
    public long TotalToProduce { get; }
    ValueTask<bool> WaitToWriteDocAsync(CancellationToken ct);
    ValueTask WriteDocAsync(TDoc doc, CancellationToken ct);
}

/// <summary>End-to-end ingestion pipeline for one source.</summary>
public interface IIngestRunner
{
    SourceKey SourceKey { get; }

    /// <summary>Live progress of the in-flight run, computed on demand for the status endpoint.</summary>
    IngestProgress CurrentSnapshot { get; }

    Task RunAsync(IngestRun run, CancellationToken ct);
}

internal sealed class IngestContext<TDoc>(IngestChannel<TDoc> channel, long totalToProduce) : IIngestContext<TDoc>
    where TDoc : class
{
    public int MaxConcurrency => channel.MaxConcurrency;
    public long TotalToProduce => totalToProduce;
    public ValueTask<bool> WaitToWriteDocAsync(CancellationToken ct) => channel.WaitToWriteAsync(ct);
    public ValueTask WriteDocAsync(TDoc doc, CancellationToken ct) => channel.WriteAsync(doc, ct);
}
