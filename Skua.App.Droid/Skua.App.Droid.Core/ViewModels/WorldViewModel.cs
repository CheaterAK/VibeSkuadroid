using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;

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

/// <summary>Read-only lists from the world model. Rebuilt only while the page is visible and the world changed.</summary>
public sealed partial class WorldViewModel : ObservableObject
{
    private readonly EngineHost _host;
    private WorldLists _lists = WorldLists.Empty;
    private long _shownVersion = -1;

    public WorldViewModel(AppServices services)
    {
        _host = services.Host;
    }

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
    private IReadOnlyList<DropRow> _drops = [];

    [ObservableProperty]
    private IReadOnlyList<MonsterRow> _monsters = [];

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _bankLoaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadBankCommand))]
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
        Drops = _lists.Drops.Where(d => Match(d.Name)).ToList();
        Monsters = _lists.Monsters.Where(m => Match(m.Name) || Match(m.Cell)).OrderBy(m => m.Cell).ThenBy(m => m.MonMapId).ToList();
    }
}
