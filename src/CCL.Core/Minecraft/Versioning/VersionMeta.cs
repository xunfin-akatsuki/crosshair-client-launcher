using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CCL.Core.Minecraft.Versioning;

public sealed class Artifact
{
    [JsonPropertyName("path")] public string? Path { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }

    [JsonPropertyName("sha1")] public string? Sha1 { get; init; }

    [JsonPropertyName("size")] public long Size { get; init; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")] public Artifact? Artifact { get; init; }

    [JsonPropertyName("classifiers")] public Dictionary<string, Artifact>? Classifiers { get; init; }
}

public sealed class OsRule
{
    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("arch")] public string? Arch { get; init; }

    [JsonPropertyName("version")] public string? Version { get; init; }
}

public sealed class Rule
{
    [JsonPropertyName("action")] public string Action { get; init; } = "allow";

    [JsonPropertyName("os")] public OsRule? Os { get; init; }

    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; init; }
}

public sealed class ExtractInfo
{
    [JsonPropertyName("exclude")] public List<string>? Exclude { get; init; }
}

public sealed class Library
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; init; }

    [JsonPropertyName("rules")] public List<Rule>? Rules { get; init; }

    /// <summary>1.18 及更早：操作系统名到 classifiers 键的映射。</summary>
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; init; }

    [JsonPropertyName("extract")] public ExtractInfo? Extract { get; init; }
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("url")] public string Url { get; init; } = string.Empty;

    [JsonPropertyName("sha1")] public string? Sha1 { get; init; }

    [JsonPropertyName("size")] public long Size { get; init; }

    [JsonPropertyName("totalSize")] public long TotalSize { get; init; }
}

public sealed class DownloadsInfo
{
    [JsonPropertyName("client")] public Artifact? Client { get; init; }

    [JsonPropertyName("server")] public Artifact? Server { get; init; }
}

public sealed class JavaVersionInfo
{
    [JsonPropertyName("component")] public string? Component { get; init; }

    [JsonPropertyName("majorVersion")] public int MajorVersion { get; init; }
}

public sealed class LoggingFile
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("url")] public string Url { get; init; } = string.Empty;

    [JsonPropertyName("sha1")] public string? Sha1 { get; init; }

    [JsonPropertyName("size")] public long Size { get; init; }
}

public sealed class LoggingClient
{
    [JsonPropertyName("argument")] public string? Argument { get; init; }

    [JsonPropertyName("file")] public LoggingFile? File { get; init; }

    [JsonPropertyName("type")] public string? Type { get; init; }
}

public sealed class LoggingInfo
{
    [JsonPropertyName("client")] public LoggingClient? Client { get; init; }
}

/// <summary>
/// 单个版本的 metadata。arguments 保持原始节点，交给 M4 启动参数组装阶段解释；
/// M3 只关心文件清单。
/// </summary>
public sealed class VersionMeta
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("type")] public string? Type { get; init; }

    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; init; }

    [JsonPropertyName("mainClass")] public string? MainClass { get; init; }

    [JsonPropertyName("assets")] public string? Assets { get; init; }

    [JsonPropertyName("assetIndex")] public AssetIndexRef? AssetIndex { get; init; }

    [JsonPropertyName("downloads")] public DownloadsInfo? Downloads { get; init; }

    [JsonPropertyName("libraries")] public List<Library> Libraries { get; init; } = [];

    [JsonPropertyName("javaVersion")] public JavaVersionInfo? JavaVersion { get; init; }

    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; init; }

    [JsonPropertyName("arguments")] public JsonObject? Arguments { get; init; }

    [JsonPropertyName("logging")] public LoggingInfo? Logging { get; init; }

    public static VersionMeta Parse(string json) =>
        JsonSerializer.Deserialize<VersionMeta>(json, SerializerOptions)
        ?? throw new InvalidDataException("版本 JSON 内容为空");

    public static VersionMeta Load(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });
}
