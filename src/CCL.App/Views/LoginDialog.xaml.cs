using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using CCL.App.Interop;
using CCL.App.Localization;
using CCL.Core.Accounts;
using CCL.Core.Configuration;

namespace CCL.App.Views;

public partial class LoginDialog
{
    private readonly AccountManager _accounts;
    private readonly CclSettings _settings;
    private CancellationTokenSource? _loginCts;

    public LoginDialog(AccountManager accounts, CclSettings settings)
    {
        InitializeComponent();

        _accounts = accounts;
        _settings = settings;

        CloseButton.Click += (_, _) => CancelAndClose();
        StartButton.Click += (_, _) => StartLogin();
        CopyCodeButton.Click += (_, _) => CopyCode();
        OpenBrowserButton.Click += (_, _) => OpenBrowser();
        LearnMoreButton.Click += (_, _) => OpenLink("https://aka.ms/AppRegInfo");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CancelAndClose();
        };

        var configured = accounts.IsMicrosoftConfigured;
        ClientIdPanel.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Content = Lang.Text(configured ? "Login.Start" : "Login.SaveAndStart");
        StatusText.Text = Lang.Text(configured ? "Login.ClickToStart" : "Login.FillClientIdFirst");

        Loaded += (_, _) =>
        {
            if (configured) StartLogin();
            else ClientIdBox.Focus();
        };
    }

    public StoredAccount? Result { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var kind = Backdrop.Enable(this);
        FallbackLayer.Visibility = kind == BackdropKind.None ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        _loginCts?.Cancel();
        _loginCts?.Dispose();
        base.OnClosed(e);
    }

    private async void StartLogin()
    {
        if (!_accounts.IsMicrosoftConfigured)
        {
            var input = ClientIdBox.Text.Trim();
            if (input.Length == 0)
            {
                StatusText.Text = Lang.Text("Login.NeedClientId");
                return;
            }

            _settings.MicrosoftClientId = input;
            _settings.Save(CCL.App.App.Layout.SettingsPath);
            ClientIdPanel.Visibility = Visibility.Collapsed;
            ResizeToContent();
            if (!_accounts.IsMicrosoftConfigured)
            {
                StatusText.Text = Lang.Text("Login.ClientIdNotApplied");
                return;
            }
        }

        _loginCts?.Cancel();
        _loginCts = new CancellationTokenSource();
        var token = _loginCts.Token;

        StartButton.IsEnabled = false;
        try
        {
            var progress = new Progress<LoginProgress>(p =>
            {
                StatusText.Text = p.Message;
                if (p.Challenge is { } challenge) ShowChallenge(challenge);
            });

            Result = await _accounts.LoginMicrosoftAsync(progress, token);
            StatusText.Text = Lang.Format("Login.Success", Result.PlayerName);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Lang.Text("Login.Cancelled");
        }
        catch (MicrosoftLoginException ex)
        {
            // 未通过审核是有明确出路的失败，直接把说明入口摆出来
            if (ex.Failure == LoginFailure.NotAllowlisted)
            {
                StatusText.Text = ex.Message;
                DetailPanel.Visibility = Visibility.Visible;
            }
            else
            {
                StatusText.Text = string.IsNullOrWhiteSpace(ex.Detail)
                    ? ex.Message
                    : ex.Message + Environment.NewLine + Lang.Format("Login.ServerSays", Shorten(ex.Detail));
            }

            ResizeToContent();
            CCL.App.App.Log.Warn("Account", $"正版登录失败：{ex.Failure} {ex.Message} | 详情：{ex.Detail}");
        }
        catch (Exception ex)
        {
            StatusText.Text = Lang.Format("Login.Failed", ex.Message);
            CCL.App.App.Log.Error("Account", "正版登录异常", ex);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private static string Shorten(string detail)
    {
        var text = detail.Replace(Environment.NewLine, " ").Trim();
        return text.Length <= 220 ? text : text[..220] + "…";
    }

    private void ShowChallenge(DeviceCodeChallenge challenge)
    {
        CodePanel.Visibility = Visibility.Visible;
        UserCodeText.Text = challenge.UserCode;
        _verificationUri = challenge.VerificationUri;
        ResizeToContent();
    }

    /// <summary>
    /// SizeToContent 在内容收缩时不会自动重算，窗口会留下空白。
    /// 重新置一次 SizeToContent 强制重新测量。
    /// </summary>
    private void ResizeToContent()
    {
        SizeToContent = SizeToContent.Manual;
        SizeToContent = SizeToContent.Height;
    }

    private string? _verificationUri;

    private void CopyCode()
    {
        if (UserCodeText.Text.Length == 0) return;
        try
        {
            Clipboard.SetText(UserCodeText.Text);
            StatusText.Text = Lang.Text("Login.CodeCopied");
        }
        catch (Exception ex)
        {
            StatusText.Text = Lang.Format("Login.CopyFailed", ex.Message);
        }
    }

    private void OpenBrowser() => OpenLink(_verificationUri ?? "https://microsoft.com/devicelogin");

    private void OpenLink(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = Lang.Format("Login.BrowserFailed", ex.Message);
        }
    }

    private void CancelAndClose()
    {
        _loginCts?.Cancel();
        DialogResult = false;
    }
}
