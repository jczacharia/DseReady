// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Ingestion;

public enum IngestPhase
{
    Queued = 0,
    Started = 1,
    Bootstrapped = 2,
    TotalMeasured = 3,
    Ingesting = 4,
    Draining = 5,
    Aliasing = 6,
    Succeeded = 7,
    Failed = 8,
    Faulted = 9,
    Canceled = 10,
}
