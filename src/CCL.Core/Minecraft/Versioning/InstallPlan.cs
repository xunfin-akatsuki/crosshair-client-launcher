using CCL.Core.Hashing;
using CCL.Core.Net;

namespace CCL.Core.Minecraft.Versioning;

public sealed class InstallOptions
{
    /// <summary>是否下载资源对象（assets/objects）。关掉可用于快速验证安装链路。</summary>
    public bool DownloadAssets { get; init; } = true;

    /// <summary>是否下载日志配置（log4j 之类），缺少时 M4 只能用默认日志。</summary>
    public bool DownloadLogging { get; init; } = true;

    /// <summary>安装前先校验再补齐，还是直接覆盖式写入。两者都走同一份计划。</summary>
    public bool VerifyBeforeDownload { get; init; } = true;

    public RuleContext RuleContext { get; init; } = RuleContext.Current();
}

/// <summary>一条待落盘文件。声明了 extract 的库除下载外还需解压到 natives 目录。</summary>
public sealed record PlanFile(DownloadItem Item, bool ExtractNatives, IReadOnlyList<string> ExtractExclude)
{
    public string TargetPath => Item.TargetPath;
}

public sealed record VirtualAssetCopy(string ObjectPath, string TargetPath, string Name);

/// <summary>安装计划：可直接交给 M2 的 DownloadBatch 执行。</summary>
public sealed class InstallPlan
{
    public required string VersionId { get; init; }

    public required string VersionDirectory { get; init; }

    public required string MinecraftDirectory { get; init; }

    public required VersionMeta Meta { get; init; }

    public List<PlanFile> Files { get; } = [];

    public List<VirtualAssetCopy> VirtualAssets { get; } = [];

    public long TotalBytes => Files.Sum(f => f.Item.Size ?? 0L);

    public int CountBy(Func<PlanFile, bool> predicate) => Files.Count(predicate);

    public string ClientJarPath => Path.Combine(VersionDirectory, $"{VersionId}.jar");

    public string VersionJsonPath => Path.Combine(VersionDirectory, $"{VersionId}.json");

    public string NativesDirectory => Path.Combine(VersionDirectory, "natives");

    public string AssetsObjectsPath => Path.Combine(MinecraftDirectory, "assets", "objects");

    public string AssetsIndexesPath => Path.Combine(MinecraftDirectory, "assets", "indexes");

    public string LibrariesPath => Path.Combine(MinecraftDirectory, "libraries");
}

public sealed record InstallResult(int Downloaded, int Skipped, int Failed, IReadOnlyList<DownloadFailure> Failures,
    int NativesExtracted)
{
    public bool AllSucceeded => Failed == 0;
}

public sealed record VerifyReport(int Ok, int Missing, int Corrupted)
{
    public bool IsComplete => Missing == 0 && Corrupted == 0;
}
