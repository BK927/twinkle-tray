namespace TwinkleTray.Core;

/// <summary>Combines refresh requests without losing changes requested during an active refresh.</summary>
/// <remarks>
/// Requests waiting for the shared gate join one batch. Requests made after that batch starts
/// wait for a subsequent refresh. The gate is released between batches so other operations can run.
/// Callbacks retain the synchronization context of the request that starts the queue; the queue
/// does not own or dispose the shared gate. A callback must not await another request on this queue.
/// </remarks>
public sealed class CoalescingRefreshQueue
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sharedGate;
    private readonly Func<Task> _refresh;
    private readonly CancellationToken _lifetime;
    private TaskCompletionSource? _pending;
    private bool _running;

    public CoalescingRefreshQueue(SemaphoreSlim sharedGate, Func<Task> refresh, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(sharedGate);
        ArgumentNullException.ThrowIfNull(refresh);
        _sharedGate = sharedGate;
        _refresh = refresh;
        _lifetime = lifetime;
    }

    /// <summary>Completes after a refresh that starts after this request, or reports that batch's failure.</summary>
    public Task RequestAsync()
    {
        Task completion;
        lock (_sync)
        {
            if (_lifetime.IsCancellationRequested) return Task.FromCanceled(_lifetime);
            _pending ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _pending.Task;
            if (_running) return completion;
            _running = true;
        }
        _ = DrainAsync();
        return completion;
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            try { await _sharedGate.WaitAsync(_lifetime); }
            catch (Exception error)
            {
                TaskCompletionSource? pending;
                lock (_sync)
                {
                    pending = _pending;
                    _pending = null;
                    _running = false;
                }
                if (pending is not null) Complete(pending, error);
                return;
            }

            TaskCompletionSource batch;
            lock (_sync)
            {
                batch = _pending!;
                _pending = null;
            }
            Exception? failure = null;
            try
            {
                _lifetime.ThrowIfCancellationRequested();
                await _refresh();
            }
            catch (Exception error) { failure = error; }
            finally { _sharedGate.Release(); }

            if (failure is null) batch.TrySetResult();
            else Complete(batch, failure);

            lock (_sync)
            {
                if (_pending is not null) continue;
                _running = false;
                return;
            }
        }
    }

    private static void Complete(TaskCompletionSource completion, Exception error)
    {
        if (error is OperationCanceledException canceled) completion.TrySetCanceled(canceled.CancellationToken);
        else completion.TrySetException(error);
    }
}
