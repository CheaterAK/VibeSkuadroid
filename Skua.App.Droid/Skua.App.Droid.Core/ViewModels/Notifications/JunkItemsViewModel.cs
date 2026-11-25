using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Notifications;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>A row of the junk list: an inventory or bank item (or a junk entry no longer held) and its junk flag.</summary>
public sealed partial class JunkItemEntry : ObservableObject
{
    private readonly Action<JunkItemEntry> _changed;
    private readonly bool _loading = true;

    public JunkItemEntry(int id, string name, string category, int inventoryQuantity, int bankQuantity, bool equipped, bool isJunk, Action<JunkItemEntry> changed)
    {
        ID = id;
        Name = name;
        Category = category;
        InventoryQuantity = inventoryQuantity;
        BankQuantity = bankQuantity;
        IsEquipped = equipped;
        IsJunk = isJunk;
        _changed = changed;
        _loading = false;
    }

    public int ID { get; }

    public string Name { get; }

    public string Category { get; }

    public int InventoryQuantity { get; }

    public int BankQuantity { get; }

    public bool IsEquipped { get; }

    /// <summary>The desktop's "Inv: n | Bank: n".</summary>
    public string QuantityDisplay => $"Inv: {InventoryQuantity}  |  Bank: {BankQuantity}";

    [ObservableProperty]
    private bool _isJunk;

    partial void OnIsJunkChanged(bool value)
    {
        if (!_loading)
            _changed(this);
    }
}

/// <summary>
/// Junk Items (port of the desktop's JunkItemsViewModel): marks inventory and bank items as junk in Skua's junk list
/// (JunkItems.json in the script folder, the desktop's file and format), sells the junk in the inventory (the
/// desktop's "Sell all junk", equipped items never), and, on Android, rejects junk drops as they are offered.
/// </summary>
public sealed partial class JunkItemsViewModel : ObservableObject
{
    private readonly NotificationHub _hub;
    private readonly EngineHost _host;
    private readonly IJunkList? _junk;
    private List<JunkItemEntry> _all = new();
    private bool _loading;
    private bool _sellArmed;

    public JunkItemsViewModel(NotificationHub hub, EngineHost host)
    {
        _hub = hub;
        _host = host;
        _junk = hub.Game?.Junk;
        _loading = true;
        AutoRejectJunk = hub.Settings.Current.AutoRejectJunk;
        _loading = false;
    }

    /// <summary>Junk needs the Skua host (its junk service and drop handling).</summary>
    public bool IsAvailable => _junk is not null;

    [ObservableProperty]
    private IReadOnlyList<JunkItemEntry> _filteredItems = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _junkOnly;

    [ObservableProperty]
    private bool _autoRejectJunk;

    [ObservableProperty]
    private int _totalJunk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SellText))]
    private bool _isSelling;

    [ObservableProperty]
    private string? _message;

    /// <summary>The page is shown (set by the Tools hub).</summary>
    [ObservableProperty]
    private bool _isActive;

    public string SellText => IsSelling ? "Selling..." : _sellArmed ? "Tap again to sell" : "Sell all junk";

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
            Refresh();
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();

    partial void OnJunkOnlyChanged(bool value) => ApplySearch();

    partial void OnAutoRejectJunkChanged(bool value)
    {
        if (_loading)
            return;
        _hub.Settings.Update(s => s with { AutoRejectJunk = value });
        Message = value ? "Junk drops are rejected as soon as they are offered (not while the running script asks for them)." : null;
    }

    /// <summary>Re-reads the junk file and the inventory and bank (the bank list is empty until it was loaded once).</summary>
    [RelayCommand]
    public void Refresh()
    {
        if (_junk is null)
            return;
        _junk.Reload();
        IReadOnlyList<JunkItem> junk = _junk.Items;
        HashSet<int> junkIds = junk.Select(j => j.Id).ToHashSet();
        WorldLists lists = _host.Engine.GetWorldLists();
        // Per-item sums by lookup: scanning the bank once per item was quadratic (9 million steps for 3000 items).
        ILookup<int, InventoryRow> inInventory = lists.Inventory.ToLookup(i => i.ItemId);
        ILookup<int, InventoryRow> banked = lists.Bank.ToLookup(i => i.ItemId);
        List<JunkItemEntry> entries = lists.Inventory.Concat(lists.Bank)
            .GroupBy(i => i.ItemId)
            .Select(g =>
            {
                InventoryRow first = g.First();
                int inv = inInventory[g.Key].Sum(i => i.Quantity);
                int bank = banked[g.Key].Sum(i => i.Quantity);
                bool equipped = inInventory[g.Key].Any(i => i.Equipped);
                return new JunkItemEntry(g.Key, first.Name, first.Type, inv, bank, equipped, junkIds.Contains(g.Key), OnEntryChanged);
            })
            .ToList();
        // Junk entries not held right now stay listed, so they can be removed.
        HashSet<int> held = entries.Select(e => e.ID).ToHashSet();
        entries.AddRange(junk.Where(j => !held.Contains(j.Id)).Select(j => new JunkItemEntry(j.Id, j.Name, j.Category, 0, 0, false, true, OnEntryChanged)));
        _all = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        TotalJunk = _all.Count(e => e.IsJunk);
        ApplySearch();
    }

    [RelayCommand]
    private void UnmarkAllJunk()
    {
        if (_junk is null)
            return;
        _junk.Set([]);
        Refresh();
    }

    /// <summary>The desktop asks before selling; here the first tap arms the button and the second sells.</summary>
    [RelayCommand]
    private async Task SellAllJunkAsync()
    {
        if (_junk is null || IsSelling)
            return;
        if (!_host.Engine.Snapshot.IsOnline)
        {
            Message = "Log in first.";
            return;
        }
        if (TotalJunk == 0)
        {
            Message = "Nothing is marked as junk.";
            return;
        }
        if (!_sellArmed)
        {
            _sellArmed = true;
            OnPropertyChanged(nameof(SellText));
            Message = "This sells every junk item in the inventory (equipped items are kept). It cannot be undone. Tap again to sell.";
            return;
        }
        _sellArmed = false;
        IsSelling = true;
        try
        {
            int offered = await Task.Run(_junk.SellAll);
            Message = offered == 0 ? "No junk in the inventory." : $"Sold {offered} junk stack(s).";
        }
        catch (Exception e) when (e is EngineException or InvalidOperationException)
        {
            Message = e.Message;
        }
        finally
        {
            IsSelling = false;
        }
        Refresh();
    }

    private void OnEntryChanged(JunkItemEntry entry)
    {
        if (_junk is null)
            return;
        List<JunkItem> items = _all.Where(e => e.IsJunk).Select(e => new JunkItem(e.ID, e.Name, e.Category)).ToList();
        try
        {
            _junk.Set(items);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not save the junk list: {e.Message}";
        }
        TotalJunk = items.Count;
        if (JunkOnly)
            ApplySearch();
    }

    private void ApplySearch()
    {
        string filter = SearchText.Trim();
        FilteredItems = _all.Where(e =>
                (!JunkOnly || e.IsJunk)
                && (filter.Length == 0
                    || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || e.ID.ToString().Contains(filter, StringComparison.Ordinal)
                    || e.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
