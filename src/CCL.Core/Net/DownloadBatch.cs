using System.Collections.Concurrent;
using CCL.Core.Diagnostics;

namespace CCL.Core.Net;

/// <summary>
/// 批量下载调度：按目标路径去重、按全局并发跑、汇总字节加权进度。
/// 单项失败不影响其他项，最终在 <see cref="BatchResult"/> 里统一返回。
/// </summary>
public sealed class DownloadBatch(FileFetcher fetcher, int maxConcurrency = 8, CclLog? log = null)
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    public async Task<BatchResult> RunAsync(IReadOnlyList<DownloadItem> items, IProgress<BatchProgress>? progress,
        CancellationToken token = default)
    {
        var unique = Deduplicate(items);
        var state = new BatchState(unique.Count, unique.Sum(t => t.Item.Size ?? 0L));
        var failures = new ConcurrentBag<DownloadFailure>();
        var reportGate = new Lock();

        void Publish(bool force)
        {
            if (progress is null) return;
            lock (reportGate)
            {
                var now = DateTimeOffset.UtcNow;
                if (!force && now - state.LastReport < ProgressInterval) return;
                state.LastReport = now;
                progress.Report(new BatchProgress(state.CompletedItems, state.TotalItems,
                    Interlocked.Read(ref state.CompletedBytes), state.TotalBytes));
            }
        }

        Publish(force: true);

        await Parallel.ForEachAsync(unique,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, maxConcurrency) },
            async (tracked, ct) =>
            {
                var itemProgress = new InlineProgress(bytes =>
                {
                    var delta = bytes - tracked.WrittenBytes;
                    tracked.WrittenBytes = bytes;
                    Interlocked.Add(ref state.CompletedBytes, delta);
                    Publish(force: false);
                });

                var outcome = await fetcher.FetchAsync(tracked.Item, itemProgress, ct).ConfigureAwait(false);

                switch (outcome.Status)
                {
                    case FetchStatus.Succeeded:
                        Interlocked.Increment(ref state.Succeeded);
                        break;
                    case FetchStatus.Skipped:
                        Interlocked.Increment(ref state.Skipped);
                        break;
                    default:
                        Interlocked.Increment(ref state.Failed);
                        failures.Add(new DownloadFailure(tracked.Item.Url, tracked.Item.TargetPath,
                            outcome.Error ?? "未知错误"));
                        break;
                }

                Interlocked.Increment(ref state.CompletedItems);
                Publish(force: true);
            }).ConfigureAwait(false);

        var list = failures.ToList();
        if (list.Count > 0) log?.Warn("Batch", $"{list.Count} 个文件下载失败，共 {unique.Count} 个");
        else log?.Info("Batch", $"批量下载完成，共 {unique.Count} 个文件");

        return new BatchResult(state.Succeeded, state.Skipped, state.Failed, list);
    }

    private static List<TrackedItem> Deduplicate(IReadOnlyList<DownloadItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TrackedItem>(items.Count);
        foreach (var item in items)
        {
            var key = Path.GetFullPath(item.TargetPath);
            if (!seen.Add(key)) continue;
            result.Add(new TrackedItem(item));
        }

        return result;
    }

    private sealed class TrackedItem(DownloadItem item)
    {
        public DownloadItem Item { get; } = item;

        public long WrittenBytes;
    }

    private sealed class BatchState(int totalItems, long totalBytes)
    {
        public readonly int TotalItems = totalItems;
        public readonly long TotalBytes = totalBytes;
        public long CompletedBytes;
        public int CompletedItems;
        public int Succeeded;
        public int Skipped;
        public int Failed;
        public DateTimeOffset LastReport = DateTimeOffset.MinValue;
    }

    /// <summary>同步回调的进度实现，避免 Progress&lt;T&gt; 依赖同步上下文导致回调乱序或延迟。</summary>
    private sealed class InlineProgress(Action<long> callback) : IProgress<long>
    {
        public void Report(long value) => callback(value);
    }
}
