using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CCL.Core.Localization;

namespace CCL.Core.Accounts;

public enum AccountType
{
    Offline,
    Microsoft
}

/// <summary>落盘的账号。令牌字段写盘前会被 <see cref="ISecretProtector"/> 加密。</summary>
public sealed class StoredAccount
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public AccountType Type { get; init; }

    public string PlayerName { get; set; } = string.Empty;

    public string Uuid { get; set; } = string.Empty;

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    [JsonIgnore] public bool IsMicrosoft => Type == AccountType.Microsoft;

    [JsonIgnore] public string TypeLabel => CoreText.Get(IsMicrosoft ? "Account.TypeMicrosoft" : "Account.TypeOffline");
}

/// <summary>令牌加解密。Windows 上用 DPAPI，其它平台由调用方提供替代实现。</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    string Unprotect(string cipherText);
}

/// <summary>基于当前用户上下文的 DPAPI 保护，免密钥且不会随程序目录被拷走。</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CCL.Accounts.v1");

    public string Protect(string plainText)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI 仅在 Windows 可用");
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public string Unprotect(string cipherText)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI 仅在 Windows 可用");
        var data = ProtectedData.Unprotect(Convert.FromBase64String(cipherText), Entropy,
            DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }
}

/// <summary>明文保护器，仅用于测试或明确不加密的场景。</summary>
public sealed class PlainTextProtector : ISecretProtector
{
    public string Protect(string plainText) => plainText;

    public string Unprotect(string cipherText) => cipherText;
}

/// <summary>
/// 账号列表的持久化。令牌字段单独加密，账号名与 UUID 保持可读以便界面显示。
/// 文件损坏时备份后重建，不让一个坏文件挡住启动。
/// </summary>
public sealed class AccountStore(string path, ISecretProtector protector)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<StoredAccount> Load()
    {
        if (!File.Exists(path)) return [];

        try
        {
            var dto = JsonSerializer.Deserialize<AccountFile>(File.ReadAllText(path), Options);
            if (dto?.Accounts is null) return [];

            foreach (var account in dto.Accounts)
            {
                account.AccessToken = TryUnprotect(account.AccessToken);
                account.RefreshToken = TryUnprotect(account.RefreshToken);
            }

            return dto.Accounts;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                       or FormatException or CryptographicException)
        {
            TryBackup();
            return [];
        }
    }

    public void Save(IEnumerable<StoredAccount> accounts)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var snapshot = accounts.Select(a => new StoredAccount
        {
            Id = a.Id,
            Type = a.Type,
            PlayerName = a.PlayerName,
            Uuid = a.Uuid,
            AccessToken = TryProtect(a.AccessToken),
            RefreshToken = TryProtect(a.RefreshToken),
            AccessTokenExpiresAt = a.AccessTokenExpiresAt
        }).ToList();

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new AccountFile { Accounts = snapshot }, Options));
        File.Move(temp, path, overwrite: true);
    }

    private string? TryProtect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return protector.Protect(value);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private string? TryUnprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return protector.Unprotect(value);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException
                                       or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private void TryBackup()
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

    private sealed class AccountFile
    {
        [JsonPropertyName("accounts")] public List<StoredAccount>? Accounts { get; init; }
    }
}
