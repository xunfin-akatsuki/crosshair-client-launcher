using System.Diagnostics;
using CCL.Core.Localization;
using CCL.Core.Configuration;
using CCL.Core.Diagnostics;
using CCL.Core.Layout;
using CCL.Core.Minecraft.Versioning;

namespace CCL.Core.Launching;

public sealed record LaunchOptions
{
    public required CclLayout Layout { get; init; }

    public required string VersionId { get; init; }

    public int MaxMemoryMb { get; init; } = 4096;

    /// <summary>额外 JVM 参数，会插在内存放在的前面。</summary>
    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = [];

    public int? ResolutionWidth { get; init; }

    public int? ResolutionHeight { get; init; }

    public bool DemoUser { get; init; }

    public string? GameDirectoryOverride { get; init; }
}

public sealed record LaunchCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>仅用于日志与诊断，包含完整命令行。</summary>
    public string ToDisplayString() =>
        Executable + " " + string.Join(' ', Arguments.Select(Quote));

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}

/// <summary>
/// 组装并启动游戏进程。日志直接重定向到文件，避免依赖游戏自身的日志配置。
/// </summary>
public sealed class GameLauncher(CclLog? log = null)
{
    public LaunchCommand Build(VersionMeta meta, JavaRuntime java, GameAccount account, LaunchOptions options)
    {
        var versionDirectory = Path.Combine(options.Layout.VersionsPath, options.VersionId);
        var gameDirectory = options.GameDirectoryOverride ?? options.Layout.MinecraftPath;
        var nativesDirectory = Path.Combine(versionDirectory, "natives");

        var classpath = BuildClasspath(meta, options.Layout, versionDirectory, options);
        var assetsIndexName = meta.Assets ?? meta.AssetIndex?.Id ?? "legacy";

        var context = new LaunchContext
        {
            Account = account,
            Meta = meta,
            GameDirectory = gameDirectory,
            AssetsRoot = options.Layout.AssetsPath,
            AssetsIndexName = assetsIndexName,
            NativesDirectory = nativesDirectory,
            Classpath = classpath,
            LauncherVersion = typeof(GameLauncher).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            ResolutionWidth = options.ResolutionWidth,
            ResolutionHeight = options.ResolutionHeight,
            DemoUser = options.DemoUser
        };

        var arguments = new List<string>
        {
            $"-Xmx{options.MaxMemoryMb}M",
            "-Dfile.encoding=UTF-8",
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8"
        };
        arguments.AddRange(options.ExtraJvmArguments);
        arguments.AddRange(ArgumentBuilder.BuildJvm(context));

        if (meta.MainClass is not { Length: > 0 } mainClass)
            throw new InvalidOperationException(CoreText.Format("Launch.NoMainClass", meta.Id));

        arguments.Add(mainClass);
        arguments.AddRange(ArgumentBuilder.BuildGame(context));

        return new LaunchCommand(java.ExecutablePath, arguments, gameDirectory);
    }

    public IReadOnlyList<string> BuildClasspath(VersionMeta meta, CclLayout layout, string versionDirectory,
        LaunchOptions options)
    {
        var entries = new List<string>();

        foreach (var library in LibraryResolver.Resolve(meta.Libraries,
                     new RuleContext(RuleContext.GetOsName(), RuleContext.GetOsArch())))
        {
            var path = Path.Combine(layout.LibrariesPath,
                library.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) entries.Add(path);
        }

        var clientJar = Path.Combine(versionDirectory, $"{options.VersionId}.jar");
        if (File.Exists(clientJar)) entries.Add(clientJar);

        return entries;
    }

    /// <summary>启动进程并把输出重定向到日志文件。返回的进程由调用方负责监视退出。</summary>
    public Process Start(LaunchCommand command, string logFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFilePath))!);

        var startInfo = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in command.Arguments) startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var writer = new StreamWriter(new FileStream(logFilePath, FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite)) { AutoFlush = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (writer) writer.WriteLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (writer) writer.WriteLine(e.Data);
        };
        process.Exited += (_, _) =>
        {
            lock (writer)
            {
                writer.WriteLine($"\n[CCL] 游戏进程已退出，退出码 {process.ExitCode}");
                writer.Flush();
            }
        };

        log?.Info("Launch", $"启动 {command.Executable}，工作目录 {command.WorkingDirectory}，日志 {logFilePath}");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }
}
