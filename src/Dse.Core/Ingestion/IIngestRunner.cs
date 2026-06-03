// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Sources;

namespace Dse.Ingestion;

/// <summary>
///     Runs a single ingestion cycle end-to-end for one source: bootstrap the ES index, measure total,
///     ingest, drain, alias swap, with a per-tick progress timer pushing <see cref="IngestRunEvent" /> rows
///     to the database and the in-memory <see cref="IngestProgressBroadcaster" />.
/// </summary>
public interface IIngestRunner
{
    SourceKey SourceKey { get; }

    Task RunAsync(IngestRun run, CancellationToken ct);
}
