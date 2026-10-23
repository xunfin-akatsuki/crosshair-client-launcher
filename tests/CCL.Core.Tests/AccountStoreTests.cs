using CCL.Core.Accounts;

namespace CCL.Core.Tests;

public class AccountStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccl-acc-" + Guid.NewGuid().ToString("N"));

    public AccountStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RoundTripKeepsAccounts()
    {
        var path = Path.Combine(_root, "accounts.json");
        var store = new AccountStore(path, new PlainTextProtector());

        store.Save([
            new StoredAccount
            {
                Type = AccountType.Microsoft,
                PlayerName = "Steve",
                Uuid = "069a79f4",
                AccessToken = "access",
                RefreshToken = "refresh",
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            },
            new StoredAccount { Type = AccountType.Offline, PlayerName = "Alex", Uuid = "abc", AccessToken = "0" }
        ]);

        var loaded = store.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("Steve", loaded[0].PlayerName);
        Assert.Equal("refresh", loaded[0].RefreshToken);
        Assert.Equal(AccountType.Offline, loaded[1].Type);
    }

    [Fact]
    public void DpapiProtectsTokensOnDisk()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_root, "enc.json");
        var store = new AccountStore(path, new DpapiSecretProtector());
        store.Save([
            new StoredAccount
            {
                Type = AccountType.Microsoft,
                PlayerName = "Steve",
                Uuid = "u",
                AccessToken = "super-secret-access",
                RefreshToken = "super-secret-refresh"
            }
        ]);

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("super-secret-access", raw);
        Assert.DoesNotContain("super-secret-refresh", raw);
        Assert.Contains("Steve", raw); // 显示用的字段保持可读

        var loaded = store.Load();
        Assert.Equal("super-secret-access", loaded[0].AccessToken);
        Assert.Equal("super-secret-refresh", loaded[0].RefreshToken);
    }

    [Fact]
    public void BrokenFileIsBackedUpAndIgnored()
    {
        var path = Path.Combine(_root, "broken.json");
        File.WriteAllText(path, "{ not json");
        var store = new AccountStore(path, new PlainTextProtector());

        Assert.Empty(store.Load());
        Assert.True(File.Exists(path + ".broken"));
    }

    [Fact]
    public void MissingFileYieldsEmptyList()
    {
        var store = new AccountStore(Path.Combine(_root, "none.json"), new PlainTextProtector());
        Assert.Empty(store.Load());
    }
}

public class AccountManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccl-accm-" + Guid.NewGuid().ToString("N"));

    public AccountManagerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeAuthenticator(bool configured = true) : IMicrosoftAuthenticator
    {
        public bool IsConfigured { get; } = configured;

        public int RefreshCalls { get; private set; }

        public Task<DeviceCodeChallenge> StartDeviceCodeAsync(CancellationToken token) =>
            IsConfigured
                ? Task.FromResult(new DeviceCodeChallenge("dev", "USER-CODE", "https://example",
                    DateTimeOffset.UtcNow.AddMinutes(10), TimeSpan.FromSeconds(1), null))
                : throw new MicrosoftLoginException(LoginFailure.ClientMisconfigured, "未配置客户端 ID");

        public Task<MicrosoftTokens> WaitForTokensAsync(DeviceCodeChallenge challenge, IProgress<string>? status,
            CancellationToken token) =>
            Task.FromResult(new MicrosoftTokens("ms-access", "ms-refresh", DateTimeOffset.UtcNow.AddHours(1)));

        public Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken token)
        {
            RefreshCalls++;
            return Task.FromResult(new MicrosoftTokens("ms-access-2", "ms-refresh-2",
                DateTimeOffset.UtcNow.AddHours(1)));
        }

        public Task<MinecraftProfile> LoginToMinecraftAsync(MicrosoftTokens tokens, CancellationToken token) =>
            Task.FromResult(new MinecraftProfile("069a79f444e94726a5befca90e38aaf5", "Steve",
                "mc-" + tokens.AccessToken, DateTimeOffset.UtcNow.AddHours(20)));
    }

    private AccountManager CreateManager(FakeAuthenticator? authenticator = null) =>
        new(new AccountStore(Path.Combine(_root, "accounts.json"), new PlainTextProtector()),
            authenticator ?? new FakeAuthenticator());

    [Fact]
    public void OfflineAccountsAreDeduplicatedByName()
    {
        var manager = CreateManager();

        var first = manager.AddOffline("Alex");
        var second = manager.AddOffline("Alex");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(manager.Accounts);
        Assert.NotEqual(Guid.Empty, first.Id);
    }

    [Fact]
    public void OfflineAccountGetsStableUuid()
    {
        var manager = CreateManager();
        var account = manager.AddOffline("Notch");

        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", account.Uuid);
        Assert.Equal(AccountType.Offline, account.Type);
    }

    [Fact]
    public async Task MicrosoftLoginAddsAccountAndSelectsIt()
    {
        var manager = CreateManager();

        await manager.LoginMicrosoftAsync(null, CancellationToken.None);

        var current = manager.Current;
        Assert.NotNull(current);
        Assert.Equal("Steve", current!.PlayerName);
        Assert.Equal(AccountType.Microsoft, current.Type);
        Assert.Equal("mc-ms-access", current.AccessToken);
        Assert.Equal("ms-refresh", current.RefreshToken);
    }

    [Fact]
    public async Task ResolveLaunchAccountRefreshesExpiredToken()
    {
        var authenticator = new FakeAuthenticator();
        var manager = CreateManager(authenticator);
        await manager.LoginMicrosoftAsync(null, CancellationToken.None);

        // 把令牌改成已过期，触发刷新
        var account = manager.Current!;
        account.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        var launch = await manager.ResolveLaunchAccountAsync(account.Id, CancellationToken.None);

        Assert.Equal(1, authenticator.RefreshCalls);
        Assert.Equal("msa", launch.UserType);
        Assert.Equal("mc-ms-access-2", launch.AccessToken);
        Assert.Equal(account.Uuid, launch.Uuid);
    }

    [Fact]
    public async Task ResolveLaunchAccountSkipsRefreshWhenTokenIsFresh()
    {
        var authenticator = new FakeAuthenticator();
        var manager = CreateManager(authenticator);
        await manager.LoginMicrosoftAsync(null, CancellationToken.None);

        var launch = await manager.ResolveLaunchAccountAsync(manager.Current!.Id, CancellationToken.None);

        Assert.Equal(0, authenticator.RefreshCalls);
        Assert.Equal("msa", launch.UserType);
    }

    [Fact]
    public async Task ResolveLaunchAccountFallsBackToOffline()
    {
        var manager = CreateManager();
        var account = manager.AddOffline("Alex");

        var launch = await manager.ResolveLaunchAccountAsync(account.Id, CancellationToken.None);

        Assert.Equal("Alex", launch.PlayerName);
        Assert.Equal("legacy", launch.UserType);
        Assert.Equal(CCL.Core.Launching.GameAccount.ComputeOfflineUuid("Alex"), launch.Uuid);
    }

    [Fact]
    public async Task RemovingCurrentAccountSelectsAnother()
    {
        var manager = CreateManager();
        var offline = manager.AddOffline("Alex");
        await manager.LoginMicrosoftAsync(null, CancellationToken.None);
        var microsoftId = manager.Current!.Id;

        manager.Remove(microsoftId);

        Assert.Equal(offline.Id, manager.Current!.Id);
        Assert.Single(manager.Accounts);
    }

    [Fact]
    public void AccountsPersistAcrossManagerInstances()
    {
        var path = Path.Combine(_root, "accounts.json");
        var store = new AccountStore(path, new PlainTextProtector());
        var first = new AccountManager(store, new FakeAuthenticator());
        first.AddOffline("Alex");

        var second = new AccountManager(new AccountStore(path, new PlainTextProtector()), new FakeAuthenticator());

        Assert.Single(second.Accounts);
        Assert.Equal("Alex", second.Current!.PlayerName);
    }

    [Fact]
    public async Task UnconfiguredClientSurfacesMisconfiguration()
    {
        var manager = new AccountManager(
            new AccountStore(Path.Combine(_root, "accounts.json"), new PlainTextProtector()),
            new FakeAuthenticator(configured: false));

        Assert.False(manager.IsMicrosoftConfigured);
        await Assert.ThrowsAsync<MicrosoftLoginException>(
            () => manager.LoginMicrosoftAsync(null, CancellationToken.None));
    }
}
