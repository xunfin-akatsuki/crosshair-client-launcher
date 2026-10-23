using System.Collections.Concurrent;
using CCL.Core.Operations;

namespace CCL.Core.Tests;

/// <summary>
/// 单线程同步上下文，模拟界面的线程编组行为。
/// 队列对 Items 的所有改动都会 Post 到这个线程，测试的读取也派发到这里，
/// 两边因此串行执行，复现真实使用方式而不引入锁。
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public SingleThreadContext()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "test-sync-context" };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        // 上下文关闭后队列不再接收，静默丢弃，避免在后台线程抛出未处理异常
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => Invoke(() => d(state));

    public T Invoke<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try
            {
                completion.SetResult(func());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, null);
        return completion.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action action) => Invoke(() =>
    {
        action();
        return true;
    });

    private void Loop()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("同步上下文回调异常：" + ex);
            }
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}

public class OperationQueueTests
{
    /// <summary>可控的假操作：记录是否开始、完成、被取消。</summary>
    private sealed class ScriptedOperation(string title, Func<CancellationToken, Task> body) : IQueuedOperation
    {
        public string Title { get; } = title;

        public bool Started { get; private set; }

        public bool Finished { get; private set; }

        public bool Canceled { get; private set; }

        public Task RunAsync(IProgress<OperationProgress> progress, CancellationToken token)
        {
            Started = true;
            progress.Report(new OperationProgress("运行", 0.5d));
            return RunCoreAsync(token);
        }

        private async Task RunCoreAsync(CancellationToken token)
        {
            try
            {
                await body(token).ConfigureAwait(false);
                Finished = true;
            }
            catch (OperationCanceledException)
            {
                Canceled = true;
                throw;
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("条件未在超时内满足");
    }

    /// <summary>在上下文线程上读一次状态快照，避免与队列的编组写入并发。</summary>
    private static (int Count, OperationState[] States) Snapshot(SingleThreadContext context, OperationQueue queue) =>
        context.Invoke(() => (queue.Items.Count, queue.Items.Select(i => i.State).ToArray()));

    private static bool AllFinished(SingleThreadContext context, OperationQueue queue)
    {
        var (count, states) = Snapshot(context, queue);
        return count > 0 && states.All(s => s is OperationState.Completed or OperationState.Canceled
            or OperationState.Failed);
    }

    [Fact]
    public async Task RunsQueuedOperationsOneAfterAnother()
    {
        using var context = new SingleThreadContext();
        await using var queue = new OperationQueue(context);
        var observe = new List<string>();
        var gates = new[] { new TaskCompletionSource(), new TaskCompletionSource() };

        void Record(string entry)
        {
            lock (observe) observe.Add(entry);
        }

        int Count()
        {
            lock (observe) return observe.Count;
        }

        for (var i = 0; i < 2; i++)
        {
            var slot = i;
            queue.Enqueue(new ScriptedOperation($"t{i}", async _ =>
            {
                Record($"start{slot}");
                await gates[slot].Task;
                Record($"end{slot}");
            }));
        }

        await WaitForAsync(() => Count() >= 1);
        lock (observe) Assert.Equal(["start0"], observe);

        gates[0].SetResult();
        await WaitForAsync(() => Count() >= 3);
        lock (observe) Assert.Equal(["start0", "end0", "start1"], observe);

        gates[1].SetResult();
        await WaitForAsync(() => AllFinished(context, queue));
        var (count, states) = Snapshot(context, queue);
        Assert.Equal(2, count);
        Assert.All(states, s => Assert.Equal(OperationState.Completed, s));
    }

    [Fact]
    public async Task CancelingPendingItemRemovesItWithoutRunning()
    {
        using var context = new SingleThreadContext();
        await using var queue = new OperationQueue(context);
        var gate = new TaskCompletionSource();
        var first = new ScriptedOperation("first", _ => gate.Task);
        var second = new ScriptedOperation("second", _ => Task.CompletedTask);

        queue.Enqueue(first);
        var pendingItem = queue.Enqueue(second);

        await WaitForAsync(() => first.Started);
        queue.Cancel(pendingItem);

        gate.SetResult();
        await WaitForAsync(() => AllFinished(context, queue));

        Assert.Equal(OperationState.Canceled, pendingItem.State);
        Assert.False(second.Started);
        Assert.Equal(OperationState.Completed, context.Invoke(() => queue.Items[0].State));
    }

    [Fact]
    public async Task CancelingRunningItemStopsItAndContinuesQueue()
    {
        using var context = new SingleThreadContext();
        await using var queue = new OperationQueue(context);
        var running = new ScriptedOperation("long", async token => await Task.Delay(Timeout.Infinite, token));
        var next = new ScriptedOperation("next", _ => Task.CompletedTask);

        var runningItem = queue.Enqueue(running);
        queue.Enqueue(next);

        await WaitForAsync(() => running.Started);
        queue.Cancel(runningItem);

        await WaitForAsync(() => AllFinished(context, queue));
        Assert.True(running.Canceled);
        Assert.Equal(OperationState.Canceled, runningItem.State);
        Assert.True(next.Finished);
    }

    [Fact]
    public async Task FailureMarksItemAndQueueKeepsGoing()
    {
        using var context = new SingleThreadContext();
        await using var queue = new OperationQueue(context);
        var broken = new ScriptedOperation("broken", _ => throw new InvalidOperationException("炸了"));
        var after = new ScriptedOperation("after", _ => Task.CompletedTask);

        queue.Enqueue(broken);
        queue.Enqueue(after);

        await WaitForAsync(() => AllFinished(context, queue));

        var (_, states) = Snapshot(context, queue);
        Assert.Equal(OperationState.Failed, states[0]);
        Assert.Equal(OperationState.Completed, states[1]);
        Assert.Contains("炸了", context.Invoke(() => queue.Items[0].StateText));
    }

    [Fact]
    public async Task CancelAllStopsCurrentAndAutoClearsRecords()
    {
        using var context = new SingleThreadContext();
        await using var queue = new OperationQueue(context);
        var gate = new TaskCompletionSource();
        var a = new ScriptedOperation("a", async token => await Task.Delay(Timeout.Infinite, token));
        var b = new ScriptedOperation("b", _ => gate.Task);

        queue.Enqueue(a);
        queue.Enqueue(b);

        await WaitForAsync(() => a.Started);
        queue.CancelAll();

        // 全部取消后记录应当被自动清理，不需要再手动“清除已完成”
        await WaitForAsync(() => context.Invoke(() => queue.Items.Count == 0));
        Assert.True(a.Canceled);
        Assert.False(b.Started);
    }

    [Fact]
    public async Task CancelAllWithoutAutoClearKeepsRecords()
    {
        using var context = new SingleThreadContext();
        await using var queue = new OperationQueue(context);
        var a = new ScriptedOperation("a", async token => await Task.Delay(Timeout.Infinite, token));
        var b = new ScriptedOperation("b", _ => Task.CompletedTask);

        queue.Enqueue(a);
        queue.Enqueue(b);

        await WaitForAsync(() => a.Started);
        queue.CancelAll(clearWhenDone: false);

        await WaitForAsync(() => AllFinished(context, queue));
        var (count, states) = Snapshot(context, queue);
        Assert.Equal(2, count);
        Assert.All(states, s => Assert.Equal(OperationState.Canceled, s));
    }

    [Fact]
    public async Task ClearFinishedKeepsUnfinishedItems()
    {
        using var context = new SingleThreadContext();
        await using var queue = new OperationQueue(context);
        var gate = new TaskCompletionSource();
        var quick = new ScriptedOperation("quick", _ => Task.CompletedTask);
        var slow = new ScriptedOperation("slow", _ => gate.Task);

        queue.Enqueue(quick);
        queue.Enqueue(slow);

        await WaitForAsync(() => context.Invoke(() =>
            queue.Items.Count == 2 && queue.Items[0].State == OperationState.Completed));

        queue.ClearFinished();
        await WaitForAsync(() => context.Invoke(() => queue.Items.Count == 1));

        Assert.Equal("slow", context.Invoke(() => queue.Items[0].Title));

        gate.SetResult();
        await WaitForAsync(() => context.Invoke(() => queue.Items[0].State == OperationState.Completed));
    }

    [Fact]
    public async Task ProgressIsForwardedToItem()
    {
        using var context = new SingleThreadContext();
        await using var queue = new OperationQueue(context);
        var gate = new TaskCompletionSource();
        // ScriptedOperation 先报告 0.5 再进入 body，用 gate 让它停在中间状态
        var item = queue.Enqueue(new ScriptedOperation("p", _ => gate.Task));

        await WaitForAsync(() => item.State == OperationState.Running && item.Progress > 0.4d);
        Assert.Equal(0.5d, item.Progress, 2);

        gate.SetResult();
        await WaitForAsync(() => item.State == OperationState.Completed);
        Assert.Equal(1d, item.Progress, 3);
    }
}
