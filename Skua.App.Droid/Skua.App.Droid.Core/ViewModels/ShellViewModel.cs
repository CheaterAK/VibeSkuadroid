using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Memory;

namespace Skua.App.Droid.Core.ViewModels;

public enum AppPage
{
    Home,
    Scripts,
    Control,
    World,
    Console,
    Plugins,
    Settings,
}

/// <summary>
/// Root ViewModel. One per process (it outlives activities: a recreated activity gets a new view bound to this
/// same instance), so the UI state and the engine survive rotation, backgrounding and activity destruction.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly EngineHost _host;
    private EngineSnapshot? _pendingSnapshot;
    private int _snapshotPosted;
    private bool _wasOnline;
    private volatile bool _uiVisible = true;

    public ShellViewModel(AppServices services)
    {
        _services = services;
        _host = services.Host;
        Login = new LoginViewModel(services);
        Dashboard = new DashboardViewModel(services);
        Scripts = new ScriptsViewModel(services);
        World = new WorldViewModel(services);
        Control = new ControlViewModel(services);
        Control.OpenListsRequested += (_, _) => CurrentPage = AppPage.World;
        Console = new ConsoleViewModel(services);
        Plugins = new PluginsViewModel(services);
        Plugins.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PluginsViewModel.Title))
                OnPropertyChanged(nameof(Title));
        };
        Settings = new SettingsViewModel(services);

        _host.Engine.SnapshotChanged += OnSnapshotChanged;
        _host.MemoryVerdictChanged += OnMemoryVerdictChanged;
        Apply(_host.Engine.Snapshot);
        RefreshPlatformState();
    }

    public LoginViewModel Login { get; }

    public DashboardViewModel Dashboard { get; }

    public ScriptsViewModel Scripts { get; }

    /// <summary>The World lists (inventory, bank, quests, drops, monsters); opened from the Control page.</summary>
    public WorldViewModel World { get; }

    /// <summary>The Control page: schematic map, current cell and manual actions.</summary>
    public ControlViewModel Control { get; }

    public ConsoleViewModel Console { get; }

    /// <summary>The built-in plugins (Daily Tracker, Butler, Cosmetics) and their pages.</summary>
    public PluginsViewModel Plugins { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentContent), nameof(Title), nameof(IsHomePage), nameof(IsScriptsPage), nameof(IsWorldPage), nameof(IsControlPage), nameof(IsConsolePage), nameof(IsPluginsPage), nameof(IsSettingsPage))]
    private AppPage _currentPage = AppPage.Home;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentContent))]
    private bool _showDashboard;

    [ObservableProperty]
    private string _connectionText = "Offline";

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private string _memoryText = string.Empty;

    [ObservableProperty]
    private bool _showNotificationBanner;

    [ObservableProperty]
    private bool _showBatteryBanner;

    [ObservableProperty]
    private bool _showMemoryBanner;

    [ObservableProperty]
    private string _memoryBannerText = string.Empty;

    /// <summary>The page body: one of the child ViewModels (the view maps each type to its view).</summary>
    public object CurrentContent => CurrentPage switch
    {
        AppPage.Home => ShowDashboard ? Dashboard : Login,
        AppPage.Scripts => Scripts,
        AppPage.Control => Control,
        AppPage.World => World,
        AppPage.Console => Console,
        AppPage.Plugins => Plugins,
        _ => Settings,
    };

    public string Title => CurrentPage switch
    {
        AppPage.Home => ShowDashboard ? "Dashboard" : "Login",
        AppPage.Scripts => "Scripts",
        AppPage.Control => "Control",
        AppPage.World => "World",
        AppPage.Console => "Console",
        AppPage.Plugins => Plugins.Title,
        _ => "Settings",
    };

    public bool IsHomePage => CurrentPage == AppPage.Home;

    public bool IsScriptsPage => CurrentPage == AppPage.Scripts;

    public bool IsWorldPage => CurrentPage == AppPage.World;

    /// <summary>The bottom bar's Control entry is lit on the Control page and on the World lists opened from it.</summary>
    public bool IsControlPage => CurrentPage is AppPage.Control or AppPage.World;

    public bool IsConsolePage => CurrentPage == AppPage.Console;

    public bool IsPluginsPage => CurrentPage == AppPage.Plugins;

    public bool IsSettingsPage => CurrentPage == AppPage.Settings;

    [RelayCommand]
    private void Navigate(string? page)
    {
        if (Enum.TryParse(page, ignoreCase: true, out AppPage target))
            CurrentPage = target;
    }

    /// <summary>System back: returns to the home page first; false when already there (the activity then goes to the background).</summary>
    public bool GoBack()
    {
        if (CurrentPage == AppPage.Scripts && (Scripts.CloseOverlay() || Scripts.GoUp()))
            return true;
        if (CurrentPage == AppPage.Plugins && Plugins.CloseSubPage())
            return true;
        if (CurrentPage == AppPage.Control && Control.CloseOverlay())
            return true;
        if (CurrentPage == AppPage.World)
        {
            CurrentPage = AppPage.Control;
            return true;
        }
        if (CurrentPage != AppPage.Home)
        {
            CurrentPage = AppPage.Home;
            return true;
        }
        return false;
    }

    /// <summary>Re-reads permission and battery state (call when the activity resumes: the user may have changed them).</summary>
    public void RefreshPlatformState()
    {
        ShowNotificationBanner = !_host.Platform.NotificationsAllowed;
        ShowBatteryBanner = !_host.Platform.IgnoringBatteryOptimizations && !_host.Settings.Current.BatteryPromptDismissed;
        Settings.RefreshPlatformState();
    }

    [RelayCommand]
    private void RequestNotifications() => _host.Platform.RequestNotificationPermission();

    [RelayCommand]
    private void RequestBatteryExemption() => _host.Platform.RequestIgnoreBatteryOptimizations();

    [RelayCommand]
    private void DismissBatteryBanner()
    {
        _host.Settings.Update(s => s with { BatteryPromptDismissed = true });
        ShowBatteryBanner = false;
    }

    [RelayCommand]
    private void RestartEngine()
    {
        if (!_host.Platform.CanRestartProcess)
            MemoryBannerText = "Restart is only available on Android.";
        else if (!_host.RequestRestart("restart requested from the memory banner"))
            MemoryBannerText = "Stop the script first; the engine restarts once it is idle.";
    }

    partial void OnCurrentPageChanged(AppPage value)
    {
        World.IsActive = value == AppPage.World;
        Control.IsActive = value == AppPage.Control;
        Console.IsActive = value == AppPage.Console;
        Plugins.IsActive = value == AppPage.Plugins;
        if (value == AppPage.Scripts)
            Scripts.Refresh();
    }

    /// <summary>
    /// The platform reports whether the UI is showing (Android: activity started/stopped). While it is hidden, engine
    /// snapshots and console lines are not pushed into the bound properties and lists (no layout work for a window
    /// that is not there); the latest state is applied once when the UI shows again.
    /// </summary>
    public void SetUiVisible(bool visible)
    {
        _uiVisible = visible;
        Console.SetUiVisible(visible);
        Control.SetUiVisible(visible);
        if (visible)
            PostSnapshot();
    }

    private void OnSnapshotChanged(object? sender, EngineSnapshot snapshot)
    {
        Volatile.Write(ref _pendingSnapshot, snapshot);
        PostSnapshot();
    }

    private void PostSnapshot()
    {
        if (_uiVisible && Interlocked.Exchange(ref _snapshotPosted, 1) == 0)
        {
            _services.Dispatcher.Post(() =>
            {
                Volatile.Write(ref _snapshotPosted, 0);
                if (Volatile.Read(ref _pendingSnapshot) is EngineSnapshot latest)
                    Apply(latest);
            });
        }
    }

    private void OnMemoryVerdictChanged(object? sender, MemoryVerdict verdict) =>
        _services.Dispatcher.Post(() => ApplyVerdict(verdict));

    private void Apply(EngineSnapshot s)
    {
        if (s.Connection == ConnectionPhase.Online)
            _wasOnline = true;
        else if (s.Connection is ConnectionPhase.Offline or ConnectionPhase.Failed)
            _wasOnline = false;

        ShowDashboard = s.Connection is ConnectionPhase.Online or ConnectionPhase.Reconnecting
            || (s.Connection == ConnectionPhase.Connecting && _wasOnline);
        IsOnline = s.IsOnline;
        IsBusy = s.IsBusy;
        IsFailed = s.Connection == ConnectionPhase.Failed;
        ConnectionText = s.Connection switch
        {
            ConnectionPhase.Online => s.ServerName ?? "Online",
            ConnectionPhase.LoggingIn => "Logging in",
            ConnectionPhase.Connecting => "Connecting",
            ConnectionPhase.Reconnecting => "Reconnecting",
            ConnectionPhase.Failed => "Error",
            _ => "Offline",
        };
        MemoryText = Formatting.Memory(s.Memory);
        OnPropertyChanged(nameof(Title));

        Login.Apply(s);
        Dashboard.Apply(s);
        Scripts.Apply(s);
        World.Apply(s);
        Control.Apply(s);
        Plugins.Apply(s);
        Settings.Apply(s);
        ApplyVerdict(_host.MemoryVerdict);
    }

    private void ApplyVerdict(MemoryVerdict verdict)
    {
        ShowMemoryBanner = verdict is MemoryVerdict.RestartPending or MemoryVerdict.ThresholdTooLow;
        MemoryBannerText = verdict switch
        {
            MemoryVerdict.ThresholdTooLow => "Memory threshold is below what the app needs at start; raise it in Settings.",
            MemoryVerdict.RestartPending when _host.Engine.Snapshot.Script.IsBusy => "Memory is high. The engine will restart after the script stops.",
            MemoryVerdict.RestartPending => "Memory is high. Restart the engine to free it.",
            _ => string.Empty,
        };
        Dashboard.MemoryVerdict = verdict;
    }

    public void Dispose()
    {
        _host.Engine.SnapshotChanged -= OnSnapshotChanged;
        _host.MemoryVerdictChanged -= OnMemoryVerdictChanged;
        Console.Dispose();
        Plugins.Dispose();
    }
}
