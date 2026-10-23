using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CCL.Core.Minecraft.Versioning;

/// <summary>版本清单里的单个条目。</summary>
public sealed class ManifestVersion
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;

    [JsonPropertyName("url")] public string Url { get; init; } = string.Empty;

    [JsonPropertyName("sha1")] public string? Sha1 { get; init; }

    [JsonPropertyName("releaseTime")] public DateTimeOffset ReleaseTime { get; init; }
}

public sealed class ManifestLatest
{
    [JsonPropertyName("release")] public string? Release { get; init; }

    [JsonPropertyName("snapshot")] public string? Snapshot { get; init; }
}

/// <summary>version_manifest.json 的完整内容。</summary>
public sealed class VersionManifest
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [JsonPropertyName("latest")] public ManifestLatest? Latest { get; init; }

    [JsonPropertyName("versions")] public List<ManifestVersion> Versions { get; init; } = [];

    public static VersionManifest Parse(string json) =>
        JsonSerializer.Deserialize<VersionManifest>(json, SerializerOptions)
        ?? throw new InvalidDataException("版本清单内容为空");

    public ManifestVersion? Find(string id) =>
        Versions.FirstOrDefault(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ManifestVersion> Releases() =>
        Versions.Where(v => v.Type.Equals("release", StringComparison.OrdinalIgnoreCase));

    /// <summary>清单本身不携带 json 节点的原始文本，这里只做必要的完整性提示。</summary>
    public JsonObject ToJsonObject() => new()
    {
        ["latest"] = new JsonObject
        {
            ["release"] = Latest?.Release,
            ["snapshot"] = Latest?.Snapshot
        },
        ["versions"] = new JsonArray(Versions.Select(v => (JsonNode)new JsonObject
        {
            ["id"] = v.Id,
            ["type"] = v.Type,
            ["url"] = v.Url,
            ["sha1"] = v.Sha1,
            ["releaseTime"] = v.ReleaseTime.ToString("O")
        }).ToArray())
    };
}
