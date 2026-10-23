using System.Net;
using CCL.Core.Net;

namespace CCL.Core.Tests;

internal sealed class FakeTransport : IHttpTransport
{
    private readonly Lock _gate = new();
    private int _inFlight;

    /// <summary>(uri, rangeStart) → 响应；抛异常即模拟网络失败。</summary>
    public Func<Uri, long?, FetchResponse> Handler { get; set; } = (_, _) => throw new InvalidOperationException("未设置处理器");

    public List<(Uri Uri, long? Range)> Requests { get; } = [];

    public int MaxInFlight { get; private set; }

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public async Task<FetchResponse> FetchAsync(Uri uri, long? rangeStart, CancellationToken token)
    {
        lock (_gate)
        {
            Requests.Add((uri, rangeStart));
            _inFlight++;
            if (_inFlight > MaxInFlight) MaxInFlight = _inFlight;
        }

        try
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, token).ConfigureAwait(false);
            return Handler(uri, rangeStart);
        }
        finally
        {
            lock (_gate) _inFlight--;
        }
    }

    public static FetchResponse Bytes(byte[] content, HttpStatusCode status = HttpStatusCode.OK,
        bool rangeHonored = false, long? totalLength = null) => new()
    {
        StatusCode = status,
        TotalLength = totalLength ?? content.Length,
        RangeHonored = rangeHonored,
        ETag = "\"fake\"",
        Content = new MemoryStream(content, writable: false)
    };

    /// <summary>构造一个支持 Range 的响应：从 rangeStart 处返回剩余内容。</summary>
    public static FetchResponse Ranged(byte[] content, long? rangeStart)
    {
        if (rangeStart is { } start && start > 0 && start <= content.Length)
        {
            var rest = content[(int)start..];
            return Bytes(rest, HttpStatusCode.PartialContent, rangeHonored: true, totalLength: content.Length);
        }

        return Bytes(content);
    }
}
