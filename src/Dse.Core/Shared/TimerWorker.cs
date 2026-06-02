// Copyright (c) PNC Financial Services. All rights reserved.


namespace Dse.Shared;

public sealed class TimerWorker(Func<ValueTask> callback, Action<Exception>? onError = null, TimeSpan? period = null)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _period = period ?? TimeSpan.FromSeconds(1);
    private Task? _loop;

    public async ValueTask DisposeAsync()
    {
        if (_loop is null)
        {
            _cts.Dispose();
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* expected: the loop observes our own cancellation on dispose */
        }

        _cts.Dispose();
    }

    public void Start() => _loop ??= RunAsync(_cts.Token);

    public static TimerWorker StartNew(Func<ValueTask> callback, Action<Exception>? onError = null, TimeSpan? period = null)
    {
        TimerWorker worker = new(callback, onError, period);
        worker.Start();
        return worker;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(_period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await callback().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best-effort work: a faulting callback must not kill the loop (which would silently stop all
                    // future ticks) nor fault this task (which would resurface at DisposeAsync and fail the
                    // caller). Report it and keep ticking.
                    onError?.Invoke(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* expected: cancellation stops the timer loop cleanly */
        }
    }
}
