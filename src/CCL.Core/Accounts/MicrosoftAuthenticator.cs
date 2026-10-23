using System.Text.Json;
using System.Text.Json.Nodes;
using CCL.Core.Localization;
using CCL.Core.Net;

namespace CCL.Core.Accounts;

/// <summary>设备码挑战：把 user_code 和验证网址展示给用户，然后轮询令牌端点。</summary>
public sealed record DeviceCodeChallenge(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    DateTimeOffset ExpiresAt,
    TimeSpan PollInterval,
    string? Message);

public sealed record MicrosoftTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    public bool NeedsRefresh(TimeSpan margin) => DateTimeOffset.UtcNow + margin >= ExpiresAt;
}

public sealed record MinecraftProfile(string Uuid, string Name, string AccessToken, DateTimeOffset ExpiresAt);

public enum LoginFailure
{
    Declined,
    Expired,
    NoXboxAccount,
    ChildAccount,
    NoGameOwnership,
    /// <summary>应用未通过 Mojang 的三方接入审核，Minecraft 服务会返回 403 Invalid app registration。</summary>
    NotAllowlisted,
    ClientMisconfigured,
    Network,
    Unknown
}

public sealed class MicrosoftLoginException(LoginFailure failure, string message, string? detail = null)
    : Exception(message)
{
    public LoginFailure Failure { get; } = failure;

    public string? Detail { get; } = detail;
}

public interface IMicrosoftAuthenticator
{
    bool IsConfigured { get; }

    Task<DeviceCodeChallenge> StartDeviceCodeAsync(CancellationToken token);

    Task<MicrosoftTokens> WaitForTokensAsync(DeviceCodeChallenge challenge, IProgress<string>? status,
        CancellationToken token);

    Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken token);

    Task<MinecraftProfile> LoginToMinecraftAsync(MicrosoftTokens tokens, CancellationToken token);
}
/// <summary>
/// 微软账号登录：设备码 → Xbox Live → XSTS → Minecraft 服务。
/// 每个阶段都显式检查状态码与业务错误码，把原始报文塞进异常详情供日志使用。
/// </summary>
public sealed class MicrosoftAuthenticator(IApiClient api, Func<string> clientIdProvider) : IMicrosoftAuthenticator
{
    public MicrosoftAuthenticator(IApiClient api, string clientId) : this(api, () => clientId)
    {
    }

    private const string Scope = "XboxLive.signin offline_access";

    private static readonly Uri DeviceCodeEndpoint =
        new("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode");

    private static readonly Uri TokenEndpoint =
        new("https://login.microsoftonline.com/consumers/oauth2/v2.0/token");

    private static readonly Uri XboxUserAuthEndpoint = new("https://user.auth.xboxlive.com/user/authenticate");
    private static readonly Uri XstsEndpoint = new("https://xsts.auth.xboxlive.com/xsts/authorize");
    private static readonly Uri MinecraftLoginEndpoint =
        new("https://api.minecraftservices.com/authentication/login_with_xbox");
    private static readonly Uri MinecraftProfileEndpoint = new("https://api.minecraftservices.com/minecraft/profile");
    private static readonly Uri EntitlementsEndpoint =
        new("https://api.minecraftservices.com/entitlements/mcstore");

    public string ClientId => clientIdProvider() ?? string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public async Task<DeviceCodeChallenge> StartDeviceCodeAsync(CancellationToken token)
    {
        if (!IsConfigured)
            throw new MicrosoftLoginException(LoginFailure.ClientMisconfigured,
                CoreText.Get("Ms.NoClientId"));

        var response = await api.PostFormAsync(DeviceCodeEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope
        }, token).ConfigureAwait(false);

        if (!response.IsSuccess)
            throw TranslateOAuthFailure(response, CoreText.Get("Ms.DeviceCodeFailed"));

        var body = response.Body!;
        var expiresIn = body["expires_in"]?.GetValue<int>() ?? 900;
        var interval = body["interval"]?.GetValue<int>() ?? 5;

        return new DeviceCodeChallenge(
            body["device_code"]?.GetValue<string>() ?? throw new MicrosoftLoginException(
                LoginFailure.Unknown, CoreText.Get("Ms.DeviceCodeEmpty")),
            body["user_code"]?.GetValue<string>() ?? string.Empty,
            body["verification_uri"]?.GetValue<string>() ?? "https://microsoft.com/devicelogin",
            DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            TimeSpan.FromSeconds(Math.Max(1, interval)),
            body["message"]?.GetValue<string>());
    }

    /// <summary>按服务端给的间隔轮询，直到用户完成授权、超时或被拒绝。</summary>
    public async Task<MicrosoftTokens> WaitForTokensAsync(DeviceCodeChallenge challenge,
        IProgress<string>? status = null, CancellationToken token = default)
    {
        var interval = challenge.PollInterval;

        while (DateTimeOffset.UtcNow < challenge.ExpiresAt)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(interval, token).ConfigureAwait(false);
            status?.Report("等待浏览器中完成授权…");

            var response = await api.PostFormAsync(TokenEndpoint, new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = challenge.DeviceCode
            }, token).ConfigureAwait(false);

            if (response.IsSuccess) return ParseTokens(response.Body!);

            switch (response.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "expired_token":
                    throw new MicrosoftLoginException(LoginFailure.Expired, CoreText.Get("Ms.Expired"),
                        response.BodyText);
                case "authorization_declined":
                    throw new MicrosoftLoginException(LoginFailure.Declined, CoreText.Get("Ms.Declined"), response.BodyText);
                default:
                    throw TranslateOAuthFailure(response, CoreText.Get("Ms.LoginFailed"));
            }
        }

        throw new MicrosoftLoginException(LoginFailure.Expired, CoreText.Get("Ms.Timeout"));
    }

    public async Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken token)
    {
        if (!IsConfigured)
            throw new MicrosoftLoginException(LoginFailure.ClientMisconfigured,
                CoreText.Get("Ms.NoClientIdRefresh"));

        var response = await api.PostFormAsync(TokenEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope
        }, token).ConfigureAwait(false);

        if (!response.IsSuccess)
            throw TranslateOAuthFailure(response, CoreText.Get("Ms.RefreshFailed"));

        return ParseTokens(response.Body!, refreshToken);
    }

    /// <summary>把微软令牌换成 Xbox → XSTS → Minecraft 三段令牌，并确认账号真的拥有游戏。</summary>
    public async Task<MinecraftProfile> LoginToMinecraftAsync(MicrosoftTokens tokens, CancellationToken token)
    {
        var (userHash, xstsToken) = await AcquireXstsAsync(tokens.AccessToken, token).ConfigureAwait(false);

        var minecraftResponse = await api.PostJsonAsync(MinecraftLoginEndpoint, new JsonObject
        {
            ["identityToken"] = $"XBL3.0 x={userHash};{xstsToken}"
        }, token).ConfigureAwait(false);

        if (!minecraftResponse.IsSuccess)
        {
            // Mojang 对三方接入采取人工允许列表制：新应用在审核通过前一律返回 403
            var body = minecraftResponse.BodyText;
            if (minecraftResponse.StatusCode == 403 &&
                body.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
                throw new MicrosoftLoginException(LoginFailure.NotAllowlisted,
                    CoreText.Get("Ms.NotAllowlisted"), body);

            throw new MicrosoftLoginException(LoginFailure.Unknown,
                CoreText.Get("Ms.MinecraftLoginFailed") + ": " + DescribeFailure(minecraftResponse) +
                "（" + DescribeXstsIdentity(xstsToken) + "）",
                minecraftResponse.BodyText);
        }

        var accessToken = minecraftResponse.Body!["access_token"]?.GetValue<string>()
                          ?? throw new MicrosoftLoginException(LoginFailure.Unknown, CoreText.Get("Ms.NoAccessToken"));
        var expiresIn = minecraftResponse.Body!["expires_in"]?.GetValue<int>() ?? 86_400;
        var minecraftExpires = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        await EnsureOwnsGameAsync(accessToken, token).ConfigureAwait(false);

        var profileResponse = await api.GetAsync(MinecraftProfileEndpoint, accessToken, token).ConfigureAwait(false);
        if (!profileResponse.IsSuccess)
            throw new MicrosoftLoginException(LoginFailure.Unknown, CoreText.Get("Ms.NoOwnershipProfile"),
                profileResponse.BodyText);

        var uuid = profileResponse.Body!["id"]?.GetValue<string>() ?? string.Empty;
        var name = profileResponse.Body!["name"]?.GetValue<string>() ?? string.Empty;
        if (uuid.Length == 0 || name.Length == 0)
            throw new MicrosoftLoginException(LoginFailure.Unknown, CoreText.Get("Ms.BadProfile"));

        return new MinecraftProfile(uuid, name, accessToken, minecraftExpires);
    }

    private async Task<(string UserHash, string XstsToken)> AcquireXstsAsync(string msAccessToken,
        CancellationToken token)
    {
        var xbl = await api.PostJsonAsync(XboxUserAuthEndpoint, new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = $"d={msAccessToken}"
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT"
        }, token).ConfigureAwait(false);

        if (!xbl.IsSuccess)
            throw new MicrosoftLoginException(LoginFailure.Unknown, CoreText.Get("Ms.XboxAuthFailed"), xbl.BodyText);

        var xblToken = xbl.Body!["Token"]?.GetValue<string>()
                       ?? throw new MicrosoftLoginException(LoginFailure.Unknown, CoreText.Get("Ms.XboxNoToken"));

        var xsts = await api.PostJsonAsync(XstsEndpoint, new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JsonArray(xblToken)
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT"
        }, token).ConfigureAwait(false);

        if (!xsts.IsSuccess) throw TranslateXstsFailure(xsts);

        var xstsToken = xsts.Body!["Token"]?.GetValue<string>()
                        ?? throw new MicrosoftLoginException(LoginFailure.Unknown, CoreText.Get("Ms.XstsNoToken"));
        var userHash = xsts.Body!["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>()
                       ?? throw new MicrosoftLoginException(LoginFailure.Unknown, CoreText.Get("Ms.XstsNoUserHash"));

        return (userHash, xstsToken);
    }

    private async Task EnsureOwnsGameAsync(string accessToken, CancellationToken token)
    {
        var entitlements = await api.GetAsync(EntitlementsEndpoint, accessToken, token).ConfigureAwait(false);
        if (!entitlements.IsSuccess)
            throw new MicrosoftLoginException(LoginFailure.NoGameOwnership, CoreText.Get("Ms.NotOwned"),
                entitlements.BodyText);

        var items = entitlements.Body?["items"] as JsonArray;
        if (items is null || items.Count == 0)
            throw new MicrosoftLoginException(LoginFailure.NoGameOwnership, CoreText.Get("Ms.NotOwned"),
                entitlements.BodyText);
    }

    private static MicrosoftTokens ParseTokens(JsonObject body, string? fallbackRefresh = null)
    {
        var access = body["access_token"]?.GetValue<string>()
                     ?? throw new MicrosoftLoginException(LoginFailure.Unknown, CoreText.Get("Ms.NoAccessToken"));
        var refresh = body["refresh_token"]?.GetValue<string>() ?? fallbackRefresh;
        var expiresIn = body["expires_in"]?.GetValue<int>() ?? 3600;
        return new MicrosoftTokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private static MicrosoftLoginException TranslateOAuthFailure(ApiResponse response, string prefix)
    {
        var error = response.Error ?? string.Empty;
        var description = response.ErrorDescription ?? string.Empty;

        // 应用注册为单租户时个人账户会被拒，这里给出可操作的提示
        if (description.Contains("AADSTS700016", StringComparison.Ordinal) ||
            description.Contains("AADSTS70001", StringComparison.Ordinal))
            return new MicrosoftLoginException(LoginFailure.ClientMisconfigured,
                CoreText.Get("Ms.TenantMismatch"),
                response.BodyText);

        if (description.Contains("AADSTS7000218", StringComparison.Ordinal))
            return new MicrosoftLoginException(LoginFailure.ClientMisconfigured,
                CoreText.Get("Ms.NoPublicClient"),
                response.BodyText);

        if (error == "invalid_grant")
            return new MicrosoftLoginException(LoginFailure.Expired, CoreText.Get("Ms.InvalidGrant"), response.BodyText);

        return new MicrosoftLoginException(LoginFailure.Unknown, $"{prefix}：{error}", response.BodyText);
    }

    /// <summary>把接口返回的错误压缩成一行，方便直接展示与写日志。</summary>
    private static string DescribeFailure(ApiResponse response)
    {
        var message = response.Body?["errorMessage"]?.GetValue<string>()
                      ?? response.Body?["error"]?.GetValue<string>()
                      ?? response.ErrorDescription
                      ?? (string.IsNullOrWhiteSpace(response.BodyText)
                          ? "无响应内容"
                          : Truncate(response.BodyText, 160));
        return $"HTTP {response.StatusCode} · {message}";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// 解析 XSTS 令牌的声明（不校验签名，只用于诊断）。
    /// Minecraft 服务拒绝时，appid 是否等于本应用客户端 ID 是最关键的一条证据。
    /// </summary>
    private string DescribeXstsIdentity(string xstsToken)
    {
        try
        {
            var parts = xstsToken.Split('.');
            if (parts.Length < 2) return "XSTS 令牌格式异常";

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            var padding = (4 - payload.Length % 4) % 4;
            if (padding > 0) payload = payload.PadRight(payload.Length + padding, '=');

            var json = JsonNode.Parse(Convert.FromBase64String(payload));
            var appId = json?["appid"]?.GetValue<string>() ?? "无";
            var audience = json?["aud"]?.GetValue<string>() ?? "无";
            var matches = appId.Equals(ClientId, StringComparison.OrdinalIgnoreCase);

            return $"XSTS 声明 appid={appId}，aud={audience}，与本应用客户端 ID {(matches ? "一致" : "不一致")}";
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return "XSTS 声明无法解析";
        }
    }

    private static MicrosoftLoginException TranslateXstsFailure(ApiResponse response)
    {
        var xerr = response.Body?["XErr"]?.GetValue<long>();
        return xerr switch
        {
            2148916233 => new MicrosoftLoginException(LoginFailure.NoXboxAccount,
                CoreText.Get("Ms.NoXboxAccount"), response.BodyText),
            2148916238 => new MicrosoftLoginException(LoginFailure.ChildAccount,
                CoreText.Get("Ms.ChildAccount"), response.BodyText),
            _ => new MicrosoftLoginException(LoginFailure.Unknown, CoreText.Get("Ms.XstsFailed"), response.BodyText)
        };
    }
}
