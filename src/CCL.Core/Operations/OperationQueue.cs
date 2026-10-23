using System.Collections.ObjectModel;
using System.Diagnostics;

namespace CCL.Core.Operations;

/// <summary>
/// 顺序执行的任务队列。同一时刻只跑一项，其余排队；
/// 所有对 <see cref="Items"/> 的改动都会被编组到构造时捕获的同步上下文，
/// 因此界面可以直接把集合绑定到列表控件。
/// </summary>
public sealed class OperationQueue : IAsyncDisposable
{
    private readonly List<QueueItem> _pending = [];
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SynchronizationContext? _context;
    private readonly Task _worker;
    private QueueItem? _current;

    public OperationQueue(SynchronizationContext? context = null, Action<string>? trace = null)
    {
        _context = context ?? SynchronizationContext.Current;
        _trace = trace;
        _worker = Task.Run(WorkerLoopAsync);
    }

    private readonly Action<string>? _trace;
    private bool _clearWhenAllFinished;

    public ObservableCollection<QueueItem> Items { get; } = [];

    public QueueItem? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    /// <summary>任何一项的状态发生有意义的变化时触发（已编组到界面线程）。</summary>
    public event EventHandler? ActivityChanged;

    public QueueItem Enqueue(IQueuedOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var item = new QueueItem(operation);
        Mutate(() => Items.Add(item));

        lock (_gate) _pending.Add(item);
        _signal.Release();
        _trace?.Invoke($"入队 {item.Title}，排队 {_pending.Count} 项");
        RaiseActivity();
        return item;
    }

    /// <summary>取消指定项：排队中的直接标记取消并移出队列，运行中的通过令牌中断。</summary>
    public void Cancel(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsFinished) return;

        var removed = false;
        lock (_gate)
        {
            if (_pending.Remove(item))
            {
                item.MarkCanceled();
                removed = true;
            }
        }

        if (removed)
        {
            item.RequestCancel();
            RaiseActivity();
            return;
        }

        item.RequestCancel();
        // 运行中的项由工作循环捕获 OperationCanceledException 后收尾
    }

    /// <summary>取消当前运行项，停止后续排队项的继续执行。</summary>
    public void CancelCurrent()
    {
        QueueItem? current;
        lock (_gate) current = _current;
        current?.RequestCancel();
    }

    /// <summary>
    /// 取消当前运行项并中止所有排队项。<paramref name="clearWhenDone"/> 为真时，
    /// 等当前项真正停下来后自动把已结束的记录一并清掉，避免用户再手动清理一次。
    /// </summary>
    public void CancelAll(bool clearWhenDone = true)
    {
        List<QueueItem> pending;
        lock (_gate)
        {
            pending = [.. _pending];
            _pending.Clear();
            _current?.RequestCancel();
            _clearWhenAllFinished = clearWhenDone;
        }

        foreach (var item in pending) item.MarkCanceled();
        _trace?.Invoke($"全部取消，取消排队 {pending.Count} 项{(clearWhenDone ? "，结束后自动清理" : string.Empty)}");
        RaiseActivity();

        if (clearWhenDone) TryClearWhenAllFinished();
    }

    /// <summary>移除所有已结束的项。</summary>
    public void ClearFinished()
    {
        List<QueueItem> finished;
        lock (_gate) finished = Items.Where(i => i.IsFinished).ToList();
        if (finished.Count == 0) return;

        Mutate(() =>
        {
            foreach (var item in finished) Items.Remove(item);
        });
        _trace?.Invoke($"清除已结束 {finished.Count} 项，原有 {Items.Count} 项");
        foreach (var item in finished) item.Dispose();
        RaiseActivity();
    }

    private async Task WorkerLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            QueueItem? item;
            lock (_gate)
            {
                if (_pending.Count == 0) continue;
                item = _pending[0];
                _pending.RemoveAt(0);
                _current = item;
            }

            await RunItemAsync(item).ConfigureAwait(false);

            lock (_gate) _current = null;
            RaiseActivity();
            TryClearWhenAllFinished();
        }
    }

    /// <summary>
    /// 全部取消后，等最后一项也停下来，把记录清干净。
    /// 判断要读取 <see cref="Items"/>，所以整段编组到界面线程执行。
    /// </summary>
    private void TryClearWhenAllFinished()
    {
        if (!_clearWhenAllFinished) return;

        Mutate(() =>
        {
            bool shouldClear;
            lock (_gate)
            {
                if (!_clearWhenAllFinished) return;
                if (_pending.Count > 0 || _current is not null) return;
                shouldClear = Items.Count > 0 && Items.All(i => i.IsFinished);
                if (shouldClear) _clearWhenAllFinished = false;
            }

            if (shouldClear) ClearFinished();
        });
    }

    private async Task RunItemAsync(QueueItem item)
    {
        Mutate(item.MarkRunning);
        RaiseActivity();

        var progress = new InlineProgress(p => Mutate(() => item.Update(p.Stage, p.Fraction)));
        try
        {
            await item.Operation.RunAsync(progress, item.Token).ConfigureAwait(false);
            Mutate(item.MarkCompleted);
        }
        catch (OperationCanceledException)
        {
            Mutate(item.MarkCanceled);
        }
        catch (Exception ex)
        {
            Mutate(() => item.MarkFailed(ex.Message));
        }
    }

    private void Mutate(Action action)
    {
        if (_context is null)
        {
            action();
            return;
        }

        TryPost(() => action());
    }

    private void RaiseActivity()
    {
        var handler = ActivityChanged;
        if (handler is null) return;
        if (_context is null)
        {
            handler(this, EventArgs.Empty);
            return;
        }

        TryPost(() => handler(this, EventArgs.Empty));
    }

    /// <summary>
    /// 向上下文投递回调。界面已关闭时 <see cref="SynchronizationContext.Post"/> 会抛异常，
    /// 而这段代码常在后台线程上运行——异常逃出去会直接终止进程，所以必须就地吞掉。
    /// </summary>
    private void TryPost(Action action)
    {
        try
        {
            _context!.Post(_ => action(), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 目标上下文已销毁：本次通知没有接收方，丢弃即可
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        CancelAll();
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            Debug.WriteLine("队列未能及时结束：" + ex.Message);
        }

        foreach (var item in Items) item.Dispose();
        _signal.Dispose();
        _shutdown.Dispose();
    }

    private sealed class InlineProgress(Action<OperationProgress> callback) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => callback(value);
    }
}
