namespace CCL.Core.Layout;

/// <summary>
/// 描述启动器使用的目录布局。数据根目录由 <see cref="CreateDefault"/> 决定：
/// 存在 ccl.portable 标记文件时跟随程序目录，否则使用漫游数据目录。
/// </summary>
public sealed class CclLayout
{
    public CclLayout(string rootPath, string minecraftPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        MinecraftPath = Path.GetFullPath(minecraftPath);
    }

    public string RootPath { get; }

    public string MinecraftPath { get; }

    public string VersionsPath => Path.Combine(MinecraftPath, "versions");

    public string LibrariesPath => Path.Combine(MinecraftPath, "libraries");

    public string AssetsPath => Path.Combine(MinecraftPath, "assets");

    public string LogsPath => Path.Combine(RootPath, "logs");

    public string CachePath => Path.Combine(RootPath, "cache");

    public string SettingsPath => Path.Combine(RootPath, "ccl.json");

    /// <param name="appDirectory">启动器可执行文件所在目录。</param>
    public static CclLayout CreateDefault(string? appDirectory = null)
    {
        var appDir = Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory);
        var portable = File.Exists(Path.Combine(appDir, "ccl.portable"));
        var root = portable
            ? appDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CCL");
        return new CclLayout(root, Path.Combine(root, ".minecraft"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(CachePath);
        Directory.CreateDirectory(MinecraftPath);
        Directory.CreateDirectory(VersionsPath);
        Directory.CreateDirectory(LibrariesPath);
        Directory.CreateDirectory(AssetsPath);
    }
}
