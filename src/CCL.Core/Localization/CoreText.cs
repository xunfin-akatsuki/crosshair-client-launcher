namespace CCL.Core.Localization;

/// <summary>
/// Core 里少量会被界面直接展示的文案。日志仍用中文，这里只覆盖用户能看到的错误与状态。
/// 语言由界面层在启动时设置，缺省中文。
/// </summary>
public static class CoreText
{
    public static bool English { get; set; }

    private static readonly Dictionary<string, (string Zh, string En)> Table = new()
    {
        ["Ms.NoClientId"] = ("未配置 Microsoft OAuth 客户端 ID，无法发起正版登录",
            "Microsoft OAuth client ID is not configured, sign-in is unavailable"),
        ["Ms.NoClientIdRefresh"] = ("未配置 Microsoft OAuth 客户端 ID，无法刷新令牌",
            "Microsoft OAuth client ID is not configured, cannot refresh the token"),
        ["Ms.DeviceCodeFailed"] = ("获取设备码失败", "Failed to obtain a device code"),
        ["Ms.DeviceCodeEmpty"] = ("设备码响应缺少 device_code", "Device code response did not contain device_code"),
        ["Ms.Expired"] = ("设备码已过期，请重新登录", "The device code expired, please sign in again"),
        ["Ms.Declined"] = ("已取消登录", "Sign-in was cancelled"),
        ["Ms.Timeout"] = ("等待授权超时，请重新登录", "Timed out waiting for authorization, please sign in again"),
        ["Ms.LoginFailed"] = ("登录失败", "Sign-in failed"),
        ["Ms.RefreshFailed"] = ("刷新令牌失败", "Failed to refresh the token"),
        ["Ms.InvalidGrant"] = ("登录凭据已失效，请重新登录", "Sign-in credentials expired, please sign in again"),
        ["Ms.TenantMismatch"] = ("Azure 应用不接受个人微软账户，请把应用清单里的 signInAudience 改为 AzureADandPersonalMicrosoftAccount",
            "The Azure app does not accept personal Microsoft accounts. Set signInAudience to AzureADandPersonalMicrosoftAccount in the app manifest"),
        ["Ms.NoPublicClient"] = ("Azure 应用未启用公共客户端流，请在“身份验证”里把“允许公共客户端流”设为是",
            "The Azure app does not allow public client flows. Enable \u201cAllow public client flows\u201d under Authentication"),
        ["Ms.XboxAuthFailed"] = ("Xbox Live 认证失败", "Xbox Live authentication failed"),
        ["Ms.XboxNoToken"] = ("Xbox Live 未返回令牌", "Xbox Live did not return a token"),
        ["Ms.XstsFailed"] = ("XSTS 授权失败", "XSTS authorization failed"),
        ["Ms.XstsNoToken"] = ("XSTS 未返回令牌", "XSTS did not return a token"),
        ["Ms.XstsNoUserHash"] = ("XSTS 未返回用户哈希", "XSTS did not return a user hash"),
        ["Ms.NoXboxAccount"] = ("该微软账号没有 Xbox 档案，请先在 Xbox 官网创建",
            "This Microsoft account has no Xbox profile. Create one on the Xbox website first"),
        ["Ms.ChildAccount"] = ("该账号是未成年账号，需要家长在 Microsoft 家庭中批准",
            "This is a child account and needs approval from a parent in the Microsoft family group"),
        ["Ms.MinecraftLoginFailed"] = ("Minecraft 服务登录失败", "Minecraft service sign-in failed"),
        ["Ms.NoAccessToken"] = ("Minecraft 未返回访问令牌", "Minecraft did not return an access token"),
        ["Ms.NoOwnershipProfile"] = ("读取 Minecraft 档案失败", "Failed to read the Minecraft profile"),
        ["Ms.BadProfile"] = ("Minecraft 档案缺少 id 或 name", "The Minecraft profile is missing id or name"),
        ["Ms.NotOwned"] = ("该账号未拥有 Minecraft Java 版", "This account does not own Minecraft: Java Edition"),
        ["Ms.NotAllowlisted"] = ("该应用尚未通过 Mojang 的三方接入审核，正版登录暂不可用。Mojang 现已对访问 Java 版游戏服务 API 的第三方应用实行人工允许列表，新应用需先提交申请；在此之前请使用离线登录。",
            "This application has not been approved by Mojang's third-party API review, so Microsoft sign-in is unavailable. Mojang now maintains a manual allow list for apps using the Java Edition game service APIs; new applications must apply first. Please use an offline account meanwhile."),
        ["Ms.NoTokenForLaunch"] = ("正版账号缺少刷新令牌，请重新登录",
            "The Microsoft account has no refresh token, please sign in again"),

        ["Account.NoAccount"] = ("没有可用的账号，请先添加一个账号", "No account available, add one first"),
        ["Account.DeleteOk"] = ("已删除 {0}", "Deleted {0}"),
        ["Account.DeleteFailed"] = ("{0} 删除失败：{1}", "Failed to delete {0}: {1}"),

        ["Launch.NoJava"] = ("未找到 Java {0} 或更高版本，请先安装后再启动",
            "Java {0} or newer was not found. Install it before launching"),
        ["Launch.NoMainClass"] = ("版本 {0} 缺少 mainClass，无法启动",
            "Version {0} has no mainClass and cannot be launched"),
        ["Instance.Incomplete"] = ("安装未完成", "incomplete install"),
        ["Instance.JavaUnknown"] = ("Java 未知", "Java unknown"),
        ["Instance.MissingJar"] = ("缺少客户端 jar", "client jar missing"),
        ["Account.TypeMicrosoft"] = ("正版", "Microsoft"),
        ["Account.TypeOffline"] = ("离线", "Offline"),
        ["Account.WaitingUser"] = ("请在浏览器中输入代码 {0}", "Enter code {0} in the browser"),
        ["Account.Exchanging"] = ("已授权，正在换取 Minecraft 令牌…", "Authorized, exchanging for a Minecraft token…"),
        ["Task.Install"] = ("安装 {0}", "Install {0}"),
    };
    public static string Get(string key)
    {
        if (!Table.TryGetValue(key, out var entry)) return key;
        return English ? entry.En : entry.Zh;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(Get(key), args);
}
