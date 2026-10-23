using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CCL.Core.Launching;

public sealed record JavaRuntime(string ExecutablePath, string Home, int MajorVersion, string Arch,
    string? Vendor = null)
{
    public bool Matches(int requiredMajor) => MajorVersion == requiredMajor;

    public override string ToString() => $"Java {MajorVersion} ({Arch}) @ {Home}";
}

/// <summary>
/// 查找本机可用的 Java。只看文件系统与 PATH，不碰注册表——这样 Core 保持平台中立，
/// 也能被跨平台单测覆盖。代价是免安装且未写注册表的 JRE 需要靠 extraRoots 指定。
/// </summary>
public static partial class JavaLocator
{
    private static readonly string[] CommonRootNames =
    [
        "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu", "BellSoft",
        "Amazon Corretto", "Semeru", "OpenJDK", "AdoptOpenJDK", "JetBrains"
    ];

    /// <summary>扫描环境变量、PATH 与常见安装目录。<paramref name="extraRoots"/> 可加入 .minecraft/runtime 之类的目录。</summary>
    public static IReadOnlyList<JavaRuntime> Discover(IEnumerable<string>? extraRoots = null)
    {
        var candidates = new List<string>();

        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome)
            AddJavaFromHome(candidates, javaHome);

        foreach (var dir in EnumeratePathDirectories())
            AddJavaExecutable(candidates, dir);

        foreach (var root in DefaultInstallRoots())
            AddChildren(candidates, root);

        if (extraRoots is not null)
        {
            foreach (var root in extraRoots)
            {
                AddJavaFromHome(candidates, root);
                foreach (var child in SafeDirectories(root)) AddJavaFromHome(candidates, child);
            }
        }

        var runtimes = new List<JavaRuntime>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var executable in candidates)
        {
            if (!seen.Add(Path.GetFullPath(executable))) continue;
            if (Probe(executable) is { } runtime) runtimes.Add(runtime);
        }

        return runtimes.OrderByDescending(r => r.MajorVersion).ToList();
    }

    /// <summary>挑选满足要求的主版本：优先精确匹配，否则取不小于要求里的最小值。</summary>
    public static JavaRuntime? Select(IReadOnlyList<JavaRuntime> runtimes, int requiredMajor)
    {
        var exact = runtimes.FirstOrDefault(r => r.MajorVersion == requiredMajor);
        if (exact is not null) return exact;

        return runtimes
            .Where(r => r.MajorVersion > requiredMajor)
            .OrderBy(r => r.MajorVersion)
            .FirstOrDefault();
    }

    /// <summary>直接读取某个 JAVA_HOME 的信息，失败返回 null。</summary>
    public static JavaRuntime? Probe(string javaExecutablePath)
    {
        if (!File.Exists(javaExecutablePath)) return null;

        var home = Path.GetDirectoryName(Path.GetDirectoryName(javaExecutablePath));
        if (home is null) return null;

        var releasePath = Path.Combine(home, "release");
        if (File.Exists(releasePath) && ParseReleaseFile(releasePath) is { } fromFile)
            return fromFile with { ExecutablePath = javaExecutablePath, Home = home };

        return ProbeByExecuting(javaExecutablePath, home);
    }

    public static int ParseMajorVersion(string version)
    {
        // 1.8.0_412 这类老格式主版本在第二段
        var normalized = version.Trim().Trim('"');
        if (normalized.StartsWith("1.", StringComparison.Ordinal))
        {
            var second = normalized.Split('.')[1];
            return int.TryParse(second, out var legacy) ? legacy : 0;
        }

        var head = normalized.Split('.', '-', '_', '+')[0];
        return int.TryParse(head, out var major) ? major : 0;
    }

    private static JavaRuntime? ParseReleaseFile(string path)
    {
        try
        {
            string? version = null;
            string? arch = null;
            string? vendor = null;

            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                    version = line["JAVA_VERSION=".Length..].Trim('"');
                else if (line.StartsWith("OS_ARCH=", StringComparison.Ordinal))
                    arch = line["OS_ARCH=".Length..].Trim('"');
                else if (line.StartsWith("IMPLEMENTOR=", StringComparison.Ordinal))
                    vendor = line["IMPLEMENTOR=".Length..].Trim('"');
            }

            if (version is null) return null;
            var major = ParseMajorVersion(version);
            if (major <= 0) return null;

            return new JavaRuntime(string.Empty, string.Empty, major, NormalizeArch(arch), vendor);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JavaRuntime? ProbeByExecuting(string javaExecutablePath, string home)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(javaExecutablePath, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return null;

            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            var match = VersionPattern().Match(output);
            if (!match.Success) return null;

            var major = ParseMajorVersion(match.Groups[1].Value);
            return major <= 0 ? null : new JavaRuntime(javaExecutablePath, home, major, "unknown");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<string> DefaultInstallRoots()
    {
        var roots = new List<string>();
        foreach (var programFiles in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local
                         ? Path.Combine(local, "Programs")
                         : null
                 })
        {
            if (string.IsNullOrWhiteSpace(programFiles)) continue;
            foreach (var name in CommonRootNames) roots.Add(Path.Combine(programFiles, name));
        }

        return roots;
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) yield break;
        foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim().Trim('"');
            if (trimmed.Length > 0 && Directory.Exists(trimmed)) yield return trimmed;
        }
    }

    private static void AddChildren(List<string> candidates, string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var child in SafeDirectories(root))
        {
            AddJavaFromHome(candidates, child);
            // 有些布局是 vendor\product\version\bin
            if (Path.GetFileName(child).Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var grandChild in SafeDirectories(child)) AddJavaFromHome(candidates, grandChild);
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddJavaFromHome(List<string> candidates, string home)
    {
        if (string.IsNullOrWhiteSpace(home)) return;
        AddJavaExecutable(candidates, Path.Combine(home, "bin"));
    }

    private static void AddJavaExecutable(List<string> candidates, string directory)
    {
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (File.Exists(executable)) candidates.Add(executable);
    }

    private static string NormalizeArch(string? raw) => raw?.ToLowerInvariant() switch
    {
        "x86_64" or "amd64" or "x64" => "x64",
        "aarch64" or "arm64" => "arm64",
        "x86" or "i386" or "i586" or "i686" => "x86",
        null or "" => "unknown",
        _ => raw!.ToLowerInvariant()
    };

    [GeneratedRegex(@"version ""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
}
