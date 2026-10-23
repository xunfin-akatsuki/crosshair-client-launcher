using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using CCL.Core.Diagnostics;

namespace CCL.Core.Net;

/// <summary>
/// 全进程共用的 HTTP 客户端：连接池、两级并发闸门、失败重试。
/// 注意 <see cref="HttpClient.Timeout"/> 设为无限——真实超时由发送阶段的短超时和
/// 下载阶段的不活动探测共同负责，否则大文件会被整体超时掐断。
/// </summary>
public sealed class CclHttp : IHttpTransport, IDisposable
{
    public static string UserAgent { get; } =
        $"CrosshairClientLauncher/{typeof(CclHttp).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _globalGate;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxAttempts;
    private readonly int _perHostConcurrency;
    private readonly TimeSpan _headerTimeout;
    private readonly CclLog? _log;
    private bool _disposed;

    public CclHttp(int maxConcurrent = 8, int perHostConcurrency = 4, int maxAttempts = 3,
        TimeSpan? headerTimeout = null, CclLog? log = null)
    {
        _maxAttempts = Math.Max(1, maxAttempts);
        _perHostConcurrency = Math.Max(1, perHostConcurrency);
        _headerTimeout = headerTimeout ?? TimeSpan.FromSeconds(30);
        _log = log;

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = Math.Max(2, perHostConcurrency * 2)
        };

        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _globalGate = new SemaphoreSlim(Math.Max(1, maxConcurrent));
    }

    public async Task<FetchResponse> FetchAsync(Uri uri, long? rangeStart, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var hostGate = _hostGates.GetOrAdd(uri.Host, _ => new SemaphoreSlim(_perHostConcurrency));

        await _globalGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await hostGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await FetchWithRetryAsync(uri, rangeStart, token).ConfigureAwait(false);
            }
            finally
            {
                hostGate.Release();
            }
        }
        finally
        {
            _globalGate.Release();
        }
    }

    private async Task<FetchResponse> FetchWithRetryAsync(Uri uri, long? rangeStart, CancellationToken token)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            HttpRequestMessage? request = null;

            try
            {
                request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (rangeStart is { } start)
                    request.Headers.Range = new RangeHeaderValue(start, null);

                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                headerTimeout.CancelAfter(_headerTimeout);

                var response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
                    .ConfigureAwait(false);

                if (RetryPolicy.IsRetryableStatus(response.StatusCode) && attempt < _maxAttempts)
                {
                    var retryAfter = RetryPolicy.ParseRetryAfter(response);
                    response.Dispose();
                    await DelayAsync(RetryPolicy.ComputeDelay(attempt, retryAfter), uri, attempt, token)
                        .ConfigureAwait(false);
                    continue;
                }

                return await ToFetchResponseAsync(response, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (RetryPolicy.IsRetryableException(ex) && attempt < _maxAttempts)
            {
                lastError = ex;
                await DelayAsync(RetryPolicy.ComputeDelay(attempt), uri, attempt, token).ConfigureAwait(false);
            }
            finally
            {
                request?.Dispose();
            }
        }

        throw new HttpRequestException($"请求 {uri} 失败，已重试 {_maxAttempts} 次", lastError);
    }

    private async Task DelayAsync(TimeSpan delay, Uri uri, int attempt, CancellationToken token)
    {
        _log?.Warn("Net", $"{uri.Host} 第 {attempt} 次尝试失败，{delay.TotalMilliseconds:F0}ms 后重试");
        await Task.Delay(delay, token).ConfigureAwait(false);
    }

    private static async Task<FetchResponse> ToFetchResponseAsync(HttpResponseMessage response,
        CancellationToken token)
    {
        var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var rangeHonored = response.StatusCode == HttpStatusCode.PartialContent;

        long? total = response.Content.Headers.ContentRange?.Length
                      ?? (rangeHonored ? null : response.Content.Headers.ContentLength);

        return new FetchResponse
        {
            StatusCode = response.StatusCode,
            TotalLength = total,
            RangeHonored = rangeHonored,
            ETag = response.Headers.ETag?.Tag,
            Content = stream
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        _globalGate.Dispose();
        foreach (var gate in _hostGates.Values) gate.Dispose();
    }
}
