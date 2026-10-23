namespace CCL.Core.Net;

public enum FetchStatus
{
    Succeeded,
    Skipped,
    Failed
}

public sealed record FetchOutcome(FetchStatus Status, long Bytes, string? Error = null)
{
    public static FetchOutcome Succeeded(long bytes) => new(FetchStatus.Succeeded, bytes);

    public static FetchOutcome Skipped(long bytes = 0) => new(FetchStatus.Skipped, bytes);

    public static FetchOutcome Failed(string error) => new(FetchStatus.Failed, 0, error);
}

public sealed record DownloadFailure(Uri Url, string TargetPath, string Reason);

public sealed record BatchResult(int Succeeded, int Skipped, int Failed, IReadOnlyList<DownloadFailure> Failures)
{
    public int Total => Succeeded + Skipped + Failed;

    public bool AllSucceeded => Failed == 0;
}

/// <summary>
/// 批量进度。总大小未知时按条目数估算，所以两个来源都保留，由 <see cref="Fraction"/> 挑可用的那个。
/// </summary>
public sealed record BatchProgress(long CompletedItems, int TotalItems, long CompletedBytes, long TotalBytes)
{
    public double Fraction
    {
        get
        {
            if (TotalBytes > 0) return Math.Clamp((double)CompletedBytes / TotalBytes, 0d, 1d);
            return TotalItems > 0 ? Math.Clamp((double)CompletedItems / TotalItems, 0d, 1d) : 0d;
        }
    }
}
