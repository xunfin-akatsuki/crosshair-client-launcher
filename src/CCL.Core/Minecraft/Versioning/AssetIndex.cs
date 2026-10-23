using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCL.Core.Minecraft.Versioning;

public sealed class AssetEntry
{
    [JsonPropertyName("hash")] public string Hash { get; init; } = string.Empty;

    [JsonPropertyName("size")] public long Size { get; init; }
}

/// <summary>
/// assets/indexes/*.json。1.7 之前的老版本用 virtual / map_to_resources，
/// 需要把对象额外拷进 assets/virtual/legacy 或 resources 目录。
/// </summary>
public sealed class AssetIndex
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [JsonPropertyName("objects")] public Dictionary<string, AssetEntry> Objects { get; init; } = [];

    [JsonPropertyName("virtual")] public bool Virtual { get; init; }

    [JsonPropertyName("map_to_resources")] public bool MapToResources { get; init; }

    public static AssetIndex Parse(string json) =>
        JsonSerializer.Deserialize<AssetIndex>(json, SerializerOptions)
        ?? throw new InvalidDataException("资源索引内容为空");

    public static AssetIndex Load(string path) => Parse(File.ReadAllText(path));

    public static Uri ObjectUrl(string hash, string baseUrl = "https://resources.download.minecraft.net") =>
        new($"{baseUrl}/{hash[..2]}/{hash}");

    public static string ObjectRelativePath(string hash) => $"{hash[..2]}/{hash}";
}
