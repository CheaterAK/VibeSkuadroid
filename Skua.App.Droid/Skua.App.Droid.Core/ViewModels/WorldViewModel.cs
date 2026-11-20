using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.World;

namespace Skua.App.Droid.Core.ViewModels;

public enum WorldTab
{
    Inventory,
    Temp,
    Bank,
    Quests,
    Drops,
    Monsters,
    Reputation,
}

/// <summary>A drop on the Drops tab, with whether it is on the drop watch list or the junk list.</summary>
public sealed record DropListItem(DropRow Row, bool Watched, bool Junk)
{
    public int ItemId => Row.ItemId;

    public string Name => Row.Name;

    public string QuantityText => $"x{Row.Quantity}";
}

/// <summary>
/// Lists from the world model, rebuilt only while the page is visible and the world changed. The Drops tab is also the
/// desktop's Current Drops window: pick up, reject, pick up all, and add a drop to the watch or junk list. The
/// Reputation tab lists every faction of Skua's <c>IScriptFaction.FactionList</c>, read on a worker thread at most
/// every <see cref="ReputationInterval"/> while it is shown (like the Control page), never in the background.
/// </summary>
public sealed partial class WorldViewModel : ObservableObject
{
    /// <summary>The shortest time between two reads of the factions (the Control page's refresh interval).</summary>
    public static readonly TimeSpan ReputationInterval = ControlViewModel.RefreshInterval;

    private readonly EngineHost _host;
    private readonly NotificationHub? _notifications;
    private readonly IUiDispatcher _dispatcher;
    private WorldLists _lists = WorldLists.Empty;
    private long _shownVersion = -1;
    private IReadOnlyList<FactionRow> _factions = [];
    private long _latestVersion;
    private long _repShownVersion = -1;
    private long _lastRepReadMs = long.MinValue / 2;
    private int _repState;
    private volatile bool _repAgain;
    private volatile bool _uiVisible = true;
    private volatile bool _repWanted;

    /// <summary>Faction reads done so far (tests).</summary>
    internal int ReputationReads;

    public WorldViewModel(AppServices services)
    {
        _host = services.Host;
        _notifications = services.Notifications;
        _dispatcher = services.Dispatcher;
    }

    /// <summary>Drops can be picked up and rejected (needs the Skua host).</summary>
    public bool CanHandleDrops => _host.Engine.GameEvents is not null;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInventoryTab), nameof(IsTempTab), nameof(IsBankTab), nameof(IsQuestsTab), nameof(IsDropsTab), nameof(IsMonstersTab), nameof(IsReputationTab))]
    private WorldTab _tab = WorldTab.Inventory;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<InventoryRow> _inventory = [];

    [ObservableProperty]
    private IReadOnlyList<InventoryRow> _temp = [];

    [ObservableProperty]
    private IReadOnlyList<InventoryRow> _bank = [];

    [ObservableProperty]
    private IReadOnlyList<QuestRow> _quests = [];

    [ObservableProperty]
    private IReadOnlyList<DropListItem> _drops = [];

    [ObservableProperty]
    private IReadOnlyList<MonsterRow> _monsters = [];

    /// <summary>The Reputation tab's rows (searched with <see cref="Filter"/>).</summary>
    [ObservableProperty]
    private IReadOnlyList<FactionRow> _factionRows = [];

    [ObservableProperty]
    private string _reputationSummary = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _bankLoaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadBankCommand), nameof(PickupAllDropsCommand), nameof(RejectAllDropsCommand), nameof(PickupDropCommand), nameof(RejectDropCommand))]
    private bool _isOnline;

    [ObservableProperty]
    private string? _message;

    public bool IsInventoryTab => Tab == WorldTab.Inventory;

    public bool IsTempTab => Tab == WorldTab.Temp;

    public bool IsBankTab => Tab == WorldTab.Bank;

    public bool IsQuestsTab => Tab == WorldTab.Quests;

    public bool IsDropsTab => Tab == WorldTab.Drops;

    public bool IsMonstersTab => Tab == WorldTab.Monsters;

    public bool IsReputationTab => Tab == WorldTab.Reputation;

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
            Reload(force: true);
        UpdateReputationWanted(force: true);
    }

    partial void OnTabChanged(WorldTab value) => UpdateReputationWanted(force: true);

    /// <summary>The UI shows (Android: activity started) or not; hidden, the Reputation tab reads nothing.</summary>
    public void SetUiVisible(bool visible)
    {
        _uiVisible = visible;
        UpdateReputationWanted(force: false);
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void SelectTab(string? tab)
    {
        if (Enum.TryParse(tab, ignoreCase: true, out WorldTab t))
            Tab = t;
    }

    [RelayCommand(CanExecute = nameof(IsOnline))]
    private async Task LoadBankAsync()
    {
        Message = null;
        try
        {
            await _host.Engine.LoadBankAsync();
            Reload(force: true);
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    private bool CanUseDrops() => IsOnline && CanHandleDrops;

    private bool CanUseDrop(DropListItem? drop) => drop is not null && CanUseDrops();

    [RelayCommand(CanExecute = nameof(CanUseDrop))]
    private void PickupDrop(DropListItem? drop)
    {
        if (drop is not null)
            _host.Engine.GameEvents?.PickupDrops([drop.ItemId]);
    }

    [RelayCommand(CanExecute = nameof(CanUseDrop))]
    private void RejectDrop(DropListItem? drop)
    {
        if (drop is not null)
            _host.Engine.GameEvents?.RejectDrops([drop.ItemId]);
    }

    [RelayCommand(CanExecute = nameof(CanUseDrops))]
    private void PickupAllDrops() => _host.Engine.GameEvents?.PickupAllDrops();

    [RelayCommand(CanExecute = nameof(CanUseDrops))]
    private void RejectAllDrops() => _host.Engine.GameEvents?.RejectDrops(_lists.Drops.Select(d => d.ItemId).ToList());

    /// <summary>Adds the drop to the drop watch list (or removes it).</summary>
    [RelayCommand]
    private void ToggleWatch(DropListItem? drop)
    {
        if (drop is null || _notifications is null)
            return;
        _notifications.Settings.Update(s => s with
        {
            WatchedItems = drop.Watched
                ? s.WatchedItems.Where(w => !string.Equals(w, drop.Name, StringComparison.OrdinalIgnoreCase)).ToList()
                : DropWatchList.Add(s.WatchedItems, drop.Name),
        });
        ApplyFilter();
    }

    /// <summary>Adds the drop to the junk list (or removes it).</summary>
    [RelayCommand]
    private void ToggleJunk(DropListItem? drop)
    {
        IJunkList? junk = _host.Engine.GameEvents?.Junk;
        if (drop is null || junk is null)
            return;
        List<JunkItem> items = junk.Items.Where(j => j.Id != drop.ItemId).ToList();
        if (!drop.Junk)
            items.Add(new JunkItem(drop.ItemId, drop.Name, string.Empty));
        try
        {
            junk.Set(items);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not save the junk list: {e.Message}";
        }
        ApplyFilter();
    }

    internal void Apply(EngineSnapshot s)
    {
        IsOnline = s.IsOnline;
        Volatile.Write(ref _latestVersion, s.WorldVersion);
        if (IsActive && s.WorldVersion != _shownVersion)
            Reload(force: false);
        if (_repWanted && s.WorldVersion != Volatile.Read(ref _repShownVersion))
            RequestReputation();
    }

    // ------------------------------------------------------------------ reputation (throttled, worker thread)

    private void UpdateReputationWanted(bool force)
    {
        _repWanted = IsActive && Tab == WorldTab.Reputation && _uiVisible;
        if (_repWanted && force)
        {
            Volatile.Write(ref _repShownVersion, -1);
            RequestReputation();
        }
    }

    private static long NowMs => Environment.TickCount64;

    private void RequestReputation()
    {
        if (!_repWanted)
            return;
        if (Interlocked.CompareExchange(ref _repState, 1, 0) != 0)
        {
            _repAgain = true;
            return;
        }
        long wait = Volatile.Read(ref _lastRepReadMs) + (long)ReputationInterval.TotalMilliseconds - NowMs;
        Task delay = wait > 0 ? Task.Delay(TimeSpan.FromMilliseconds(wait)) : Task.CompletedTask;
        delay.ContinueWith(_ => ReadReputation(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void ReadReputation()
    {
        if (!_repWanted)
        {
            Volatile.Write(ref _repState, 0);
            return;
        }
        _repAgain = false;
        long version = Volatile.Read(ref _latestVersion);
        Volatile.Write(ref _lastRepReadMs, NowMs);
        IReadOnlyList<FactionRow>? rows = null;
        string? error = null;
        try
        {
            IServiceProvider? skua = _host.Engine.SkuaServices;
            rows = skua is null ? null : _host.Engine.Snapshot.IsOnline ? ReputationList.Read(skua) : [];
        }
        catch (Exception e)
        {
            // A bad read must not stop the list: the next change reads again.
            error = $"Could not read the factions: {e.Message}";
        }
        Interlocked.Increment(ref ReputationReads);
        _dispatcher.Post(() =>
        {
            Volatile.Write(ref _repShownVersion, version);
            Volatile.Write(ref _repState, 0);
            ShowReputation(rows, error);
            if (_repAgain || Volatile.Read(ref _latestVersion) != version)
                RequestReputation();
        });
    }

    private void ShowReputation(IReadOnlyList<FactionRow>? rows, string? error)
    {
        if (error is not null)
        {
            ReputationSummary = error;
            return;
        }
        if (rows is null)
        {
            _factions = [];
            ReputationSummary = "Reputation needs the Skua host (not available in this build).";
        }
        else if (!_host.Engine.Snapshot.IsOnline)
        {
            _factions = [];
            ReputationSummary = "Log in to see your reputation.";
        }
        else
        {
            if (!_factions.SequenceEqual(rows))
                _factions = rows;
            int max = rows.Count(r => r.IsMax);
            ReputationSummary = $"{rows.Count} factions · {max} at rank 10";
        }
        ApplyReputationFilter();
    }

    private void ApplyReputationFilter()
    {
        IReadOnlyList<FactionRow> shown = ReputationList.Filter(_factions, Filter);
        if (!shown.SequenceEqual(FactionRows))
            FactionRows = shown;
    }

    private void Reload(bool force)
    {
        WorldLists lists = _host.Engine.GetWorldLists();
        if (!force && lists.WorldVersion == _shownVersion)
            return;
        _lists = lists;
        _shownVersion = lists.WorldVersion;
        BankLoaded = lists.BankLoaded;
        Summary = $"{lists.Inventory.Count} items · {lists.TempInventory.Count} temp · bank {(lists.BankLoaded ? lists.Bank.Count.ToString() : "not loaded")} · {lists.Quests.Count} quests · {lists.Drops.Count} drops";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string f = Filter.Trim();
        bool Match(string name) => f.Length == 0 || name.Contains(f, StringComparison.OrdinalIgnoreCase);
        Inventory = _lists.Inventory.Where(i => Match(i.Name)).OrderByDescending(i => i.Equipped).ThenBy(i => i.Name).ToList();
        Temp = _lists.TempInventory.Where(i => Match(i.Name)).OrderBy(i => i.Name).ToList();
        Bank = _lists.Bank.Where(i => Match(i.Name)).OrderBy(i => i.Name).ToList();
        Quests = _lists.Quests.Where(q => Match(q.Name) || q.QuestId.ToString() == f).ToList();
        IReadOnlyList<string> watched = _notifications?.Settings.Current.WatchedItems ?? [];
        IJunkList? junk = _host.Engine.GameEvents?.Junk;
        Drops = _lists.Drops.Where(d => Match(d.Name))
            .Select(d => new DropListItem(d, DropWatchList.Matches(watched, d.Name), junk?.IsJunk(d.ItemId) ?? false))
            .ToList();
        Monsters = _lists.Monsters.Where(m => Match(m.Name) || Match(m.Cell)).OrderBy(m => m.Cell).ThenBy(m => m.MonMapId).ToList();
        ApplyReputationFilter();
    }
}
