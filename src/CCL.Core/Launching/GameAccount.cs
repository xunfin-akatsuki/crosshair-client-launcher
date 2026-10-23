using System.Security.Cryptography;
using System.Text;

namespace CCL.Core.Launching;

/// <summary>
/// 启动用的账号信息。离线账号的 UUID 由名字按官方离线算法派生（MD5 的 v3 UUID），
/// 同名永远得到同一个 UUID，这样存档与皮肤缓存不会每次启动都变。
/// </summary>
public sealed record GameAccount(string PlayerName, string Uuid, string AccessToken, string UserType = "legacy")
{
    public static GameAccount Offline(string playerName, string accessToken = "0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        return new GameAccount(playerName, ComputeOfflineUuid(playerName), accessToken, "legacy");
    }

    /// <summary>UUID.nameUUIDFromBytes("OfflinePlayer:name") 的等价实现。</summary>
    public static string ComputeOfflineUuid(string playerName)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30); // version 3
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant RFC 4122
        var hex = Convert.ToHexStringLower(bytes);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>去掉连字符，Minecraft 新版本写档案时用这种形式。</summary>
    public string UuidCompact => Uuid.Replace("-", string.Empty, StringComparison.Ordinal);
}
