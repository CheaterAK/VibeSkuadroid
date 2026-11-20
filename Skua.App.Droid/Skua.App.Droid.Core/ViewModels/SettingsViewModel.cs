using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Settings;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>App settings, permission state, engine restart and build information, and the Tools hub.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly EngineHost _host;
    private bool _loading;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _host = services.Host;
        Load(_host.Settings.Current);
        Tools = new ToolsViewModel();
        GamePages = GameToolPages.AddTo(Tools, services);
        _notificationTools = NotificationTools.AddTo(Tools, services);
        SkillPages = SkillToolPages.AddTo(Tools, services);
        DiagnosticPages = DiagnosticsToolPages.AddTo(Tools, services);
        Tools.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolsViewModel.CurrentTitle) || e.PropertyName == nameof(ToolsViewModel.CurrentPage))
                OnPropertyChanged(nameof(Title));
        };
    }

    /// <summary>The game tool pages of the hub (Fast Travel, Loader, Grabber).</summary>
    public GameToolPages GamePages { get; }

    /// <summary>The skill tool pages of the hub (Advanced Skills, CoreBots options).</summary>
    public SkillToolPages SkillPages { get; }

    /// <summary>The Diagnostics page of the hub.</summary>
    public DiagnosticsToolPages DiagnosticPages { get; }

    /// <summary>"Settings", "Tools", or the open tool's title.</summary>
    public string Title => !ShowTools ? "Settings" : Tools.CurrentPage is null ? "Tools" : Tools.CurrentTitle;

    private readonly NotificationTools? _notificationTools;

    /// <summary>The Tools hub (opened from this page).</summary>
    public ToolsViewModel Tools { get; }

    /// <summary>The Tools hub is shown instead of the settings.</summary>
    [ObservableProperty]
    private bool _showTools;

    /// <summary>The Settings tab is shown.</summary>
    [ObservableProperty]
    private bool _isActive;

    partial void OnShowToolsChanged(bool value)
    {
        UpdateToolsVisible();
        OnPropertyChanged(nameof(Title));
    }

    partial void OnIsActiveChanged(bool value) => UpdateToolsVisible();

    private bool _uiVisible = true;

    /// <summary>The app's UI shows (Android: activity started) or not; hidden, the tool pages read nothing.</summary>
    public void SetUiVisible(bool visible)
    {
        _uiVisible = visible;
        UpdateToolsVisible();
    }

    private void UpdateToolsVisible()
    {
        if (_notificationTools is not null)
            _notificationTools.Visible = IsActive && ShowTools;
        DiagnosticPages.Visible = IsActive && ShowTools && _uiVisible;
    }

    [RelayCommand]
    private void OpenTools() => ShowTools = true;

    [RelayCommand]
    private void CloseTools() => ShowTools = false;

    /// <summary>Back on this tab: closes an open tool page, then the Tools hub; false when the settings are shown.</summary>
    public bool CloseSubPage()
    {
        if (!ShowTools)
            return false;
        if (GamePages.CancelQuestion())
            return true;
        if (SkillPages.GoBack())
            return true;
        if (!Tools.CloseSubPage())
            ShowTools = false;
        return true;
    }

    public void Dispose()
    {
        _notificationTools?.Dispose();
        DiagnosticPages.Dispose();
    }

    [ObservableProperty]
    private bool _autoReconnect;

    [ObservableProperty]
    private bool _autoRestartOnMemory;

    [ObservableProperty]
    private decimal? _memoryThresholdMb;

    [ObservableProperty]
    private bool _wifiLock;

    [ObservableProperty]
    private string _clientVersion = string.Empty;

    [ObservableProperty]
    private bool _hasRememberedPassword;

    [ObservableProperty]
    private bool _notificationsAllowed;

    [ObservableProperty]
    private bool _batteryExempt;

    [ObservableProperty]
    private string _memoryText = string.Empty;

    [ObservableProperty]
    private string? _message;

    public decimal MinThreshold => AppSettings.MinMemoryThresholdMb;

    public decimal MaxThreshold => AppSettings.MaxMemoryThresholdMb;

    public bool CanRestart => _host.Platform.CanRestartProcess;

    public string VersionText => $"VibeSkua Android {_host.Platform.AppVersion}";

    public string DeviceText => _host.Platform.DeviceDescription;

    public string ScriptsPath => _services.Paths.ScriptsDir;

    public string ScriptSupportText => _host.Engine.ScriptSupport.Available ? "Scripts: available" : "Scripts: " + _host.Engine.ScriptSupport.Reason;

    public bool CanRememberPasswords => _host.Vault.IsAvailable;

    private void Load(AppSettings s)
    {
        _loading = true;
        AutoReconnect = s.AutoReconnect;
        AutoRestartOnMemory = s.AutoRestartOnMemory;
        MemoryThresholdMb = s.MemoryThresholdMb;
        WifiLock = s.WifiLockWhileScriptRuns;
        ClientVersion = s.ClientVersion ?? string.Empty;
        HasRememberedPassword = _host.RememberedAccount is not null; // metadata only, nothing decrypted
        _loading = false;
    }

    partial void OnAutoReconnectChanged(bool value) => Save(s => s with { AutoReconnect = value });

    partial void OnAutoRestartOnMemoryChanged(bool value) => Save(s => s with { AutoRestartOnMemory = value });

    partial void OnMemoryThresholdMbChanged(decimal? value)
    {
        if (value is decimal mb)
            Save(s => s with { MemoryThresholdMb = (int)mb });
    }

    partial void OnWifiLockChanged(bool value) => Save(s => s with { WifiLockWhileScriptRuns = value });

    partial void OnClientVersionChanged(string value) => Save(s => s with { ClientVersion = value });

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (_loading)
            return;
        try
        {
            _host.Settings.Update(change);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not save settings: {e.Message}";
        }
    }

    [RelayCommand]
    private void ForgetPassword()
    {
        _host.ForgetPassword();
        HasRememberedPassword = false;
        Message = "The saved password was deleted.";
    }

    [RelayCommand]
    private void RequestNotifications() => _host.Platform.RequestNotificationPermission();

    [RelayCommand]
    private void RequestBatteryExemption() => _host.Platform.RequestIgnoreBatteryOptimizations();

    [RelayCommand]
    private void RestartEngine()
    {
        if (!_host.Platform.CanRestartProcess)
        {
            Message = "Restart is only available on Android.";
            return;
        }
        if (!_host.RequestRestart("restart requested in settings"))
            Message = "Stop the script first.";
    }

    public void RefreshPlatformState()
    {
        NotificationsAllowed = _host.Platform.NotificationsAllowed;
        BatteryExempt = _host.Platform.IgnoringBatteryOptimizations;
    }

    internal void Apply(EngineSnapshot s)
    {
        GamePages.Apply(s);
        SkillPages.Apply(s);
        MemoryText = $"{Formatting.Memory(s.Memory)}, peak {(s.Memory.PeakRssBytes < 0 ? "n/a" : $"{s.Memory.PeakRssBytes / 1048576.0:0} MB")}";
    }
}
