using CCL.Core.Hashing;
using CCL.Core.Layout;
using CCL.Core.Net;

namespace CCL.Core.Minecraft.Versioning;

public static class InstallPlanner
{
    private const string AssetBaseUrl = "https://resources.download.minecraft.net";

    /// <summary>构造不依赖网络的安装计划核心部分：客户端 jar、库、日志配置。</summary>
    public static InstallPlan BuildCore(VersionMeta meta, CclLayout layout, InstallOptions options)
    {
        var versionDirectory = Path.Combine(layout.VersionsPath, meta.Id);
        var plan = new InstallPlan
        {
            VersionId = meta.Id,
            VersionDirectory = versionDirectory,
            MinecraftDirectory = layout.MinecraftPath,
            Meta = meta
        };

        if (meta.Downloads?.Client is { Url: { Length: > 0 } clientUrl } client)
            plan.Files.Add(new PlanFile(
                new DownloadItem(new Uri(clientUrl), plan.ClientJarPath)
                {
                    Hash = client.Sha1,
                    Size = client.Size > 0 ? client.Size : null
                }, false, []));

        if (meta.AssetIndex is { Url.Length: > 0 } assetIndex)
            plan.Files.Add(new PlanFile(
                new DownloadItem(new Uri(assetIndex.Url),
                    Path.Combine(plan.AssetsIndexesPath, $"{assetIndex.Id}.json"))
                {
                    Hash = assetIndex.Sha1,
                    Size = assetIndex.Size > 0 ? assetIndex.Size : null
                }, false, []));

        if (options.DownloadLogging && meta.Logging?.Client?.File is { Url.Length: > 0 } logFile)
            plan.Files.Add(new PlanFile(
                new DownloadItem(new Uri(logFile.Url),
                    Path.Combine(layout.AssetsPath, "log_configs", logFile.Id))
                {
                    Hash = logFile.Sha1,
                    Size = logFile.Size > 0 ? logFile.Size : null
                }, false, []));

        foreach (var library in LibraryResolver.Resolve(meta.Libraries, options.RuleContext))
        {
            var target = Path.Combine(plan.LibrariesPath, library.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            plan.Files.Add(new PlanFile(
                new DownloadItem(new Uri(library.Artifact.Url!), target)
                {
                    Hash = library.Artifact.Sha1,
                    Size = library.Artifact.Size > 0 ? library.Artifact.Size : null
                }, library.ExtractNatives, library.Exclude));
        }

        return plan;
    }

    /// <summary>把资源索引展开成对象下载项。对象地址由哈希推导，不需要逐个请求。</summary>
    public static void AddAssetFiles(InstallPlan plan, AssetIndex index)
    {
        foreach (var (name, entry) in index.Objects)
        {
            var target = Path.Combine(plan.AssetsObjectsPath,
                AssetIndex.ObjectRelativePath(entry.Hash).Replace('/', Path.DirectorySeparatorChar));
            plan.Files.Add(new PlanFile(
                new DownloadItem(AssetIndex.ObjectUrl(entry.Hash, AssetBaseUrl), target)
                {
                    Hash = entry.Hash,
                    Size = entry.Size > 0 ? entry.Size : null
                }, false, []));

            if (index.Virtual)
                plan.VirtualAssets.Add(new VirtualAssetCopy(target,
                    Path.Combine(plan.MinecraftDirectory, "assets", "virtual", "legacy",
                        name.Replace('/', Path.DirectorySeparatorChar)), name));
            else if (index.MapToResources)
                plan.VirtualAssets.Add(new VirtualAssetCopy(target,
                    Path.Combine(plan.MinecraftDirectory, "resources", name.Replace('/', Path.DirectorySeparatorChar)),
                    name));
        }
    }
}
