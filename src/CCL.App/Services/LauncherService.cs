using System.Diagnostics;
using System.IO;
using CCL.Core.Configuration;
using CCL.Core.Accounts;
using CCL.Core.Diagnostics;
using CCL.Core.Launching;
using CCL.Core.Layout;
using CCL.Core.Minecraft.Instances;
using CCL.Core.Minecraft.Versioning;
using CCL.Core.Net;

namespace CCL.App.Services;

/// <summary>
/// 把 Core 里的下载、安装、启动能力包成界面能直接调用的形式。
/// 所有重活都在这层，ViewModel 只负责状态与命令。
/// </summary>
public sealed class LauncherService : IDisposable
{
    private readonly CclHttp _http;
    private readonly MirrorRouter _router;
    private readonly FileFetcher _fetcher;
    private readonly VersionInstaller _installer;
    private readonly GameLauncher _launcher;
    private readonly JsonApiClient _apiClient;
    private VersionManifest? _manifest;
    private bool _disposed;

    public LauncherService(CclLayout layout, CclSettings settings, CclLog log)
    {
        Layout = layout;
        Settings = settings;
        Log = log;

        _http = new CclHttp(Math.Max(2, settings.MaxConcurrentDownloads), 4, 3, log: log);
        _router = new MirrorRouter(MirrorRouter.CreateBmclapiRules(), settings.UseMirror);
        _fetcher = new FileFetcher(_http, _router, log);
        _installer = new VersionInstaller(_fetcher, log);
        _launcher = new GameLauncher(log);

        _apiClient = new JsonApiClient();
        var authenticator = new MicrosoftAuthenticator(_apiClient, () => Settings.MicrosoftClientId ?? string.Empty);
        var store = new AccountStore(Path.Combine(Layout.RootPath, "accounts.json"), new DpapiSecretProtector());
        Accounts = new AccountManager(store, authenticator, log);
    }

    public CclLayout Layout { get; }

    public CclSettings Settings { get; }

    public AccountManager Accounts { get; }

    public CclLog Log { get; }

    public IReadOnlyList<InstanceInfo> ScanInstances() => InstanceScanner.Scan(Layout.VersionsPath);

    public async Task<IReadOnlyList<ManifestVersion>> GetVersionsAsync(bool includeSnapshots,
        CancellationToken token = default)
    {
        _manifest ??= await LoadManifestAsync(token).ConfigureAwait(false);
        return includeSnapshots
            ? _manifest.Versions
            : _manifest.Releases().ToList();
    }

    public async Task<InstallResult> InstallAsync(ManifestVersion version, IProgress<InstallProgress>? progress,
        CancellationToken token = default)
    {
        var versionDirectory = Path.Combine(Layout.VersionsPath, version.Id);
        Directory.CreateDirectory(versionDirectory);

        var metaPath = Path.Combine(versionDirectory, $"{version.Id}.json");
        if (!File.Exists(metaPath))
            await _fetcher.FetchAsync(new DownloadItem(new Uri(version.Url), metaPath) { Hash = version.Sha1 },
                null, token).ConfigureAwait(false);

        var meta = VersionMeta.Load(metaPath);
        var options = new InstallOptions
        {
            DownloadAssets = true,
            DownloadLogging = true,
            RuleContext = RuleContext.Current()
        };

        return await _installer.InstallAsync(meta, Layout, options, progress,
            Math.Max(2, Settings.MaxConcurrentDownloads), token).ConfigureAwait(false);
    }

    /// <summary>启动实例。正版账号会在这里按需刷新令牌，失败会抛出可读的异常。</summary>
    public async Task<Process> LaunchAsync(InstanceInfo instance, CancellationToken token = default)
    {
        var meta = VersionMeta.Load(instance.JsonPath);
        var required = meta.JavaVersion?.MajorVersion ?? 8;

        var runtimes = JavaLocator.Discover([Path.Combine(Layout.MinecraftPath, "runtime")]);
        var java = JavaLocator.Select(runtimes, required)
                   ?? throw new InvalidOperationException(
                       $"未找到 Java {required} 或更高版本，请先安装后再启动");

        var account = await Accounts.ResolveLaunchAccountAsync(null, token).ConfigureAwait(false);

        var command = _launcher.Build(meta, java, account, new LaunchOptions
        {
            Layout = Layout,
            VersionId = instance.Id,
            MaxMemoryMb = Settings.MaxMemoryMb
        });

        var logPath = Path.Combine(Layout.LogsPath, $"game-{instance.Id}-latest.log");
        return _launcher.Start(command, logPath);
    }

    private async Task<VersionManifest> LoadManifestAsync(CancellationToken token)
    {
        var cachePath = Path.Combine(Layout.CachePath, "version_manifest_v2.json");

        // 清单没有官方哈希，用文件时间做六小时缓存，避免每次开下载页都重新拉
        if (File.Exists(cachePath) &&
            DateTimeOffset.UtcNow - new DateTimeOffset(File.GetLastWriteTimeUtc(cachePath), TimeSpan.Zero)
            < TimeSpan.FromHours(6))
            return VersionManifest.Parse(await File.ReadAllTextAsync(cachePath, token).ConfigureAwait(false));

        var url = new Uri("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json");
        var outcome = await _fetcher.FetchAsync(new DownloadItem(url, cachePath), null, token)
            .ConfigureAwait(false);
        if (outcome.Status == FetchStatus.Failed)
            throw new InvalidOperationException($"获取版本清单失败：{outcome.Error}");

        return VersionManifest.Parse(await File.ReadAllTextAsync(cachePath, token).ConfigureAwait(false));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _apiClient.Dispose();
        _http.Dispose();
    }
}
