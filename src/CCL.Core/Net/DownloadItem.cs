using CCL.Core.Hashing;

namespace CCL.Core.Net;

/// <summary>
/// 一个下载任务的描述。哈希与大小都可空：缺失时跳过校验、按未知大小处理。
/// </summary>
public sealed record DownloadItem(Uri Url, string TargetPath)
{
    public string? Hash { get; init; }

    public HashAlgorithmKind HashKind { get; init; } = HashAlgorithmKind.Sha1;

    public long? Size { get; init; }

    /// <summary>日志里用的可读来源名，例如 libraries.minecraft.net。</summary>
    public string? SourceLabel => Url.Host;
}
