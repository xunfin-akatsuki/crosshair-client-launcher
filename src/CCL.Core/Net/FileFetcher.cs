using CCL.Core.Diagnostics;
using CCL.Core.Hashing;

namespace CCL.Core.Net;

/// <summary>
/// 单文件取回：先按哈希跳过，失败按候选源依次降级，落盘一律先写 .part 再原子替换。
/// 校验不通过时不会留下可用文件，避免“看起来存在但内容是坏的”。
/// </summary>
public sealed class FileFetcher(IHttpTransport transport, MirrorRouter router, CclLog? log = null)
{
    private const int BufferSize = 128 * 1024;
    private const int AttemptsPerSource = 2;

    /// <summary>连续这么久没有收到任何字节就判定连接已死，避免整批任务卡在一个静止的流上。</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<FetchOutcome> FetchAsync(DownloadItem item, IProgress<long>? progress,
        CancellationToken token = default)
    {
        var existing = await InspectExistingAsync(item, token).ConfigureAwait(false);
        if (existing is { } size) return FetchOutcome.Skipped(size);

        var candidates = router.Resolve(item.Url);
        var errors = new List<string>();

        foreach (var target in candidates)
        {
            for (var attempt = 1; attempt <= AttemptsPerSource; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var written = await FetchFromSourceAsync(item, target, progress, token).ConfigureAwait(false);
                    router.ReportSuccess(target.SourceName);
                    return FetchOutcome.Succeeded(written);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (HashMismatchException ex)
                {
                    // 内容不对通常是源给错了，同一源再试一次，仍不对就换源
                    router.ReportFailure(target.SourceName);
                    errors.Add($"{target.SourceName}: {ex.Message}");
                    log?.Warn("Fetch", $"{item.TargetPath} 校验失败（{target.SourceName} 第 {attempt} 次）");
                }
                catch (Exception ex)
                {
                    router.ReportFailure(target.SourceName);
                    errors.Add($"{target.SourceName}: {ex.Message}");
                    log?.Warn("Fetch", $"{item.SourceLabel} 取回失败（{target.SourceName}）：{ex.Message}");
                    break;
                }
            }
        }

        return FetchOutcome.Failed(errors.Count == 0 ? "没有可用的下载源" : string.Join("; ", errors));
    }

    /// <summary>返回已存在文件的大小，表示可以使用；null 表示还需要下载。</summary>
    private static async Task<long?> InspectExistingAsync(DownloadItem item, CancellationToken token)
    {
        if (!File.Exists(item.TargetPath)) return null;
        var info = new FileInfo(item.TargetPath);
        if (item.Hash is null) return info.Length;

        var actual = await FileHasher.ComputeAsync(item.TargetPath, item.HashKind, token).ConfigureAwait(false);
        if (FileHasher.Matches(item.Hash, actual)) return info.Length;

        TryDelete(item.TargetPath);
        return null;
    }

    private async Task<long> FetchFromSourceAsync(DownloadItem item, MirrorTarget target,
        IProgress<long>? progress, CancellationToken token)
    {
        var partPath = item.TargetPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(item.TargetPath))!);

        var resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        using var response = await transport
            .FetchAsync(target.Uri, resumeFrom > 0 ? resumeFrom : null, token)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

        // 服务端不接受续传时会返回 200 全量内容，此时必须丢弃已有片段从头写
        var startOffset = response.RangeHonored ? resumeFrom : 0;

        var mode = startOffset > 0 ? FileMode.Append : FileMode.Create;
        long written = startOffset;

        await using (var file = new FileStream(partPath, mode, FileAccess.Write, FileShare.None, BufferSize, true))
        {
            var buffer = new byte[BufferSize];
            using var readGuard = CancellationTokenSource.CreateLinkedTokenSource(token);
            readGuard.CancelAfter(InactivityTimeout);

            while (true)
            {
                int read;
                try
                {
                    read = await response.Content.ReadAsync(buffer, readGuard.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new IOException($"下载停滞超过 {InactivityTimeout.TotalSeconds:F0} 秒");
                }

                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                written += read;
                readGuard.CancelAfter(InactivityTimeout);
                progress?.Report(written);
            }
        }

        if (item.Size is { } expected && written != expected)
            log?.Warn("Fetch", $"{item.TargetPath} 长度不符：期望 {expected}，实际 {written}");

        if (item.Hash is { } expectedHash)
        {
            var actual = await FileHasher.ComputeAsync(partPath, item.HashKind, token).ConfigureAwait(false);
            if (!FileHasher.Matches(expectedHash, actual))
            {
                TryDelete(partPath);
                throw new HashMismatchException($"哈希不匹配：期望 {expectedHash}，实际 {actual}");
            }
        }

        File.Move(partPath, item.TargetPath, overwrite: true);
        progress?.Report(written);
        return written;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class HashMismatchException(string message) : Exception(message);
}
