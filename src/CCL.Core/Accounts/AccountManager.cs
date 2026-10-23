using CCL.Core.Localization;
using CCL.Core.Diagnostics;
using CCL.Core.Localization;
using CCL.Core.Launching;

namespace CCL.Core.Accounts;

/// <summary>
/// 账号的增删改与“取一个可用于启动的凭据”。正版账号在临近过期时自动刷新，
/// 刷新失败会抛出让界面提示重新登录，而不是带着过期令牌启动失败。
/// </summary>
public sealed class AccountManager(AccountStore store, IMicrosoftAuthenticator authenticator, CclLog? log = null)
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(10);

    private readonly List<StoredAccount> _accounts = [];
    private bool _loaded;

    public IReadOnlyList<StoredAccount> Accounts
    {
        get
        {
            EnsureLoaded();
            return _accounts;
        }
    }

    public Guid? CurrentId { get; private set; }

    public StoredAccount? Current
    {
        get
        {
            EnsureLoaded();
            return _accounts.FirstOrDefault(a => a.Id == CurrentId) ?? _accounts.FirstOrDefault();
        }
    }

    public bool IsMicrosoftConfigured => authenticator.IsConfigured;

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _accounts.AddRange(store.Load());
        CurrentId = _accounts.FirstOrDefault()?.Id;
        log?.Info("Account", $"已加载 {_accounts.Count} 个账号");
    }

    public void Select(Guid id)
    {
        EnsureLoaded();
        if (_accounts.All(a => a.Id != id)) return;
        CurrentId = id;
    }

    /// <summary>离线账号按名字去重：同名视为同一个账号。</summary>
    public StoredAccount AddOffline(string playerName)
    {
        EnsureLoaded();
        var existing = _accounts.FirstOrDefault(a => a.Type == AccountType.Offline &&
                                                     a.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            CurrentId = existing.Id;
            Persist();
            return existing;
        }

        var account = new StoredAccount
        {
            Type = AccountType.Offline,
            PlayerName = playerName,
            Uuid = GameAccount.ComputeOfflineUuid(playerName),
            AccessToken = "0"
        };
        _accounts.Add(account);
        CurrentId = account.Id;
        Persist();
        return account;
    }

    public async Task<StoredAccount> LoginMicrosoftAsync(IProgress<LoginProgress>? progress,
        CancellationToken token)
    {
        EnsureLoaded();

        var challenge = await authenticator.StartDeviceCodeAsync(token).ConfigureAwait(false);
        progress?.Report(LoginProgress.WaitingForUser(challenge));

        var textStatus = new Progress<string>(_ => { });
        var msTokens = await authenticator
            .WaitForTokensAsync(challenge, textStatus, token)
            .ConfigureAwait(false);

        progress?.Report(LoginProgress.Stage(CoreText.Get("Account.Exchanging")));
        var profile = await authenticator.LoginToMinecraftAsync(msTokens, token).ConfigureAwait(false);

        var existing = _accounts.FirstOrDefault(a =>
            a.Type == AccountType.Microsoft && a.Uuid.Equals(profile.Uuid, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.PlayerName = profile.Name;
            existing.AccessToken = profile.AccessToken;
            existing.RefreshToken = msTokens.RefreshToken ?? existing.RefreshToken;
            existing.AccessTokenExpiresAt = profile.ExpiresAt;
            CurrentId = existing.Id;
            Persist();
            log?.Info("Account", $"已更新正版账号 {profile.Name}");
            return existing;
        }

        var account = new StoredAccount
        {
            Type = AccountType.Microsoft,
            PlayerName = profile.Name,
            Uuid = profile.Uuid,
            AccessToken = profile.AccessToken,
            RefreshToken = msTokens.RefreshToken,
            AccessTokenExpiresAt = profile.ExpiresAt
        };
        _accounts.Add(account);
        CurrentId = account.Id;
        Persist();
        log?.Info("Account", $"已添加正版账号 {profile.Name}");
        return account;
    }

    public void Remove(Guid id)
    {
        EnsureLoaded();
        var removed = _accounts.RemoveAll(a => a.Id == id);
        if (removed == 0) return;

        if (CurrentId == id) CurrentId = _accounts.FirstOrDefault()?.Id;
        Persist();
    }

    /// <summary>
    /// 取一个可直接用于启动的账号。正版账号会检查过期时间并刷新令牌。
    /// </summary>
    public async Task<GameAccount> ResolveLaunchAccountAsync(Guid? id, CancellationToken token)
    {
        EnsureLoaded();
        var account = id is { } target
            ? _accounts.FirstOrDefault(a => a.Id == target)
            : Current;

        if (account is null)
            throw new InvalidOperationException(CoreText.Get("Account.NoAccount"));

        if (!account.IsMicrosoft) return GameAccount.Offline(account.PlayerName);

        if (account.RefreshToken is not { Length: > 0 })
            throw new MicrosoftLoginException(LoginFailure.Expired, CoreText.Get("Ms.NoTokenForLaunch"));

        var needsRefresh = account.AccessTokenExpiresAt is null ||
                           DateTimeOffset.UtcNow + RefreshMargin >= account.AccessTokenExpiresAt;
        if (!needsRefresh && account.AccessToken is { Length: > 0 } current)
            return new GameAccount(account.PlayerName, account.Uuid, current, "msa");

        log?.Info("Account", $"刷新 {account.PlayerName} 的令牌");
        var refreshed = await authenticator.RefreshAsync(account.RefreshToken, token).ConfigureAwait(false);
        var profile = await authenticator.LoginToMinecraftAsync(refreshed, token).ConfigureAwait(false);

        account.AccessToken = profile.AccessToken;
        account.AccessTokenExpiresAt = profile.ExpiresAt;
        account.RefreshToken = refreshed.RefreshToken ?? account.RefreshToken;
        account.PlayerName = profile.Name;
        account.Uuid = profile.Uuid;
        Persist();

        return new GameAccount(profile.Name, profile.Uuid, profile.AccessToken, "msa");
    }

    private void Persist()
    {
        try
        {
            store.Save(_accounts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Warn("Account", $"保存账号失败：{ex.Message}");
        }
    }
}

public sealed record LoginProgress(string Message, DeviceCodeChallenge? Challenge)
{
    public static LoginProgress Stage(string message) => new(message, null);

    public static LoginProgress WaitingForUser(DeviceCodeChallenge challenge) =>
        new(CoreText.Format("Account.WaitingUser", challenge.UserCode), challenge);
}
