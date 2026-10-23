using System.Net;

namespace CCL.Core.Net;

/// <summary>
/// 一次 HTTP 取回的原始结果。状态码原样返回（包括 404），由调用方决定是换源还是终止。
/// </summary>
public sealed class FetchResponse : IDisposable
{
    public required HttpStatusCode StatusCode { get; init; }

    /// <summary>完整文件长度：206 时取自 Content-Range，否则取自 Content-Length；都无法确定时为 null。</summary>
    public long? TotalLength { get; init; }

    public bool RangeHonored { get; init; }

    public string? ETag { get; init; }

    public required Stream Content { get; init; }

    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;

    public void Dispose() => Content.Dispose();
}

public interface IHttpTransport
{
    Task<FetchResponse> FetchAsync(Uri uri, long? rangeStart, CancellationToken token);
}
