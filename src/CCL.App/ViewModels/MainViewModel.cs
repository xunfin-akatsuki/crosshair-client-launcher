using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using CCL.App.Localization;
using CCL.App.Services;
using CCL.App.Views;
using CCL.Core.Accounts;
using CCL.Core.Minecraft.Instances;
using CCL.Core.Minecraft.Versioning;
using CCL.Core.Operations;

namespace CCL.App.ViewModels;

public sealed record LanguageOption(string Value, string Display)
{
    // 供屏幕阅读器与自动化使用，也让默认渲染显示名称而不是记录转储
    public override string ToString() => Display;
}

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly string[] StageTexts =
        ["准备中", "核心文件", "资源文件", "解压原生库", "完成"];

    private readonly LauncherService _service;
    private readonly SynchronizationContext? _uiContext;
    private readonly OperationQueue _queue;
    private InstanceInfo? _selectedInstance;
    private ManifestVersion? _selectedVersion;
    private bool _showDownload;
    private bool _showSnapshots;
    private bool _isLaunching;
    private bool _tasksExpanded = true;
    private LanguageOption? _selectedLanguage;
    private bool _suppressLanguageChange;
    private string _statusText = Lang.Text("Vm.Ready");
    private string _playerName = "Player";
    private int _memoryMb = 4096;

    public MainViewModel(LauncherService service)
    {
        _service = service;
        _uiContext = SynchronizationContext.Current;

        _playerName = service.Settings.OfflinePlayerName is { Length: > 0 } name ? name : "Player";
        _memoryMb = service.Settings.MaxMemoryMb;
        _showSnapshots = service.Settings.ShowSnapshots;

        _queue = new OperationQueue(_uiContext, message => _service.Log.Info("Queue", message));
        _queue.ActivityChanged += (_, _) => OnQueueActivity();
        _queue.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTasks));

        RefreshCommand = new RelayCommand(_ => RefreshInstances());
        ShowDownloadCommand = new RelayCommand(_ => ShowDownload = true);
        ShowInstanceCommand = new RelayCommand(_ => ShowDownload = false);
        InstallCommand = new RelayCommand(parameter => EnqueueInstall(parameter as ManifestVersion));
        LaunchCommand = new AsyncRelayCommand(_ => LaunchAsync(), _ => SelectedInstance?.IsComplete == true && !IsLaunching);
        CancelTaskCommand = new RelayCommand(parameter => { if (parameter is QueueItem item) _queue.Cancel(item); });
        CancelAllTasksCommand = new RelayCommand(_ => _queue.CancelAll());
        ClearFinishedTasksCommand = new RelayCommand(_ => _queue.ClearFinished());
        ToggleTasksCommand = new RelayCommand(_ => IsTasksExpanded = !IsTasksExpanded);
        DeleteInstanceCommand = new RelayCommand(parameter => DeleteInstance(parameter as InstanceInfo));
        LoginMicrosoftCommand = new AsyncRelayCommand(_ => LoginMicrosoftAsync());
        UseOfflineCommand = new RelayCommand(_ => UseOfflineAccount());
        SignOutCommand = new RelayCommand(_ => SignOutMicrosoft());
        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(_service.Layout.LogsPath));
        OpenGameFolderCommand = new RelayCommand(_ => OpenFolder(_service.Layout.MinecraftPath));

        BuildLanguageOptions();
        _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Value == Lang.Preference) ?? LanguageOptions[0];

        RefreshInstances();
        _ = LoadVersionsAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<InstanceInfo> Instances { get; } = [];

    public ObservableCollection<ManifestVersion> Versions { get; } = [];

    public ObservableCollection<QueueItem> Tasks => _queue.Items;

    public RelayCommand RefreshCommand { get; }

    public RelayCommand ShowDownloadCommand { get; }

    public RelayCommand ShowInstanceCommand { get; }

    public RelayCommand InstallCommand { get; }

    public AsyncRelayCommand LaunchCommand { get; }

    public RelayCommand CancelTaskCommand { get; }

    public RelayCommand CancelAllTasksCommand { get; }

    public RelayCommand ClearFinishedTasksCommand { get; }

    public RelayCommand ToggleTasksCommand { get; }

    public RelayCommand DeleteInstanceCommand { get; }

    public AsyncRelayCommand LoginMicrosoftCommand { get; }

    public RelayCommand UseOfflineCommand { get; }

    public RelayCommand SignOutCommand { get; }

    public RelayCommand SaveSettingsCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand OpenGameFolderCommand { get; }

    public InstanceInfo? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!Set(ref _selectedInstance, value)) return;
            OnPropertyChanged(nameof(CanLaunchHint));
            OnPropertyChanged(nameof(CurrentInstanceTitle));
            OnPropertyChanged(nameof(CurrentInstanceType));
            OnPropertyChanged(nameof(CurrentInstanceJava));
            OnPropertyChanged(nameof(CurrentInstanceSize));
            OnPropertyChanged(nameof(CurrentInstanceDirectory));
            LaunchCommand.RaiseCanExecuteChanged();
        }
    }

    public ManifestVersion? SelectedVersion
    {
        get => _selectedVersion;
        set => Set(ref _selectedVersion, value);
    }

    public bool ShowDownload
    {
        get => _showDownload;
        set
        {
            if (!Set(ref _showDownload, value)) return;
            OnPropertyChanged(nameof(ShowInstance));

            // 回到实例页时把任务面板折叠起来，把空间让给实例内容
            if (!value) IsTasksExpanded = false;
        }
    }

    /// <summary>任务面板是否展开。折叠时只留一行摘要。</summary>
    public bool IsTasksExpanded
    {
        get => _tasksExpanded;
        set => Set(ref _tasksExpanded, value);
    }

    public bool ShowInstance => !_showDownload;

    public bool HasTasks => Tasks.Count > 0;

    /// <summary>当前正在执行的任务，状态栏直接绑定它的进度与阶段。</summary>
    public QueueItem? CurrentTask => Tasks.FirstOrDefault(t => t.State == OperationState.Running);

    public bool IsBusy => CurrentTask is not null;

    /// <summary>折叠状态下显示的一行摘要。</summary>
    public string TasksSummary => CurrentTask is { } current
        ? Lang.Format("Vm.TaskSummaryRunning", Tasks.Count, current.Title)
        : Lang.Format("Vm.TaskSummaryIdle", Tasks.Count);

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (!Set(ref _isLaunching, value)) return;
            LaunchCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string PlayerName
    {
        get => _playerName;
        set => Set(ref _playerName, value);
    }

    public int MemoryMb
    {
        get => _memoryMb;
        set => Set(ref _memoryMb, value);
    }

    public bool ShowSnapshots
    {
        get => _showSnapshots;
        set
        {
            if (!Set(ref _showSnapshots, value)) return;
            _ = LoadVersionsAsync();
        }
    }

    public string CanLaunchHint => SelectedInstance switch
    {
        null => Lang.Text("Vm.HintSelectInstance"),
        { IsComplete: false } => Lang.Text("Vm.HintIncomplete"),
        _ => Lang.Text("Vm.HintOffline")
    };

    public string DataDirectory => _service.Layout.MinecraftPath;

    public void SaveSettings()
    {
        _service.Settings.OfflinePlayerName = string.IsNullOrWhiteSpace(PlayerName) ? "Player" : PlayerName.Trim();
        _service.Settings.MaxMemoryMb = Math.Clamp(MemoryMb, 512, 65536);
        _service.Settings.ShowSnapshots = ShowSnapshots;
        _service.Settings.Save(_service.Layout.SettingsPath);
    }

    private void OnQueueActivity()
    {
        var pending = Tasks.Count(t => t.State == OperationState.Pending);
        var current = CurrentTask;
        OnPropertyChanged(nameof(CurrentTask));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(TasksSummary));

        StatusText = current is not null
            ? Lang.Format("Vm.Running", current.Title,
                pending > 0 ? Lang.Format("Vm.Queued", pending) : string.Empty)
            : HasTasks
                ? Lang.Format("Vm.QueueIdle", Tasks.Count)
                : Instances.Count == 0
                    ? Lang.Text("Vm.NoInstances")
                    : Lang.Format("Vm.InstanceCount", Instances.Count);
    }

    /// <summary>实例详情的展示文本，用 VM 属性承载以便语言切换时刷新。</summary>
    public string CurrentInstanceTitle => SelectedInstance?.Id ?? Lang.Text("App.NoInstance");

    public string CurrentInstanceType => SelectedInstance?.Type ?? "—";

    public string CurrentInstanceJava => SelectedInstance?.JavaLabel ?? "—";

    public string CurrentInstanceSize => SelectedInstance?.SizeLabel ?? "—";

    public string CurrentInstanceDirectory => SelectedInstance?.Directory ?? "—";

    public string GameFolderText => Lang.Format("App.GameFolder", _service.Layout.MinecraftPath);

    public ObservableCollection<LanguageOption> LanguageOptions { get; } = [];

    public LanguageOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!Set(ref _selectedLanguage, value) || value is null || _suppressLanguageChange) return;

            _service.Settings.Language = value.Value;
            _service.Settings.Save(_service.Layout.SettingsPath);
            Lang.Apply(value.Value);

            _suppressLanguageChange = true;
            try
            {
                BuildLanguageOptions();
                _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Value == value.Value);
                OnPropertyChanged(nameof(SelectedLanguage));
            }
            finally
            {
                _suppressLanguageChange = false;
            }

            RefreshLocalizedText();
        }
    }

    private void BuildLanguageOptions()
    {
        LanguageOptions.Clear();
        LanguageOptions.Add(new LanguageOption(Lang.SystemPreference, Lang.Text("App.LanguageSystem")));
        LanguageOptions.Add(new LanguageOption(Lang.Chinese, "简体中文"));
        LanguageOptions.Add(new LanguageOption(Lang.English, "English"));
    }

    /// <summary>语言变化后把由 C# 生成的文案全部重发一遍通知。</summary>
    private void RefreshLocalizedText()
    {
        foreach (var name in new[]
                 {
                     nameof(CurrentInstanceTitle), nameof(CurrentInstanceType), nameof(CurrentInstanceJava),
                     nameof(CurrentInstanceSize), nameof(CurrentInstanceDirectory), nameof(GameFolderText),
                     nameof(AccountLabel), nameof(AccountHint), nameof(CanLaunchHint), nameof(TasksSummary)
                 })
            OnPropertyChanged(name);

        OnQueueActivity();
    }

    /// <summary>当前账号的展示文本。</summary>
    public string AccountLabel    {
        get
        {
            var account = _service.Accounts.Current;
            return account is null
                ? Lang.Text("Vm.AccountNone")
                : Lang.Format("Vm.AccountLabel", account.PlayerName, account.TypeLabel);
        }
    }

    public string AccountHint => _service.Accounts.IsMicrosoftConfigured
        ? Lang.Text("Vm.AccountHintConfigured")
        : Lang.Text("Vm.AccountHintMissing");

    private async Task LoginMicrosoftAsync()
    {
        _service.Settings.Save(_service.Layout.SettingsPath);

        var dialog = new LoginDialog(_service.Accounts, _service.Settings)
        {
            Owner = Application.Current.MainWindow
        };

        var ok = dialog.ShowDialog();
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(AccountHint));
        OnPropertyChanged(nameof(CanLaunchHint));

        if (ok == true && dialog.Result is { } account)
        {
            PlayerName = account.PlayerName;
            StatusText = Lang.Format("Vm.SignInOk", account.PlayerName);
        }
    }

    private void UseOfflineAccount()
    {
        var name = string.IsNullOrWhiteSpace(PlayerName) ? "Player" : PlayerName.Trim();
        _service.Accounts.AddOffline(name);
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(CanLaunchHint));
        StatusText = Lang.Format("Vm.OfflineOk", name);
    }

    private void SignOutMicrosoft()
    {
        var current = _service.Accounts.Current;
        if (current is null || !current.IsMicrosoft)
        {
            StatusText = Lang.Text("Vm.NotMicrosoft");
            return;
        }

        _service.Accounts.Remove(current.Id);
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(CanLaunchHint));
        StatusText = Lang.Format("Vm.SignOutOk", current.PlayerName);
    }

    private void RefreshInstances()
    {
        var previousId = SelectedInstance?.Id;
        Instances.Clear();
        foreach (var instance in _service.ScanInstances()) Instances.Add(instance);

        SelectedInstance = Instances.FirstOrDefault(i => i.Id == previousId) ?? Instances.FirstOrDefault();
        if (!HasTasks)
            StatusText = Lang.Format(Instances.Count == 0 ? "Vm.NoInstances" : "Vm.InstanceCount", Instances.Count);
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            StatusText = Lang.Text("Vm.LoadingVersions");
            var versions = await _service.GetVersionsAsync(ShowSnapshots);

            Versions.Clear();
            foreach (var version in versions) Versions.Add(version);
            SelectedVersion = Versions.FirstOrDefault();
            if (!HasTasks) StatusText = Lang.Format("Vm.VersionCount", Versions.Count);
        }
        catch (Exception ex)
        {
            StatusText = Lang.Format("Vm.VersionLoadFailed", ex.Message);
            _service.Log.Error("UI", "加载版本列表失败", ex);
        }
    }

    /// <summary>把安装包装成队列项。真正的取消由队列通过令牌发起。</summary>
    private void EnqueueInstall(ManifestVersion? version)
    {
        version ??= SelectedVersion;
        if (version is null)
        {
            StatusText = Lang.Text("Vm.SelectVersionFirst");
            return;
        }

        _queue.Enqueue(new InstallOperation(_service, version, () => _uiContext?.Post(_ => AfterInstall(version), null)));
        IsTasksExpanded = true;
    }

    /// <summary>删除实例：先确认，再只删 versions 下的版本目录。</summary>
    private void DeleteInstance(InstanceInfo? instance)
    {
        if (instance is null) return;

        if (Tasks.Any(t => !t.IsFinished && t.Title.Contains(instance.Id, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = Lang.Format("Vm.DeleteBlocked", instance.Id);
            return;
        }

        var size = InstanceManager.GetSizeBytes(instance.Directory);
        var sizeText = size >= 1024 * 1024
            ? $"{size / 1024d / 1024d:F1} MB"
            : $"{size / 1024d:F0} KB";

        var dialog = new ConfirmDialog(
            Lang.Text("Vm.DeleteTitle"),
            Lang.Format("Vm.DeleteBody", instance.Id, sizeText),
            Lang.Text("App.Delete"))
        { Owner = Application.Current.MainWindow };

        if (dialog.ShowDialog() != true) return;

        var result = InstanceManager.Delete(instance);
        RefreshInstances();
        StatusText = result.Describe();
        if (!result.Succeeded) _service.Log.Warn("UI", $"删除实例 {instance.Id} 失败：{result.Error}");
    }

    private void AfterInstall(ManifestVersion version)
    {
        var hadTasks = HasTasks;
        RefreshInstances();
        SelectedInstance = Instances.FirstOrDefault(i => i.Id == version.Id) ?? SelectedInstance;
        if (!hadTasks) OnQueueActivity();
    }

    private async Task LaunchAsync()
    {
        if (SelectedInstance is not { } instance) return;

        try
        {
            IsLaunching = true;
            StatusText = Lang.Format("Vm.Starting", instance.Id);
            SaveSettings();

            var process = await Task.Run(() => _service.LaunchAsync(instance));
            StatusText = Lang.Format("Vm.Started", instance.Id, process.Id);

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (!HasTasks) StatusText = Lang.Format("Vm.Exited", instance.Id, process.HasExited ? process.ExitCode : -1);
            });
        }
        catch (Exception ex)
        {
            StatusText = Lang.Format("Vm.LaunchFailed", ex.Message);
            _service.Log.Error("UI", $"启动 {instance.Id} 失败", ex);
        }
        finally
        {
            IsLaunching = false;
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose() => _queue.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>单个版本的安装任务，把安装进度折叠成队列能理解的阶段名。</summary>
    private sealed class InstallOperation(LauncherService service, ManifestVersion version, Action onCompleted)
        : IQueuedOperation
    {
        public string Title => CCL.Core.Localization.CoreText.Format("Task.Install", version.Id);

        public async Task RunAsync(IProgress<OperationProgress> progress, CancellationToken token)
        {
            var adapter = new InstallProgressAdapter(progress);
            var result = await service.InstallAsync(version, adapter, token).ConfigureAwait(false);
            if (!result.AllSucceeded)
                throw new InvalidOperationException(Lang.Format("Vm.SomeFilesFailed", result.Failed));

            onCompleted();
        }
    }

    private sealed class InstallProgressAdapter(IProgress<OperationProgress> inner) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) =>
            inner.Report(new OperationProgress(value.Stage, value.Fraction));
    }
}
