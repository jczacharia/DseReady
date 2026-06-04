// Copyright (c) PNC Financial Services. All rights reserved.


using System.Collections.Concurrent;

namespace Dse.Ingestion;

/// <summary>
///     Process-local registry of in-flight runs to their live runner + cancellation source. Lets the status
///     endpoint read a live snapshot and the cancel endpoint signal cooperative cancellation. Single-pod today;
///     the multi-pod evolution is a node-routed control message delivered to the node owning the run.
/// </summary>
public interface IIngestRunControl
{
    IDisposable Register(Guid runId, IIngestRunner runner, CancellationTokenSource cts);

    /// <summary>Live progress for an in-flight run, or null when no run with that id is executing here.</summary>
    IngestProgress? LiveSnapshot(Guid runId);

    /// <summary>Signals cooperative cancellation. Returns false when no run with that id is executing here.</summary>
    bool SignalCancellation(Guid runId);
}

public sealed class IngestRunControl : IIngestRunControl
{
    private readonly ConcurrentDictionary<Guid, Entry> _running = new();

    public IDisposable Register(Guid runId, IIngestRunner runner, CancellationTokenSource cts)
    {
        _running[runId] = new Entry(runner, cts);
        return new Registration(this, runId);
    }

    public IngestProgress? LiveSnapshot(Guid runId) =>
        _running.TryGetValue(runId, out Entry? entry) ? entry.Runner.CurrentSnapshot : null;

    public bool SignalCancellation(Guid runId)
    {
        if (!_running.TryGetValue(runId, out Entry? entry))
        {
            return false;
        }

        entry.Cts.Cancel();
        return true;
    }

    private void Release(Guid runId) => _running.TryRemove(runId, out _);

    private sealed record Entry(IIngestRunner Runner, CancellationTokenSource Cts);

    private sealed class Registration(IngestRunControl owner, Guid runId) : IDisposable
    {
        public void Dispose() => owner.Release(runId);
    }
}
