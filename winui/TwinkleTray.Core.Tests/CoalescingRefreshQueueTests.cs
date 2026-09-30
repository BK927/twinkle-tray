using System.Collections.Concurrent;
using TwinkleTray.Core;

internal static class CoalescingRefreshQueueTests
{
    public static (string Name, Action Run)[] All =>
    [
        Async("Refresh requests waiting behind an operation share one scan", WaitingRequestsAsync),
        Async("Concurrent refresh callers share one pending batch", ConcurrentRequestsAsync),
        Async("Requests during a scan wait for one fresh trailing scan", TrailingRequestsAsync),
        Async("A queued write runs between refresh batches", WriteInterleavingAsync),
        Async("A failed refresh reports its batch and permits later scans", FailureRecoveryAsync),
        Async("Lifetime cancellation drains pending refresh requests without releasing another owner's gate", PendingCancellationAsync),
        Async("Lifetime cancellation prevents a trailing scan after active work finishes", ActiveCancellationAsync),
        Async("A canceled refresh batch does not poison later requests", BatchCancellationAsync),
        ("Refresh callbacks preserve the caller synchronization context", SynchronizationContext)
    ];

    private static (string, Action) Async(string name, Func<Task> test) =>
        (name, () => test().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task WaitingRequestsAsync()
    {
        using var gate = new SemaphoreSlim(0, 1);
        int calls = 0;
        var queue = new CoalescingRefreshQueue(gate, () => { calls++; return Task.CompletedTask; }, default);
        var requests = Enumerable.Range(0, 20).Select(_ => queue.RequestAsync()).ToArray();
        Check(requests.All(task => ReferenceEquals(task, requests[0])) && calls == 0, "Waiting requests were not combined.");
        gate.Release();
        await Task.WhenAll(requests);
        Check(calls == 1, "A blocked burst started more than one refresh.");
        await queue.RequestAsync();
        Check(calls == 2, "A later request reused a completed refresh.");
        await gate.WaitAsync();
        gate.Release();
    }

    private static async Task TrailingRequestsAsync()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var firstFinish = Signal(); var secondStarted = Signal(); var secondFinish = Signal();
        int version = 1, calls = 0, observedVersion = 0;
        var queue = new CoalescingRefreshQueue(gate, async () =>
        {
            if (++calls == 1) await firstFinish.Task;
            else { observedVersion = version; secondStarted.SetResult(); await secondFinish.Task; }
        }, default);
        var first = queue.RequestAsync();
        version = 2;
        var trailing = Enumerable.Range(0, 20).Select(_ => queue.RequestAsync()).ToArray();
        Check(trailing.All(task => ReferenceEquals(task, trailing[0])) && !ReferenceEquals(first, trailing[0]), "Active and pending batches were not separated.");
        firstFinish.SetResult();
        await first;
        await secondStarted.Task;
        Check(calls == 2 && observedVersion == 2 && trailing.All(task => !task.IsCompleted), "Trailing callers completed before their fresh scan finished.");
        secondFinish.SetResult();
        await Task.WhenAll(trailing);
        Check(calls == 2, "The active burst was not bounded to one trailing refresh.");
    }

    private static async Task ConcurrentRequestsAsync()
    {
        using var gate = new SemaphoreSlim(0, 1);
        int calls = 0;
        var queue = new CoalescingRefreshQueue(gate, () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, default);
        // Return the request in an array so Task.Run does not unwrap and await the blocked refresh.
        var workers = Enumerable.Range(0, 20).Select(_ => Task.Run(() => new[] { queue.RequestAsync() })).ToArray();
        var requests = (await Task.WhenAll(workers)).SelectMany(tasks => tasks).ToArray();
        Check(requests.All(task => ReferenceEquals(task, requests[0])), "Concurrent callers created separate pending batches.");
        gate.Release();
        await Task.WhenAll(requests);
        Check(calls == 1, "Concurrent callers started multiple queue drains.");
    }

    private static async Task WriteInterleavingAsync()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var firstFinish = Signal();
        var order = new List<string>();
        int calls = 0;
        var queue = new CoalescingRefreshQueue(gate, async () =>
        {
            order.Add("refresh" + ++calls);
            if (calls == 1) await firstFinish.Task;
        }, default);
        var first = queue.RequestAsync();
        var trailing = queue.RequestAsync();
        var writeGate = gate.WaitAsync();
        firstFinish.SetResult();
        await writeGate;
        Check(calls == 1 && !trailing.IsCompleted, "A trailing refresh bypassed the queued write.");
        order.Add("write");
        gate.Release();
        await Task.WhenAll(first, trailing);
        Check(order.SequenceEqual(["refresh1", "write", "refresh2"]), "Refresh/write serialization changed.");
    }

    private static async Task FailureRecoveryAsync()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var firstFinish = Signal();
        var expected = new InvalidOperationException("Simulated scan failure");
        int calls = 0;
        var queue = new CoalescingRefreshQueue(gate, async () =>
        {
            if (++calls == 1) { await firstFinish.Task; throw expected; }
        }, default);
        var first = queue.RequestAsync();
        var trailing = queue.RequestAsync();
        firstFinish.SetResult();
        try { await first; throw new Exception("The failed batch completed successfully."); }
        catch (InvalidOperationException error) { Check(ReferenceEquals(expected, error), "The callback failure was changed."); }
        await trailing;
        await queue.RequestAsync();
        Check(calls == 3, "A failed batch stranded pending or subsequent requests.");
    }

    private static async Task PendingCancellationAsync()
    {
        using var gate = new SemaphoreSlim(0, 1);
        using var lifetime = new CancellationTokenSource();
        int calls = 0;
        var queue = new CoalescingRefreshQueue(gate, () => { calls++; return Task.CompletedTask; }, lifetime.Token);
        var requests = Enumerable.Range(0, 20).Select(_ => queue.RequestAsync()).ToArray();
        lifetime.Cancel();
        foreach (var task in requests) await CanceledAsync(task, lifetime.Token);
        await CanceledAsync(queue.RequestAsync(), lifetime.Token);
        Check(calls == 0 && gate.CurrentCount == 0, "Cancellation ran a callback or released a gate it did not acquire.");
        gate.Release();
    }

    private static async Task ActiveCancellationAsync()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var lifetime = new CancellationTokenSource();
        var firstFinish = Signal();
        int calls = 0;
        var queue = new CoalescingRefreshQueue(gate, async () =>
        {
            calls++;
            await firstFinish.Task;
            lifetime.Token.ThrowIfCancellationRequested();
        }, lifetime.Token);
        var first = queue.RequestAsync();
        var trailing = queue.RequestAsync();
        lifetime.Cancel();
        Check(!first.IsCompleted, "Cancellation pretended that active work had stopped.");
        firstFinish.SetResult();
        await CanceledAsync(first, lifetime.Token);
        await CanceledAsync(trailing, lifetime.Token);
        Check(calls == 1, "A trailing callback ran after lifetime cancellation.");
        await gate.WaitAsync();
        gate.Release();
    }

    private static async Task BatchCancellationAsync()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        int calls = 0;
        var queue = new CoalescingRefreshQueue(gate, () => ++calls == 1 ? Task.FromCanceled(canceled.Token) : Task.CompletedTask, default);
        await CanceledAsync(queue.RequestAsync(), canceled.Token);
        await queue.RequestAsync();
        Check(calls == 2, "Callback cancellation prevented recovery.");
    }

    private static async Task CanceledAsync(Task task, CancellationToken expected)
    {
        try { await task; }
        catch (OperationCanceledException error)
        {
            Check(task.IsCanceled && error.CancellationToken == expected, "Cancellation status or token was not preserved.");
            return;
        }
        throw new InvalidOperationException("Expected a canceled request.");
    }

    private static void SynchronizationContext()
    {
        var previous = System.Threading.SynchronizationContext.Current;
        using var context = new PumpContext();
        using var gate = new SemaphoreSlim(0, 1);
        int thread = Environment.CurrentManagedThreadId, calls = 0;
        try
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            var queue = new CoalescingRefreshQueue(gate, async () =>
            {
                Check(System.Threading.SynchronizationContext.Current == context && Environment.CurrentManagedThreadId == thread, "Callback entered outside its caller's context.");
                await Task.Yield();
                Check(System.Threading.SynchronizationContext.Current == context && Environment.CurrentManagedThreadId == thread, "Callback resumed outside its caller's context.");
                calls++;
            }, default);
            var request = queue.RequestAsync();
            gate.Release();
            context.RunUntil(request);
            request.GetAwaiter().GetResult();
            Check(calls == 1, "The context-bound callback did not finish.");
        }
        finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class PumpContext : System.Threading.SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = new();
        public override void Post(SendOrPostCallback callback, object? state) => _work.Add((callback, state));
        public void RunUntil(Task task)
        {
            while (!task.IsCompleted)
            {
                if (!_work.TryTake(out var work, 5000)) throw new TimeoutException("A context-bound refresh did not complete.");
                work.Callback(work.State);
            }
        }
        public void Dispose() => _work.Dispose();
    }
}
