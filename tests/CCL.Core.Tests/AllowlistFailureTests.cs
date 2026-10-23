using System.Text.Json.Nodes;
using CCL.Core.Accounts;
using CCL.Core.Net;

namespace CCL.Core.Tests;

public class AllowlistFailureTests
{
    private const string XboxJson = """
    { "Token": "xbl-token", "DisplayClaims": { "xui": [ { "uhs": "user-hash" } ] } }
    """;

    // 用真实的 XSTS 令牌结构（header.payload.signature，payload 里带 appid/aud）
    private static string FakeXsts()
    {
        var header = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
        var payload = Base64Url("""{"appid":"test-client","aud":"rp://api.minecraftservices.com/","xid":"123"}""");
        return $"{header}.{payload}.signature";
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static ScriptedApiClient ChainWithLoginResponse(int status, string body)
    {
        var xsts = FakeXsts();
        return new ScriptedApiClient()
            .EnqueueJson(200, XboxJson)
            .EnqueueJson(200,
                "{\"Token\":\"" + xsts + "\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"user-hash\"}]}}")
            .EnqueueJson(status, body);
    }

    [Fact]
    public async Task InvalidAppRegistrationIsClassifiedAsNotAllowlisted()
    {
        var api = ChainWithLoginResponse(403,
            """{"path":"/authentication/login_with_xbox","errorMessage":"Invalid app registration, see https://aka.ms/AppRegInfo for more information"}""");
        var authenticator = new MicrosoftAuthenticator(api, "test-client");

        var ex = await Assert.ThrowsAsync<MicrosoftLoginException>(
            () => authenticator.LoginToMinecraftAsync(
                new MicrosoftTokens("t", "r", DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None));

        Assert.Equal(LoginFailure.NotAllowlisted, ex.Failure);
        Assert.Contains("允许列表", ex.Message);
        Assert.Contains("离线登录", ex.Message);
    }

    [Fact]
    public async Task OtherForbiddenStaysGeneric()
    {
        var api = ChainWithLoginResponse(403, """{"errorMessage":"something else"}""");
        var authenticator = new MicrosoftAuthenticator(api, "test-client");

        var ex = await Assert.ThrowsAsync<MicrosoftLoginException>(
            () => authenticator.LoginToMinecraftAsync(
                new MicrosoftTokens("t", "r", DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None));

        Assert.Equal(LoginFailure.Unknown, ex.Failure);
        Assert.Contains("HTTP 403", ex.Message);
    }

    [Fact]
    public async Task GenericFailureReportsXstsIdentity()
    {
        var api = ChainWithLoginResponse(500, """{"errorMessage":"boom"}""");
        var authenticator = new MicrosoftAuthenticator(api, "test-client");

        var ex = await Assert.ThrowsAsync<MicrosoftLoginException>(
            () => authenticator.LoginToMinecraftAsync(
                new MicrosoftTokens("t", "r", DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None));

        Assert.Contains("appid=test-client", ex.Message);
        Assert.Contains("一致", ex.Message);
    }
}
