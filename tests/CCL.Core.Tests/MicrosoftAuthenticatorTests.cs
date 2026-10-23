using System.Text.Json.Nodes;
using CCL.Core.Accounts;
using CCL.Core.Net;

namespace CCL.Core.Tests;

/// <summary>按顺序返回预置响应的假接口客户端，用于覆盖登录链路的每个分支。</summary>
internal sealed class ScriptedApiClient : IApiClient
{
    private readonly Queue<Func<ApiResponse>> _responses = new();

    public List<string> Calls { get; } = [];

    public ScriptedApiClient Enqueue(Func<ApiResponse> factory)
    {
        _responses.Enqueue(factory);
        return this;
    }

    public ScriptedApiClient EnqueueJson(int status, string json) =>
        Enqueue(() => Build(status, json));

    public Task<ApiResponse> PostFormAsync(Uri uri, IReadOnlyDictionary<string, string> form, CancellationToken token)
    {
        Calls.Add($"form {uri.Host}{uri.AbsolutePath}");
        return Task.FromResult(Next());
    }

    public Task<ApiResponse> PostJsonAsync(Uri uri, JsonNode body, CancellationToken token)
    {
        Calls.Add($"json {uri.Host}{uri.AbsolutePath}");
        return Task.FromResult(Next());
    }

    public Task<ApiResponse> GetAsync(Uri uri, string? bearerToken, CancellationToken token)
    {
        Calls.Add($"get {uri.Host}{uri.AbsolutePath}");
        return Task.FromResult(Next());
    }

    private ApiResponse Next() =>
        _responses.Count > 0 ? _responses.Dequeue()() : Build(500, """{"error":"no scripted response"}""");

    public static ApiResponse Build(int status, string json) =>
        new(status, JsonNode.Parse(json) as JsonObject, json);
}

public class MicrosoftAuthenticatorTests
{
    private const string ClientId = "test-client";

    private const string DeviceCodeJson = """
    {
      "device_code": "dev-123",
      "user_code": "ABCD-EFGH",
      "verification_uri": "https://microsoft.com/devicelogin",
      "expires_in": 900,
      "interval": 1,
      "message": "go to the url"
    }
    """;

    private const string TokenJson = """
    {
      "access_token": "ms-access",
      "refresh_token": "ms-refresh",
      "expires_in": 3600,
      "token_type": "Bearer"
    }
    """;

    private const string XboxJson = """
    { "Token": "xbl-token", "DisplayClaims": { "xui": [ { "uhs": "user-hash" } ] } }
    """;

    private const string XstsJson = """
    { "Token": "xsts-token", "DisplayClaims": { "xui": [ { "uhs": "user-hash" } ] } }
    """;

    private const string MinecraftTokenJson = """
    { "access_token": "mc-access", "expires_in": 86400, "username": "Steve" }
    """;

    private const string EntitlementsJson = """
    { "items": [ { "name": "product_minecraft" } ] }
    """;

    private const string ProfileJson = """
    { "id": "069a79f444e94726a5befca90e38aaf5", "name": "Steve" }
    """;

    [Fact]
    public async Task EmptyClientIdIsRejectedBeforeAnyRequest()
    {
        var api = new ScriptedApiClient();
        var authenticator = new MicrosoftAuthenticator(api, "");

        Assert.False(authenticator.IsConfigured);
        var ex = await Assert.ThrowsAsync<MicrosoftLoginException>(
            () => authenticator.StartDeviceCodeAsync(CancellationToken.None));
        Assert.Equal(LoginFailure.ClientMisconfigured, ex.Failure);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task StartDeviceCodeParsesChallenge()
    {
        var api = new ScriptedApiClient().EnqueueJson(200, DeviceCodeJson);
        var authenticator = new MicrosoftAuthenticator(api, ClientId);

        var challenge = await authenticator.StartDeviceCodeAsync(CancellationToken.None);

        Assert.Equal("ABCD-EFGH", challenge.UserCode);
        Assert.Equal("dev-123", challenge.DeviceCode);
        Assert.Equal(TimeSpan.FromSeconds(1), challenge.PollInterval);
        Assert.True(challenge.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task PendingThenSuccessCompletesLogin()
    {
        var api = new ScriptedApiClient()
            .EnqueueJson(200, DeviceCodeJson)
            .EnqueueJson(400, """{"error":"authorization_pending"}""")
            .EnqueueJson(400, """{"error":"authorization_pending"}""")
            .EnqueueJson(200, TokenJson);
        var authenticator = new MicrosoftAuthenticator(api, ClientId);

        var challenge = await authenticator.StartDeviceCodeAsync(CancellationToken.None);
        var tokens = await authenticator.WaitForTokensAsync(challenge, null, CancellationToken.None);

        Assert.Equal("ms-access", tokens.AccessToken);
        Assert.Equal("ms-refresh", tokens.RefreshToken);
        Assert.Equal(4, api.Calls.Count);
    }

    [Theory]
    [InlineData("expired_token", LoginFailure.Expired)]
    [InlineData("authorization_declined", LoginFailure.Declined)]
    public async Task TerminalErrorsAreClassified(string error, LoginFailure expected)
    {
        var api = new ScriptedApiClient()
            .EnqueueJson(200, DeviceCodeJson)
            .EnqueueJson(400, $$"""{"error":"{{error}}"}""");
        var authenticator = new MicrosoftAuthenticator(api, ClientId);

        var challenge = await authenticator.StartDeviceCodeAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<MicrosoftLoginException>(
            () => authenticator.WaitForTokensAsync(challenge, null, CancellationToken.None));

        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public async Task SingleTenantAppProducesActionableHint()
    {
        var api = new ScriptedApiClient().EnqueueJson(400,
            """{"error":"unauthorized_client","error_description":"AADSTS700016: Application with identifier 'x' was not found in the directory"}""");
        var authenticator = new MicrosoftAuthenticator(api, ClientId);

        var ex = await Assert.ThrowsAsync<MicrosoftLoginException>(
            () => authenticator.StartDeviceCodeAsync(CancellationToken.None));

        Assert.Equal(LoginFailure.ClientMisconfigured, ex.Failure);
        Assert.Contains("signInAudience", ex.Message);
    }

    [Fact]
    public async Task PublicClientFlowErrorHasOwnHint()
    {
        var api = new ScriptedApiClient().EnqueueJson(400,
            """{"error":"invalid_client","error_description":"AADSTS7000218: The request body must contain client_assertion"}""");
        var authenticator = new MicrosoftAuthenticator(api, ClientId);

        var ex = await Assert.ThrowsAsync<MicrosoftLoginException>(
            () => authenticator.StartDeviceCodeAsync(CancellationToken.None));

        Assert.Contains("公共客户端流", ex.Message);
    }

    [Fact]
    public async Task FullChainReturnsProfile()
    {
        var api = new ScriptedApiClient()
            .EnqueueJson(200, XboxJson)
            .EnqueueJson(200, XstsJson)
            .EnqueueJson(200, MinecraftTokenJson)
            .EnqueueJson(200, EntitlementsJson)
            .EnqueueJson(200, ProfileJson);
        var authenticator = new MicrosoftAuthenticator(api, ClientId);

        var profile = await authenticator.LoginToMinecraftAsync(
            new MicrosoftTokens("ms-access", "ms-refresh", DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);

        Assert.Equal("Steve", profile.Name);
        Assert.Equal("069a79f444e94726a5befca90e38aaf5", profile.Uuid);
        Assert.Equal("mc-access", profile.AccessToken);
        Assert.Equal(5, api.Calls.Count);
        Assert.Contains(api.Calls, c => c.Contains("user.auth.xboxlive.com"));
        Assert.Contains(api.Calls, c => c.Contains("xsts.auth.xboxlive.com"));
    }

    [Fact]
    public async Task MissingXboxProfileIsClassified()
    {
        var api = new ScriptedApiClient()
            .EnqueueJson(200, XboxJson)
            .EnqueueJson(401, """{"XErr":2148916233,"Message":"no xbox account"}""");
        var authenticator = new MicrosoftAuthenticator(api, ClientId);

        var ex = await Assert.ThrowsAsync<MicrosoftLoginException>(
            () => authenticator.LoginToMinecraftAsync(
                new MicrosoftTokens("t", "r", DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None));

        Assert.Equal(LoginFailure.NoXboxAccount, ex.Failure);
    }

    [Fact]
    public async Task ChildAccountIsClassified()
    {
        var api = new ScriptedApiClient()
            .EnqueueJson(200, XboxJson)
            .EnqueueJson(401, """{"XErr":2148916238,"Message":"child"}""");
        var authenticator = new MicrosoftAuthenticator(api, ClientId);

        var ex = await Assert.ThrowsAsync<MicrosoftLoginException>(
            () => authenticator.LoginToMinecraftAsync(
                new MicrosoftTokens("t", "r", DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None));

        Assert.Equal(LoginFailure.ChildAccount, ex.Failure);
    }

    [Fact]
    public async Task EmptyEntitlementsMeansNoOwnership()
    {
        var api = new ScriptedApiClient()
            .EnqueueJson(200, XboxJson)
            .EnqueueJson(200, XstsJson)
            .EnqueueJson(200, MinecraftTokenJson)
            .EnqueueJson(200, """{"items":[]}""");
        var authenticator = new MicrosoftAuthenticator(api, ClientId);

        var ex = await Assert.ThrowsAsync<MicrosoftLoginException>(
            () => authenticator.LoginToMinecraftAsync(
                new MicrosoftTokens("t", "r", DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None));

        Assert.Equal(LoginFailure.NoGameOwnership, ex.Failure);
    }

    [Fact]
    public async Task RefreshKeepsOldRefreshTokenWhenOmitted()
    {
        var api = new ScriptedApiClient().EnqueueJson(200,
            """{"access_token":"new-access","expires_in":3600}""");
        var authenticator = new MicrosoftAuthenticator(api, ClientId);

        var tokens = await authenticator.RefreshAsync("old-refresh", CancellationToken.None);

        Assert.Equal("new-access", tokens.AccessToken);
        Assert.Equal("old-refresh", tokens.RefreshToken);
    }
}
