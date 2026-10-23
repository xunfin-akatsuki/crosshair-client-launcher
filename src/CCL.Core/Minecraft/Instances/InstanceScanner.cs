using CCL.Core.Localization;
using CCL.Core.Minecraft.Versioning;

namespace CCL.Core.Minecraft.Instances;

/// <summary>实例目录里能直接看到的信息。详细参数在启动时才读完整 JSON。</summary>
public sealed record InstanceInfo(
    string Id,
    string Type,
    string Directory,
    string JsonPath,
    bool HasClientJar,
    long ClientJarBytes,
    int? RequiredJavaMajor,
    DateTimeOffset LastModified)
{
    public string JavaLabel => RequiredJavaMajor is { } major ? $"Java {major}" : CoreText.Get("Instance.JavaUnknown");

    /// <summary>列表里显示的副标题。缺少客户端 jar 说明安装被中断，需要提示用户补全。</summary>
    public string SubtitleLabel =>
        IsComplete ? $"{Type} · {JavaLabel}" : $"{Type} · {CoreText.Get("Instance.Incomplete")}";

    /// <summary>只有客户端 jar 到位才算可启动。</summary>
    public bool IsComplete => HasClientJar;

    public string SizeLabel => HasClientJar
        ? $"{ClientJarBytes / 1024d / 1024d:F1} MB"
        : CoreText.Get("Instance.MissingJar");
}

public static class InstanceScanner
{
    /// <summary>扫描 versions 目录。坏掉的 JSON 会被跳过而不是让整次扫描失败。</summary>
    public static IReadOnlyList<InstanceInfo> Scan(string versionsPath)
    {
        if (!Directory.Exists(versionsPath)) return [];

        var result = new List<InstanceInfo>();
        foreach (var directory in SafeDirectories(versionsPath))
        {
            var info = Read(directory);
            if (info is not null) result.Add(info);
        }

        return result
            .OrderByDescending(i => i.LastModified)
            .ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static InstanceInfo? Read(string versionDirectory)
    {
        var name = Path.GetFileName(versionDirectory);
        if (name.Length == 0) return null;

        // 约定是 {目录名}.json；某些启动器会写成别的名字，退回取目录里唯一的 json
        var expected = Path.Combine(versionDirectory, $"{name}.json");
        var jsonPath = File.Exists(expected) ? expected : FindSingleJson(versionDirectory);
        if (jsonPath is null) return null;

        try
        {
            var meta = VersionMeta.Load(jsonPath);
            var jarPath = Path.Combine(versionDirectory, $"{name}.jar");
            var jarInfo = File.Exists(jarPath) ? new FileInfo(jarPath) : null;

            return new InstanceInfo(
                meta.Id.Length > 0 ? meta.Id : name,
                meta.Type ?? "unknown",
                versionDirectory,
                jsonPath,
                jarInfo is not null,
                jarInfo?.Length ?? 0,
                meta.JavaVersion?.MajorVersion,
                new DateTimeOffset(Directory.GetLastWriteTimeUtc(versionDirectory), TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidDataException
                                       or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindSingleJson(string directory)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).ToList();
            return files.Count == 1 ? files[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
