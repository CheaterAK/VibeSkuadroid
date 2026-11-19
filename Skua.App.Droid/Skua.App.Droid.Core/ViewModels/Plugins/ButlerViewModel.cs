using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Plugins;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>A player name the page offers (recent target or someone in the room), with its "use" action.</summary>
public sealed class ButlerNameRow
{
    public ButlerNameRow(string name, IRelayCommand<string?> use)
    {
        Name = name;
        UseCommand = use;
    }

    public string Name { get; }

    public IRelayCommand<string?> UseCommand { get; }
}

/// <summary>
/// ButlerV5 for one account (port of the desktop plugin's single-account parts): follow a player by name, send this
/// account to a player, jump to a player in the room, the follow options, the recent targets and the plugin's log.
/// Summon / roster / send-all need several local accounts (army) and are not offered.
/// </summary>
public sealed partial class ButlerViewModel : ObservableObject, IDisposable
{
    public const string PluginId = "butler";
    public const int MaxTargets = 20;
    public const int ShownLogLines = 300;

    // Option keys: the desktop plugin's option names.
    internal const string KeyPassiveAttack = "passiveAttack";
    internal const string KeyGotoDelay = "gotoDelay";
    internal const string KeyAlwaysGoto = "alwaysGoto";
    internal const string KeyCopyWalk = "copyWalk";
    internal const string KeyLeechMode = "leechMode";
    internal const string KeyPark = "parkLocation";
    internal const string KeyRescue = "rescueThreshold";
    internal const string KeyInstantWarnings = "instantWarnings";
    internal const string KeyClassType = "classType";
    internal const string KeyDebugLogs = "debugLogs";
    internal const string KeyTargets = "targets";

    private readonly AppServices _services;
    private readonly EngineHost _host;
    private readonly PluginStore _store;
    private readonly IButlerBackend? _backend;
    private bool _loading;
    private int _logPosted;
    private int _statusPosted;

    public ButlerViewModel(AppServices services, PluginStore store)
    {
        _services = services;
        _host = services.Host;
        _store = store;
        _backend = _host.Engine.Plugins?.Butler;
        IsAvailable = _backend is not null;
        LoadOptions();
        if (_backend is not null)
        {
            _backend.Options = CurrentOptions();
            _backend.StatusChanged += OnStatusChanged;
            _backend.LogChanged += OnLogChanged;
            ApplyStatus(_backend.Status);
        }
        else
        {
            Message = "ButlerV5 needs the Skua script host, which is not available in this engine.";
        }
        Apply(_host.Engine.Snapshot);
    }

    public bool IsAvailable { get; }

    public IReadOnlyList<string> ParkChoices { get; } = Enum.GetNames<ButlerParkSpot>();

    public IReadOnlyList<string> ClassChoices { get; } = Enum.GetNames<ButlerClassChoice>();

    public ObservableCollection<ButlerNameRow> RecentTargets { get; } = new();

    public ObservableCollection<ButlerNameRow> RoomPlayers { get; } = new();

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FollowCommand), nameof(GotoCommand), nameof(JumpCommand))]
    private bool _isOnline;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FollowCommand), nameof(GotoCommand), nameof(JumpCommand))]
    private string _playerName = string.Empty;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(StopAndParkCommand))]
    private bool _isFollowing;

    [ObservableProperty]
    private string _statusText = ButlerStatus.Idle.Detail;

    [ObservableProperty]
    private string _stateText = nameof(ButlerFollowState.Idle);

    [ObservableProperty]
    private IReadOnlyList<string> _logLines = [];

    // ---- options (saved on every change)

    [ObservableProperty]
    private bool _passiveAttack;

    [ObservableProperty]
    private decimal _gotoDelayMs = 500;

    [ObservableProperty]
    private bool _alwaysGoto;

    [ObservableProperty]
    private bool _copyWalk;

    [ObservableProperty]
    private bool _leechMode;

    [ObservableProperty]
    private string _park = nameof(ButlerParkSpot.House);

    [ObservableProperty]
    private decimal _rescueThreshold = 3;

    [ObservableProperty]
    private bool _instantWarnings = true;

    [ObservableProperty]
    private string _classType = nameof(ButlerClassChoice.None);

    [ObservableProperty]
    private bool _debugLogs = true;

    partial void OnPassiveAttackChanged(bool value) => SaveOptions();

    partial void OnGotoDelayMsChanged(decimal value) => SaveOptions();

    partial void OnAlwaysGotoChanged(bool value) => SaveOptions();

    partial void OnCopyWalkChanged(bool value) => SaveOptions();

    partial void OnLeechModeChanged(bool value) => SaveOptions();

    partial void OnParkChanged(string value) => SaveOptions();

    partial void OnRescueThresholdChanged(decimal value) => SaveOptions();

    partial void OnInstantWarningsChanged(bool value) => SaveOptions();

    partial void OnClassTypeChanged(string value) => SaveOptions();

    partial void OnDebugLogsChanged(bool value) => SaveOptions();

    partial void OnIsActiveChanged(bool value)
    {
        if (!value)
            return;
        RefreshLog();
        RefreshPlayers();
    }

    /// <summary>The options as the backend reads them.</summary>
    public ButlerOptions CurrentOptions() => new ButlerOptions
    {
        PassiveAttack = PassiveAttack,
        GotoDelayMs = (int)GotoDelayMs,
        AlwaysGoto = AlwaysGoto,
        CopyWalk = CopyWalk,
        LeechMode = LeechMode,
        Park = Enum.TryParse(Park, out ButlerParkSpot park) ? park : ButlerParkSpot.House,
        RescueThreshold = (int)RescueThreshold,
        InstantWarnings = InstantWarnings,
        ClassType = Enum.TryParse(ClassType, out ButlerClassChoice c) ? c : ButlerClassChoice.None,
        DebugLog = DebugLogs,
    }.Normalized();

    private void LoadOptions()
    {
        _loading = true;
        try
        {
            ButlerOptions d = new();
            PluginState state = _store.Get(PluginId);
            bool Bool(string key, bool fallback) => state.Options.TryGetValue(key, out string? v) && bool.TryParse(v, out bool b) ? b : fallback;
            int Int(string key, int fallback) => state.Options.TryGetValue(key, out string? v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : fallback;
            string Choice<T>(string key, T fallback) where T : struct, Enum =>
                state.Options.TryGetValue(key, out string? v) && Enum.TryParse(v, out T t) ? t.ToString() : fallback.ToString();

            ButlerOptions o = new ButlerOptions
            {
                PassiveAttack = Bool(KeyPassiveAttack, d.PassiveAttack),
                GotoDelayMs = Int(KeyGotoDelay, d.GotoDelayMs),
                AlwaysGoto = Bool(KeyAlwaysGoto, d.AlwaysGoto),
                CopyWalk = Bool(KeyCopyWalk, d.CopyWalk),
                LeechMode = Bool(KeyLeechMode, d.LeechMode),
                RescueThreshold = Int(KeyRescue, d.RescueThreshold),
                InstantWarnings = Bool(KeyInstantWarnings, d.InstantWarnings),
                DebugLog = Bool(KeyDebugLogs, d.DebugLog),
            }.Normalized();
            PassiveAttack = o.PassiveAttack;
            GotoDelayMs = o.GotoDelayMs;
            AlwaysGoto = o.AlwaysGoto;
            CopyWalk = o.CopyWalk;
            LeechMode = o.LeechMode;
            RescueThreshold = o.RescueThreshold;
            InstantWarnings = o.InstantWarnings;
            DebugLogs = o.DebugLog;
            Park = Choice(KeyPark, d.Park);
            ClassType = Choice(KeyClassType, d.ClassType);

            RecentTargets.Clear();
            if (state.Options.TryGetValue(KeyTargets, out string? targets))
            {
                foreach (string t in targets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxTargets))
                    RecentTargets.Add(new ButlerNameRow(t, UseNameCommand));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveOptions()
    {
        if (_loading)
            return;
        ButlerOptions o = CurrentOptions();
        if (_backend is not null)
            _backend.Options = o;
        _store.SetOptions(PluginId, new Dictionary<string, string>
        {
            [KeyPassiveAttack] = o.PassiveAttack.ToString(),
            [KeyGotoDelay] = o.GotoDelayMs.ToString(CultureInfo.InvariantCulture),
            [KeyAlwaysGoto] = o.AlwaysGoto.ToString(),
            [KeyCopyWalk] = o.CopyWalk.ToString(),
            [KeyLeechMode] = o.LeechMode.ToString(),
            [KeyPark] = o.Park.ToString(),
            [KeyRescue] = o.RescueThreshold.ToString(CultureInfo.InvariantCulture),
            [KeyInstantWarnings] = o.InstantWarnings.ToString(),
            [KeyClassType] = o.ClassType.ToString(),
            [KeyDebugLogs] = o.DebugLog.ToString(),
        });
    }

    /// <summary>Remembers a player name at the top of the recent targets (ButlerV5's PlayerTargetHistory).</summary>
    private void Remember(string name)
    {
        for (int i = RecentTargets.Count - 1; i >= 0; i--)
        {
            if (RecentTargets[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                RecentTargets.RemoveAt(i);
        }
        RecentTargets.Insert(0, new ButlerNameRow(name, UseNameCommand));
        while (RecentTargets.Count > MaxTargets)
            RecentTargets.RemoveAt(RecentTargets.Count - 1);
        _store.SetOption(PluginId, KeyTargets, string.Join('\n', RecentTargets.Select(r => r.Name)));
    }

    private bool CanAct() => IsAvailable && IsOnline && PlayerName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Follow() => Run(b =>
    {
        b.StartFollow(PlayerName);
        return null;
    }, remember: true);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Goto() => Run(b =>
    {
        b.GotoPlayer(PlayerName);
        return $"Sent to {PlayerName.Trim()}.";
    }, remember: true);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Jump() => Run(b =>
    {
        b.JumpToPlayer(PlayerName);
        return $"Jumped to {PlayerName.Trim()}.";
    }, remember: false);

    [RelayCommand(CanExecute = nameof(IsFollowing))]
    private Task StopAsync() => StopCoreAsync(park: false);

    [RelayCommand(CanExecute = nameof(IsFollowing))]
    private Task StopAndParkAsync() => StopCoreAsync(park: true);

    private async Task StopCoreAsync(bool park)
    {
        if (_backend is null)
            return;
        Message = park ? "Stopping and parking..." : null;
        IButlerBackend backend = _backend;
        await Task.Run(() => backend.StopFollow(park));
        Message = null;
    }

    [RelayCommand]
    private void UseName(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            PlayerName = name;
    }

    [RelayCommand]
    private void RefreshPlayers()
    {
        RoomPlayers.Clear();
        foreach (string name in _backend?.PlayersInRoom() ?? [])
            RoomPlayers.Add(new ButlerNameRow(name, UseNameCommand));
    }

    [RelayCommand]
    private void ClearLog()
    {
        _backend?.ClearLog();
        RefreshLog();
    }

    [RelayCommand]
    private void ShareLog()
    {
        IReadOnlyList<ButlerLogLine> lines = _backend?.GetLog() ?? [];
        _host.Platform.ShareText("ButlerV5 log", string.Join('\n', lines.Select(l => l.Display)));
    }

    private void Run(Func<IButlerBackend, string?> action, bool remember)
    {
        if (_backend is null)
            return;
        string name = PlayerName.Trim();
        try
        {
            Message = action(_backend);
            if (remember)
                Remember(name);
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    internal void Apply(EngineSnapshot s)
    {
        IsOnline = s.IsOnline;
        if (!s.IsOnline)
            RoomPlayers.Clear();
    }

    private void OnStatusChanged(object? sender, ButlerStatus status)
    {
        if (Interlocked.Exchange(ref _statusPosted, 1) == 0)
        {
            _services.Dispatcher.Post(() =>
            {
                Volatile.Write(ref _statusPosted, 0);
                if (_backend is not null)
                    ApplyStatus(_backend.Status);
            });
        }
    }

    private void ApplyStatus(ButlerStatus status)
    {
        IsFollowing = status.IsFollowing;
        StatusText = status.Detail;
        StateText = status.Player is null ? "Idle" : $"{status.State} · {status.Player}";
    }

    private void OnLogChanged(object? sender, EventArgs e)
    {
        if (!IsActive || Interlocked.Exchange(ref _logPosted, 1) != 0)
            return;
        _services.Dispatcher.Post(() =>
        {
            Volatile.Write(ref _logPosted, 0);
            RefreshLog();
        });
    }

    private void RefreshLog()
    {
        IReadOnlyList<ButlerLogLine> lines = _backend?.GetLog() ?? [];
        // Newest first: the phone shows the latest without scrolling.
        LogLines = lines.Reverse().Take(ShownLogLines).Select(l => l.Display).ToList();
    }

    public void Dispose()
    {
        if (_backend is null)
            return;
        _backend.StatusChanged -= OnStatusChanged;
        _backend.LogChanged -= OnLogChanged;
    }
}
