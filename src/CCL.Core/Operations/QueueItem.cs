using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CCL.Core.Operations;

public enum OperationState
{
    Pending,
    Running,
    Completed,
    Canceled,
    Failed
}

public sealed record OperationProgress(string Stage, double Fraction);

/// <summary>可入队的一项工作。实现方只需关心“拿令牌跑完”，取消由队列统一发起。</summary>
public interface IQueuedOperation
{
    string Title { get; }

    Task RunAsync(IProgress<OperationProgress> progress, CancellationToken token);
}

/// <summary>
/// 队列里的一项。状态与进度只由 <see cref="OperationQueue"/> 写入，
/// 对外暴露为只读属性供界面绑定。
/// </summary>
public sealed class QueueItem : INotifyPropertyChanged
{
    private readonly CancellationTokenSource _cts = new();
    private OperationState _state = OperationState.Pending;
    private double _progress;
    private string _stage = "等待中";
    private string? _error;

    internal QueueItem(IQueuedOperation operation)
    {
        Operation = operation;
        Title = operation.Title;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; } = Guid.NewGuid();

    public string Title { get; }

    internal IQueuedOperation Operation { get; }

    internal CancellationToken Token => _cts.Token;

    public OperationState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(StateText));
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (Math.Abs(_progress - value) < 0.0005) return;
            _progress = value;
            OnPropertyChanged();
        }
    }

    public string Stage
    {
        get => _stage;
        private set
        {
            if (_stage == value) return;
            _stage = value;
            OnPropertyChanged();
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (_error == value) return;
            _error = value;
            OnPropertyChanged();
        }
    }

    public bool CanCancel => State is OperationState.Pending or OperationState.Running;

    public bool IsFinished => State is OperationState.Completed or OperationState.Canceled or OperationState.Failed;

    public string StateText => State switch
    {
        OperationState.Pending => "等待中",
        OperationState.Running => "进行中",
        OperationState.Completed => "已完成",
        OperationState.Canceled => "已取消",
        OperationState.Failed => Error is { Length: > 0 } reason ? $"失败：{reason}" : "失败",
        _ => "未知"
    };

    /// <summary>请求取消。待执行项由队列直接移除，执行中的项通过令牌中断。</summary>
    public void RequestCancel() => _cts.Cancel();

    internal void MarkRunning()
    {
        State = OperationState.Running;
        Stage = "准备中";
    }

    internal void Update(string stage, double fraction)
    {
        Stage = stage;
        Progress = Math.Clamp(fraction, 0d, 1d);
    }

    internal void MarkCompleted()
    {
        Progress = 1d;
        Stage = "已完成";
        State = OperationState.Completed;
    }

    internal void MarkCanceled()
    {
        Stage = "已取消";
        State = OperationState.Canceled;
    }

    internal void MarkFailed(string reason)
    {
        Error = reason;
        Stage = "失败";
        State = OperationState.Failed;
    }

    internal void Dispose() => _cts.Dispose();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
