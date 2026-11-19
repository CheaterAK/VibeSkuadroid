using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Notifications;

namespace Skua.App.Droid.Core.ViewModels;

public enum WorldTab
{
    Inventory,
    Temp,
    Bank,
    Quests,
    Drops,
    Monsters,
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
/// desktop's Current Drops window: pick up, reject, pick up all, and add a drop to the watch or junk list.
/// </summary>
public sealed partial class WorldViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private readonly NotificationHub? _notifications;
    private WorldLists _lists = WorldLists.Empty;
    private long _shownVersion = -1;

    public WorldViewModel(AppServices services)
    {
        _host = services.Host;
        _notifications = services.Notifications;
    }

    /// <summary>Drops can be picked up and rejected (needs the Skua host).</summary>
    public bool CanHandleDrops => _host.Engine.GameEvents is not null;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInventoryTab), nameof(IsTempTab), nameof(IsBankTab), nameof(IsQuestsTab), nameof(IsDropsTab), nameof(IsMonstersTab))]
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

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
            Reload(force: true);
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
        if (IsActive && s.WorldVersion != _shownVersion)
            Reload(force: false);
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
    }
}
