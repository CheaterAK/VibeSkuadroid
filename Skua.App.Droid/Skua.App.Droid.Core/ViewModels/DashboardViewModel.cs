using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Memory;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>Character, connection, script and memory at a glance, plus quick actions.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly EngineHost _host;

    public DashboardViewModel(AppServices services)
    {
        _host = services.Host;
    }

    [ObservableProperty]
    private string _characterName = "-";

    [ObservableProperty]
    private string _levelAndClass = string.Empty;

    [ObservableProperty]
    private int _hp;

    [ObservableProperty]
    private int _hpMax = 1;

    [ObservableProperty]
    private int _mp;

    [ObservableProperty]
    private int _mpMax = 1;

    [ObservableProperty]
    private string _hpText = string.Empty;

    [ObservableProperty]
    private string _mpText = string.Empty;

    [ObservableProperty]
    private string _gold = "0";

    [ObservableProperty]
    private string _location = "-";

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    [ObservableProperty]
    private string _serverName = "-";

    [ObservableProperty]
    private string _stateFlags = string.Empty;

    [ObservableProperty]
    private string _scriptName = "No script loaded";

    [ObservableProperty]
    private string _scriptState = "Idle";

    [ObservableProperty]
    private string? _scriptMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopScriptCommand))]
    private bool _scriptBusy;

    [ObservableProperty]
    private string _memoryText = string.Empty;

    [ObservableProperty]
    private string _memoryDetail = string.Empty;

    [ObservableProperty]
    private MemoryVerdict _memoryVerdict;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinMapCommand), nameof(LogoutCommand))]
    private bool _isOnline;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinMapCommand))]
    private string _mapInput = string.Empty;

    [ObservableProperty]
    private string _cellInput = string.Empty;

    [ObservableProperty]
    private string? _actionMessage;

    [ObservableProperty]
    private bool _isActing;

    public bool MemoryHigh => MemoryVerdict is MemoryVerdict.RestartPending or MemoryVerdict.ThresholdTooLow;

    partial void OnMemoryVerdictChanged(MemoryVerdict value) => OnPropertyChanged(nameof(MemoryHigh));

    internal void Apply(EngineSnapshot s)
    {
        IsOnline = s.IsOnline;
        ConnectionStatus = s.StatusText ?? s.Connection.ToString();
        ServerName = s.ServerName ?? "-";
        if (s.Character is CharacterState c)
        {
            CharacterName = c.Name;
            LevelAndClass = string.IsNullOrEmpty(c.ClassName) ? $"Level {c.Level}" : $"Level {c.Level} · {c.ClassName}";
            HpMax = Math.Max(1, c.HpMax);
            Hp = Math.Clamp(c.Hp, 0, HpMax);
            MpMax = Math.Max(1, c.MpMax);
            Mp = Math.Clamp(c.Mp, 0, MpMax);
            HpText = $"{c.Hp} / {c.HpMax}";
            MpText = $"{c.Mp} / {c.MpMax}";
            Gold = Formatting.Number(c.Gold);
            Location = $"{c.Map} · {c.Cell} / {c.Pad}  ({c.PlayersInRoom} here)";
            StateFlags = string.Join("  ", new[] { c.Alive ? null : "dead", c.InCombat ? "in combat" : null, c.Afk ? "AFK" : null }.Where(x => x is not null));
        }
        else
        {
            CharacterName = s.AccountName ?? "-";
            LevelAndClass = string.Empty;
            HpText = MpText = string.Empty;
            Hp = Mp = 0;
            Location = "-";
            StateFlags = string.Empty;
        }

        ScriptBusy = s.Script.IsBusy;
        ScriptName = s.Script.ScriptName ?? "No script loaded";
        ScriptState = s.Script.State.ToString();
        ScriptMessage = s.Script.Message;
        MemoryText = Formatting.Memory(s.Memory);
        MemoryDetail = s.Memory.RssBytes < 0
            ? string.Empty
            : $"managed {s.Memory.ManagedMb:0} MB · peak {s.Memory.PeakRssBytes / 1048576.0:0} MB · restart above {_host.Settings.Current.MemoryThresholdMb} MB";
    }

    private bool CanJoin() => IsOnline && !string.IsNullOrWhiteSpace(MapInput);

    [RelayCommand(CanExecute = nameof(CanJoin))]
    private Task JoinMapAsync() => RunAsync(() =>
    {
        string[] cell = CellInput.Split(new[] { ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return _host.Engine.JoinMapAsync(MapInput.Trim(), cell.ElementAtOrDefault(0) ?? "Enter", cell.ElementAtOrDefault(1) ?? "Spawn");
    });

    [RelayCommand(CanExecute = nameof(ScriptBusy))]
    private Task StopScriptAsync() => RunAsync(() => _host.Engine.StopScriptAsync());

    [RelayCommand(CanExecute = nameof(IsOnline))]
    private Task LogoutAsync() => RunAsync(() => _host.LogoutAsync());

    [RelayCommand]
    private void RestartEngine()
    {
        if (!_host.Platform.CanRestartProcess)
            ActionMessage = "Restart is only available on Android.";
        else if (!_host.RequestRestart("restart requested from the dashboard"))
            ActionMessage = "Stop the script first.";
    }

    private async Task RunAsync(Func<Task> action)
    {
        ActionMessage = null;
        IsActing = true;
        try
        {
            await action();
        }
        catch (EngineException e)
        {
            ActionMessage = e.Message;
        }
        finally
        {
            IsActing = false;
        }
    }
}
