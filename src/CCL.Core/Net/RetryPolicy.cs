using System.Net;

namespace CCL.Core.Net;

/// <summary>
/// 重试判定与退避计算。做成纯函数便于单测，实际发送循环在 <see cref="CclHttp"/> 里。
/// </summary>
public static class RetryPolicy
{
    /// <summary>4xx 除 408/429 外都不重试：文件不存在、参数错，重试只是浪费时间。</summary>
    public static bool IsRetryableStatus(HttpStatusCode status) => (int)status switch
    {
        408 => true,
        429 => true,
        >= 500 => true,
        _ => false
    };

    public static bool IsRetryableException(Exception exception) => exception switch
    {
        HttpRequestException => true,
        TaskCanceledException => true,
        TimeoutException => true,
        IOException => true,
        _ => false
    };

    /// <summary>指数退避加抖动，避免一批任务同时重试再次打垮同一个源。</summary>
    public static TimeSpan ComputeDelay(int attempt, TimeSpan? retryAfter = null, Random? random = null)
    {
        if (retryAfter is { } hint && hint > TimeSpan.Zero)
            return hint > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : hint;

        var baseMs = 250 * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(baseMs, 8000);
        var jitter = (random ?? Random.Shared).Next(0, 250);
        return TimeSpan.FromMilliseconds(capped + jitter);
    }

    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return null;
        if (retryAfter.Delta is { } delta) return delta;
        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
