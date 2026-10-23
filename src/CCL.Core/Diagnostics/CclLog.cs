namespace CCL.Core.Diagnostics;

public enum CclLogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

public sealed record CclLogEntry(DateTimeOffset Timestamp, CclLogLevel Level, string Category, string Message);

/// <summary>
/// 同时写文件与内存环形缓冲的日志。UI 订阅 <see cref="Written"/> 追加显示，
/// 启动失败时可以从缓冲里取上下文做诊断。
/// </summary>
public sealed class CclLog : IDisposable
{
    private const int BufferCapacity = 2000;

    private readonly object _gate = new();
    private readonly Queue<CclLogEntry> _buffer = new(BufferCapacity);
    private readonly StreamWriter? _writer;
    private readonly string? _filePath;
    private bool _disposed;

    public CclLog(string? directory = null)
    {
        if (directory is null) return;
        try
        {
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory, $"ccl-{DateTimeOffset.Now:yyyyMMdd}.log");
            _writer = new StreamWriter(new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
        }
        catch (IOException)
        {
            // 日志不可写不应阻断启动器启动
            _writer = null;
        }
        catch (UnauthorizedAccessException)
        {
            _writer = null;
        }
    }

    public event Action<CclLogEntry>? Written;

    public string? FilePath => _filePath;

    public void Debug(string category, string message) => Write(CclLogLevel.Debug, category, message);

    public void Info(string category, string message) => Write(CclLogLevel.Info, category, message);

    public void Warn(string category, string message) => Write(CclLogLevel.Warn, category, message);

    public void Error(string category, string message, Exception? exception = null) =>
        Write(CclLogLevel.Error, category, exception is null ? message : $"{message}: {exception}");

    public void Write(CclLogLevel level, string category, string message)
    {
        var entry = new CclLogEntry(DateTimeOffset.Now, level, category, message);
        lock (_gate)
        {
            if (_disposed) return;
            _buffer.Enqueue(entry);
            while (_buffer.Count > BufferCapacity) _buffer.Dequeue();
            _writer?.WriteLine(
                $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LevelTag(level)}] [{category}] {message}");
        }

        Written?.Invoke(entry);
    }

    public IReadOnlyList<CclLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _buffer.ToArray();
        }
    }

    private static string LevelTag(CclLogLevel level) => level switch
    {
        CclLogLevel.Debug => "DBG",
        CclLogLevel.Info => "INF",
        CclLogLevel.Warn => "WRN",
        CclLogLevel.Error => "ERR",
        _ => "???"
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
        }
    }
}
