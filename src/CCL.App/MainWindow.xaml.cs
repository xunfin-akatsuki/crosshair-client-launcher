using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CCL.App.Interop;
using CCL.App.Services;
using CCL.App.ViewModels;

namespace CCL.App;

public partial class MainWindow
{
    // 最大化与还原用矢量路径表示，避免依赖 Segoe MDL2 之类的图标字体
    private static readonly Geometry MaximizeGeometry = Geometry.Parse("M1.5,2 H10.5 V11 H1.5 Z");
    private static readonly Geometry RestoreGeometry =
        Geometry.Parse("M3.5,1.5 H10.5 V8.5 M1.5,3.5 H8.5 V11 H1.5 Z");

    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(280));

    private readonly MainViewModel _viewModel;

    public MainWindow(LauncherService service)
    {
        InitializeComponent();

        _viewModel = new MainViewModel(service);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Close();

        StateChanged += (_, _) => SyncMaximizeGlyph();
        Loaded += (_, _) => PlayEntrance(InstanceView, InstanceOffset);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var kind = Backdrop.Enable(this);
        MaximizeFix.Attach(this);
        // 材质生效后兜底渐变必须让位，否则毛玻璃被盖住；材质不可用时保留渐变，避免出现空洞的透明窗口
        FallbackLayer.Visibility = kind == BackdropKind.None ? Visibility.Visible : Visibility.Collapsed;
        App.Log.Info("UI", kind == BackdropKind.None ? "系统背景材质不可用，使用渐变底" : $"已启用 {kind} 背景材质");
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.SaveSettings();
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ShowDownload)) return;

        // 两个视图叠放在同一格，切换时让目标视图淡入并轻微上移
        if (_viewModel.ShowDownload) PlayEntrance(DownloadView, DownloadOffset);
        else PlayEntrance(InstanceView, InstanceOffset);
    }

    /// <summary>目标视图从头播一次入场动画。动画直接作用于元素本身，不改动绑定值。</summary>
    private static void PlayEntrance(UIElement target, TranslateTransform offset)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        target.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0d, 1d, FadeDuration) { EasingFunction = ease });
        offset.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12d, 0d, SlideDuration) { EasingFunction = ease });
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void SyncMaximizeGlyph() =>
        MaximizeGlyph.Data = WindowState == WindowState.Maximized ? RestoreGeometry : MaximizeGeometry;
}
