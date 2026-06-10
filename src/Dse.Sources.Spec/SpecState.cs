// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Sources.Spec;

/// <summary>
///     The control surface for the <see cref="Spec" /> source's in-process ingestion. A test steers this singleton —
///     how many documents the source yields, and an optional gate that parks production mid-run so a run is provably
///     in-flight when cancellation arrives. Because the Spec source produces documents directly, there is no network
///     seam to stub: exercising the ingestion engine needs no HTTP fake.
/// </summary>
public sealed class SpecState
{
    private TaskCompletionSource _entered = NewTcs();
    private TaskCompletionSource _release = NewTcs();

    /// <summary>How many documents the source yields; a dry run still clamps its own work to one.</summary>
    public int Total { get; set; }

    /// <summary>When armed, production parks on <see cref="ReleaseTask" /> until released or canceled.</summary>
    public bool GateProduction { get; private set; }

    /// <summary>Completes the first time production reaches the gate — the run is now mid-ingest.</summary>
    public Task Entered => _entered.Task;

    internal Task ReleaseTask => _release.Task;

    /// <summary>Arms the gate with fresh signals for one cancel scenario.</summary>
    public void BeginGate()
    {
        _entered = NewTcs();
        _release = NewTcs();
        GateProduction = true;
    }

    public void Release() => _release.TrySetResult();

    internal void SignalEntered() => _entered.TrySetResult();

    public void Reset()
    {
        GateProduction = false;
        Total = 0;
        _entered = NewTcs();
        _release = NewTcs();
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
