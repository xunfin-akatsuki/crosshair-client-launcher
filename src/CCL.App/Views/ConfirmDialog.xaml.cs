using System.Windows;
using System.Windows.Input;
using CCL.App.Interop;

namespace CCL.App.Views;

public partial class ConfirmDialog
{
    public ConfirmDialog(string title, string message, string confirmText = "确定")
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        CancelButton.Click += (_, _) => DialogResult = false;
        ConfirmButton.Click += (_, _) => DialogResult = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) DialogResult = false;
            else if (e.Key == Key.Enter) DialogResult = true;
        };

        Loaded += (_, _) => ConfirmButton.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 与主窗口保持一致：能用系统材质就用，用不上则保留兜底渐变，避免出现纯黑边
        var kind = Backdrop.Enable(this);
        FallbackLayer.Visibility = kind == BackdropKind.None ? Visibility.Visible : Visibility.Collapsed;
    }
}
