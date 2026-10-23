using System.IO.Compression;
using CCL.Core.Configuration;
using CCL.Core.Diagnostics;
using CCL.Core.Hashing;
using CCL.Core.Layout;
using CCL.Core.Net;

namespace CCL.Core.Minecraft.Versioning;

public sealed record InstallProgress(string Stage, double Fraction, int Done, int Total);

/// <summary>
/// 执行安装计划：下载、解压 natives、写版本 JSON、复制虚拟资源、完整性校验。
/// 所有下载都走 M2 的 DownloadBatch，因此天然具备跳过、降级、校验和原子落盘。
/// </summary>
public sealed class VersionInstaller(FileFetcher fetcher, CclLog? log = null)
{
    public async Task<InstallResult> InstallAsync(VersionMeta meta, CclLayout layout, InstallOptions options,
        IProgress<InstallProgress>? progress = null, int maxConcurrency = 8, CancellationToken token = default)
    {
        var plan = InstallPlanner.BuildCore(meta, layout, options);
        Directory.CreateDirectory(plan.VersionDirectory);
        Directory.CreateDirectory(plan.NativesDirectory);
        File.WriteAllText(plan.VersionJsonPath, meta.ToJson());
        log?.Info("Install", $"开始安装 {meta.Id}：核心文件 {plan.Files.Count} 个");

        var batch = new DownloadBatch(fetcher, maxConcurrency, log);
        var coreProgress = Wrap(progress, "核心文件", 0d, 0.45d);
        var core = await batch.RunAsync(plan.Files.ToList().Select(p => p.Item).ToList(), coreProgress, token)
            .ConfigureAwait(false);
        var failures = new List<DownloadFailure>(core.Failures);
        var downloaded = core.Succeeded;
        var skipped = core.Skipped;

        if (options.DownloadAssets && meta.AssetIndex is { } assetIndexRef)
        {
            var indexPath = Path.Combine(plan.AssetsIndexesPath, $"{assetIndexRef.Id}.json");
            if (File.Exists(indexPath))
            {
                var index = AssetIndex.Load(indexPath);
                InstallPlanner.AddAssetFiles(plan, index);
                var assetItems = plan.Files
                    .Where(f => f.Item.TargetPath.StartsWith(plan.AssetsObjectsPath, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Item)
                    .ToList();

                log?.Info("Install", $"资源对象 {assetItems.Count} 个");
                var assetProgress = Wrap(progress, "资源文件", 0.45d, 0.5d);
                var assets = await batch.RunAsync(assetItems, assetProgress, token).ConfigureAwait(false);
                failures.AddRange(assets.Failures);
                downloaded += assets.Succeeded;
                skipped += assets.Skipped;

                await MaterializeVirtualAssetsAsync(plan, token).ConfigureAwait(false);
            }
            else
            {
                log?.Warn("Install", "资源索引文件缺失，跳过资源下载");
            }
        }
        else
        {
            // 未下载资源时也要保证核心文件都到位，前面已经处理
        }

        progress?.Report(new InstallProgress("解压原生库", 0d, 0, 1));
        var extracted = await Task.Run(() => ExtractNatives(plan), token).ConfigureAwait(false);
        progress?.Report(new InstallProgress("完成", 1d, 1, 1));

        log?.Info("Install",
            $"{meta.Id} 安装结束：下载 {downloaded}，跳过 {skipped}，失败 {failures.Count}，解压原生库 {extracted} 个");
        return new InstallResult(downloaded, skipped, failures.Count, failures, extracted);
    }

    /// <summary>只校验不下载，用于“补全”前的状态判断。</summary>
    public async Task<VerifyReport> VerifyAsync(InstallPlan plan, CancellationToken token = default)
    {
        int ok = 0, missing = 0, corrupted = 0;

        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(file.Item.TargetPath))
            {
                missing++;
                continue;
            }

            if (file.Item.Hash is not { Length: > 0 })
            {
                ok++;
                continue;
            }

            var actual = await FileHasher
                .ComputeAsync(file.Item.TargetPath, file.Item.HashKind, token)
                .ConfigureAwait(false);
            if (FileHasher.Matches(file.Item.Hash, actual)) ok++;
            else corrupted++;
        }

        return new VerifyReport(ok, missing, corrupted);
    }

    private static int ExtractNatives(InstallPlan plan)
    {
        var count = 0;
        Directory.CreateDirectory(plan.NativesDirectory);

        foreach (var native in plan.Files.Where(f => f.ExtractNatives))
        {
            if (!File.Exists(native.TargetPath)) continue;

            try
            {
                using var archive = ZipFile.OpenRead(native.TargetPath);
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;
                    if (IsExcluded(entry.FullName, native.ExtractExclude)) continue;

                    var destination = Path.Combine(plan.NativesDirectory,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                    count++;
                }
            }
            catch (InvalidDataException)
            {
                // 不是合法 zip 就跳过，避免一个坏库阻断整次安装
            }
        }

        return count;
    }

    private static bool IsExcluded(string entryName, IReadOnlyList<string> excludes)
    {
        foreach (var prefix in excludes)
        {
            if (entryName.StartsWith(prefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static async Task MaterializeVirtualAssetsAsync(InstallPlan plan, CancellationToken token)
    {
        foreach (var copy in plan.VirtualAssets)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(copy.ObjectPath)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(copy.TargetPath)!);
            await using (var source = File.OpenRead(copy.ObjectPath))
            await using (var target = new FileStream(copy.TargetPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 64 * 1024, useAsync: true))
            {
                await source.CopyToAsync(target, token).ConfigureAwait(false);
            }
        }
    }

    private static IProgress<BatchProgress>? Wrap(IProgress<InstallProgress>? outer, string stage,
        double start, double span) =>
        outer is null
            ? null
            : new StageProgress(outer, stage, start, span);

    private sealed class StageProgress(IProgress<InstallProgress> outer, string stage, double start, double span)
        : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) =>
            outer.Report(new InstallProgress(stage, start + value.Fraction * span, (int)value.CompletedItems,
                value.TotalItems));
    }
}
