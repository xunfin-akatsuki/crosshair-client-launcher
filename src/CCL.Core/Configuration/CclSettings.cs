using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCL.Core.Configuration;

/// <summary>
/// 启动器持久化设置。缺失字段由默认值补齐，读取失败的配置会被备份后重建，
/// 避免一次坏写入导致启动器再也打不开。
/// </summary>
public sealed class CclSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string? MinecraftPath { get; set; }

    public string? JavaPath { get; set; }

    public int MaxConcurrentDownloads { get; set; } = 8;

    public int MaxMemoryMb { get; set; } = 4096;

    public bool UseMirror { get; set; } = true;

    public string? LastSelectedVersion { get; set; }

    public string? OfflinePlayerName { get; set; } = "Player";

    public bool ShowSnapshots { get; set; }

    /// <summary>界面语言偏好：system / zh-CN / en-US。</summary>
    public string? Language { get; set; } = "system";

    /// <summary>微软 OAuth 客户端 ID。留空时正版登录不可用，由用户在登录窗口填写。</summary>
    public string? MicrosoftClientId { get; set; }

    public int Normalize()
    {
        if (MaxConcurrentDownloads is < 1 or > 64) MaxConcurrentDownloads = 8;
        if (MaxMemoryMb < 512) MaxMemoryMb = 4096;
        return MaxConcurrentDownloads;
    }

    public static CclSettings Load(string path)
    {
        if (!File.Exists(path)) return new CclSettings();

        try
        {
            var text = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<CclSettings>(text, SerializerOptions);
            if (loaded is null) throw new JsonException("配置内容为空");
            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            TryBackupBroken(path);
            return new CclSettings();
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, SerializerOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static void TryBackupBroken(string path)
    {
        try
        {
            File.Move(path, path + ".broken", overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
