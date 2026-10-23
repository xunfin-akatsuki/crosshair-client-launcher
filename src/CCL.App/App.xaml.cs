using System.Windows;
using System.Windows.Threading;
using CCL.App.Services;
using CCL.Core.Configuration;
using CCL.Core.Diagnostics;
using CCL.App.Localization;
using CCL.Core.Layout;

namespace CCL.App;

public partial class App
{
    public static CclLayout Layout { get; private set; } = null!;

    public static CclLog Log { get; private set; } = null!;

    public static CclSettings Settings { get; private set; } = null!;

    public static LauncherService Launcher { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Layout = CclLayout.CreateDefault();
        Layout.EnsureCreated();

        Log = new CclLog(Layout.LogsPath);
        Log.Info("App", $"Crosshair Client Launcher 启动，数据目录：{Layout.RootPath}");

        Settings = CclSettings.Load(Layout.SettingsPath);
        if (!string.IsNullOrWhiteSpace(Settings.MinecraftPath))
            Layout = new CclLayout(Layout.RootPath, Settings.MinecraftPath);
        else
            Settings.MinecraftPath = Layout.MinecraftPath;
        Settings.Save(Layout.SettingsPath);

        Lang.Apply(Settings.Language);

        Launcher = new LauncherService(Layout, Settings, Log);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("App", "未处理的后台异常", args.ExceptionObject as Exception);

        base.OnStartup(e);
        new MainWindow(Launcher).Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("App", "界面线程未处理异常", e.Exception);
        MessageBox.Show(e.Exception.Message, "Crosshair Client Launcher 发生错误", MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("App", "启动器退出");
        Launcher.Dispose();
        Log.Dispose();
        base.OnExit(e);
    }
}
