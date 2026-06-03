// Copyright (c) PNC Financial Services. All rights reserved.


using Dse.Sources;

namespace Dse.Ingestion;

/// <summary>End-to-end ingestion pipeline for one source.</summary>
public interface IIngestRunner
{
    SourceKey SourceKey { get; }

    Task RunAsync(IngestRun run, CancellationToken ct);
}
