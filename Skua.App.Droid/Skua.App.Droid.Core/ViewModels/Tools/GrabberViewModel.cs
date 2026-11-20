using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Tools;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>A Grabber tab: the desktop's title, group and buttons.</summary>
public sealed record GrabberTab(GrabberCategory Category, string Title, string Group, IReadOnlyList<GrabberActionInfo> Actions)
{
    public string Display => $"{Group.ToLowerInvariant()} · {Title}";

    public override string ToString() => Display;
}

/// <summary>A button of a Grabber tab.</summary>
/// <param name="Action">The Grabber action the button runs.</param>
/// <param name="Title">The button text.</param>
/// <param name="Destructive">Shown in red (the desktop's soft red: Sell, Sell All).</param>
public sealed record GrabberActionInfo(GrabberAction Action, string Title, bool Destructive = false);

/// <summary>A tab's action button on the page.</summary>
public sealed class GrabberActionButton
{
    public GrabberActionButton(GrabberActionInfo info, GrabberViewModel page)
    {
        Info = info;
        Command = new AsyncRelayCommand(() => page.RunActionAsync(info));
    }

    public GrabberActionInfo Info { get; }

    public string Title => Info.Title;

    public bool Destructive => Info.Destructive;

    public IAsyncRelayCommand Command { get; }
}

/// <summary>A grabbed row: tap to select.</summary>
public sealed partial class GrabberRow : ObservableObject
{
    private readonly GrabberViewModel _page;

    public GrabberRow(GrabbedItem item, GrabberViewModel page)
    {
        Item = item;
        _page = page;
        CopyIdCommand = new AsyncRelayCommand(() => page.CopyAsync(item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Copied ID {item.Id}."));
    }

    public GrabbedItem Item { get; }

    public string Id => Item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The desktop's row text: the summary without its leading "[ID] ".</summary>
    public string Text
    {
        get
        {
            string summary = Item.Text;
            int idEnd = summary.StartsWith('[') ? summary.IndexOf("] ", StringComparison.Ordinal) : -1;
            return idEnd > 0 ? summary[(idEnd + 2)..] : summary;
        }
    }

    public bool IsJunk => Item.IsJunk;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _page.SelectionChanged();

    public IAsyncRelayCommand CopyIdCommand { get; }
}

/// <summary>
/// Grabber (port of the desktop Grabber window): grab shop items, loaded shops, quests, map items, inventory, house
/// items, temp items, bank and monsters from the game; search; select; copy IDs, names, the desktop's summary lines or
/// share every detail; and the desktop's item actions. Anything that spends or moves asks first with the app's own
/// confirmation (buy, sell, sell all, bank moves, equip, get map item, register), with a quantity where the desktop asks
/// for one; refusals are the desktop's (AC items, gold, one sale at a time, equipped items). Every action while a
/// script runs first asks "a script is running; do it anyway?". One action at a time; Cancel stops a running one.
/// </summary>
public sealed partial class GrabberViewModel : ObservableObject
{
    /// <summary>The desktop's tabs, in its order, with its buttons (Skua.App.Avalonia AppStartup/Grabber.cs).</summary>
    public static IReadOnlyList<GrabberTab> Catalog { get; } =
    [
        new(GrabberCategory.ShopItems, "Shop Items", "SHOPS", [new(GrabberAction.Buy, "Buy")]),
        new(GrabberCategory.ShopIds, "Shop IDs", "SHOPS", [new(GrabberAction.LoadShop, "Load Shop")]),
        new(GrabberCategory.Quests, "Quests", "QUESTS",
        [
            new(GrabberAction.OpenQuest, "Open"), new(GrabberAction.AcceptQuest, "Accept"), new(GrabberAction.RegisterQuest, "Register"),
            new(GrabberAction.FakeComplete, "Fake Complete"), new(GrabberAction.UnregisterAll, "Unregister All"),
        ]),
        new(GrabberCategory.MapItemIds, "GetMap Item IDs", "QUESTS",
            [new(GrabberAction.OpenQuest, "Open"), new(GrabberAction.AcceptQuest, "Accept"), new(GrabberAction.GetMapItem, "Get Map Item")]),
        new(GrabberCategory.Inventory, "Inventory", "ITEMS",
        [
            new(GrabberAction.Equip, "Equip"), new(GrabberAction.Sell, "Sell", true), new(GrabberAction.SellAll, "Sell All", true),
            new(GrabberAction.ToBank, "To Bank"),
        ]),
        new(GrabberCategory.HouseInventory, "House Inventory", "ITEMS", [new(GrabberAction.HouseToBank, "To Bank")]),
        new(GrabberCategory.TempInventory, "Temp Inventory", "ITEMS", []),
        new(GrabberCategory.Bank, "Bank Items", "ITEMS", [new(GrabberAction.ToInventory, "To Inventory")]),
        new(GrabberCategory.CellMonsters, "Cell Monsters", "MONSTERS", [new(GrabberAction.Kill, "Kill")]),
        new(GrabberCategory.MapMonsters, "Map Monsters", "MONSTERS", [new(GrabberAction.Kill, "Kill"), new(GrabberAction.TeleportTo, "Teleport To")]),
    ];

    /// <summary>Rows shown at once (search narrows).</summary>
    public const int MaxShown = 300;

    private readonly EngineHost _host;
    private readonly IFilePicker _clipboard;
    private readonly IPlatformServices _platform;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<GrabbedItem> _all = new();
    private CancellationTokenSource? _cts;
    private bool _scriptBusy;

    public GrabberViewModel(AppServices services)
    {
        _host = services.Host;
        _clipboard = services.FilePicker;
        _platform = services.Host.Platform;
        _dispatcher = services.Dispatcher;
        _selectedTab = Catalog[0];
        Actions = new(_selectedTab.Actions.Select(a => new GrabberActionButton(a, this)));
        Apply(_host.Engine.Snapshot);
    }

    private IGrabberBackend? Backend => _host.Engine.Plugins?.GameTools?.Grabber;

    public bool IsAvailable => Backend is not null;

    public ConfirmHost Confirm { get; } = new();

    public IReadOnlyList<GrabberTab> Tabs => Catalog;

    [ObservableProperty]
    private GrabberTab _selectedTab;

    partial void OnSelectedTabChanged(GrabberTab value)
    {
        Actions.Clear();
        foreach (GrabberActionInfo a in value?.Actions ?? [])
            Actions.Add(new GrabberActionButton(a, this));
        _all.Clear();
        ApplySearch();
        Grabbed = false;
        Message = null;
        Status = null;
    }

    public ObservableCollection<GrabberActionButton> Actions { get; }

    public ObservableCollection<GrabberRow> Rows { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplySearch();

    [ObservableProperty]
    private bool _grabbed;

    [ObservableProperty]
    private string _listText = "Tap Grab to read this list from the game.";

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isBusy;

    public bool CanCancel => IsBusy && _cts is not null;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _status;

    /// <summary>A script compiles, runs or stops (last snapshot, or the engine's own state now).</summary>
    public bool ScriptBusy => _scriptBusy || _host.Engine.Snapshot.Script.IsBusy;

    /// <summary>Selects a tab and grabs it (the Loader's "View in Grabber").</summary>
    public async Task ShowAsync(GrabberCategory category)
    {
        SelectedTab = Catalog.First(t => t.Category == category);
        await GrabAsync().ConfigureAwait(true);
    }

    public void Refresh()
    {
        if (Backend is null)
            Message = "The Grabber needs the Skua script host, which is not available in this engine.";
    }

    [RelayCommand]
    private async Task GrabAsync()
    {
        Message = null;
        if (Backend is not IGrabberBackend backend)
        {
            Refresh();
            return;
        }
        if (IsBusy)
            return;
        IsBusy = true;
        GrabberCategory category = SelectedTab.Category;
        try
        {
            IReadOnlyList<GrabbedItem> items = await Task.Run(() => backend.Grab(category)).ConfigureAwait(true);
            if (SelectedTab.Category != category)
                return;
            _all.Clear();
            _all.AddRange(items);
            Grabbed = true;
            ApplySearch();
            Status = null;
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
        catch (Exception e)
        {
            Message = $"Grab failed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySearch()
    {
        Rows.Clear();
        string q = SearchText.Trim();
        int matches = 0;
        foreach (GrabbedItem item in _all)
        {
            // The desktop searches the summary line.
            if (q.Length > 0 && !item.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            matches++;
            if (Rows.Count < MaxShown)
                Rows.Add(new GrabberRow(item, this));
        }
        ListText = !Grabbed ? "Tap Grab to read this list from the game."
            : _all.Count == 0 ? EmptyText(SelectedTab.Category)
            : matches > Rows.Count ? $"{Rows.Count} of {matches} shown (of {_all.Count}); search to narrow."
            : $"{matches} of {_all.Count}";
        SelectionChanged();
    }

    private static string EmptyText(GrabberCategory c) => c switch
    {
        GrabberCategory.MapItemIds => "No map items known for this map. Skua finds them by decompiling the map file, which only runs on Windows; here only map items the desktop saved before (Skua's cache) are listed.",
        GrabberCategory.ShopItems => "No shop loaded (Loader or Shop IDs: Load Shop).",
        GrabberCategory.Bank => "Nothing (the bank is empty, or not loaded yet).",
        _ => "Nothing to show.",
    };

    internal void SelectionChanged() => SelectedCount = Rows.Count(r => r.IsSelected);

    private List<GrabbedItem> Selected() => Rows.Where(r => r.IsSelected).Select(r => r.Item).ToList();

    [RelayCommand]
    private void SelectAll()
    {
        foreach (GrabberRow row in Rows)
            row.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (GrabberRow row in Rows.Where(r => r.IsSelected).ToList())
            row.IsSelected = false;
    }

    /// <summary>The selection, or every shown row when nothing is selected (the desktop's Copy and Export).</summary>
    private List<GrabbedItem> Targets()
    {
        List<GrabbedItem> selected = Selected();
        return selected.Count > 0 ? selected : Rows.Select(r => r.Item).ToList();
    }

    [RelayCommand]
    private Task CopyIdsAsync()
    {
        List<GrabbedItem> t = Targets();
        return t.Count == 0 ? NothingAsync() : CopyAsync(string.Join(",", t.Select(i => i.Id)), $"Copied {t.Count} ID(s).");
    }

    [RelayCommand]
    private Task CopyNamesAsync()
    {
        List<GrabbedItem> t = Targets();
        return t.Count == 0 ? NothingAsync() : CopyAsync(string.Join(",", t.Select(i => i.Name)), $"Copied {t.Count} name(s).");
    }

    /// <summary>The desktop's Copy: a title line and one summary line per item.</summary>
    [RelayCommand]
    private Task CopyLinesAsync()
    {
        List<GrabbedItem> t = Targets();
        if (t.Count == 0 || Backend is not IGrabberBackend backend)
            return NothingAsync();
        return CopyAsync(backend.CopyText(SelectedTab.Title, t), $"Copied {t.Count} item(s) to clipboard.");
    }

    /// <summary>The desktop's Export (every property), handed to the share sheet.</summary>
    [RelayCommand]
    private void ShareDetails()
    {
        List<GrabbedItem> t = Targets();
        if (t.Count == 0 || Backend is not IGrabberBackend backend)
        {
            Message = "No items to export.";
            return;
        }
        _platform.ShareText($"Grabber: {SelectedTab.Title}", backend.DetailedText(SelectedTab.Title, t));
        Status = $"Exported {t.Count} item(s).";
    }

    private Task NothingAsync()
    {
        Message = "No items to copy.";
        return Task.CompletedTask;
    }

    internal async Task CopyAsync(string text, string done)
    {
        Message = null;
        try
        {
            await _clipboard.CopyTextAsync(text).ConfigureAwait(true);
            Status = done;
        }
        catch (Exception e)
        {
            Message = $"Could not copy: {e.Message}";
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        if (_cts is not null)
            Status = "Cancelling...";
    }

    internal async Task RunActionAsync(GrabberActionInfo info)
    {
        Message = null;
        if (Backend is not IGrabberBackend backend)
        {
            Refresh();
            return;
        }
        if (IsBusy)
        {
            Message = "Wait: the last action is still running.";
            return;
        }
        List<GrabbedItem> items = Selected();
        IsBusy = true;
        string what = items.Count == 1 ? $"{info.Title}: {items[0].Name}" : $"{info.Title}: {items.Count} items";
        try
        {
            GrabberPlan plan = backend.Plan(info.Action, items);
            if (plan.Refusal is string refusal)
            {
                Message = refusal;
                return;
            }
            if (!await Confirm.AllowWhileScriptRunsAsync(ScriptBusy, what).ConfigureAwait(true))
            {
                Status = $"Cancelled: {what}.";
                return;
            }
            int quantity = 1;
            if (plan.Question is string question)
            {
                if (plan.Quantity is QuantityAsk q)
                {
                    int? answer = await Confirm.AskQuantityAsync(info.Title, question, info.Title, q.Label, q.Initial, q.Min, q.Max, danger: true).ConfigureAwait(true);
                    if (answer is null)
                    {
                        Status = "Cancelled.";
                        return;
                    }
                    quantity = answer.Value;
                }
                else if (!await Confirm.AskAsync(info.Title, question, info.Title, danger: true).ConfigureAwait(true))
                {
                    Status = "Cancelled.";
                    return;
                }
            }

            using CancellationTokenSource cts = new();
            _cts = cts;
            OnPropertyChanged(nameof(CanCancel));
            Status = what + "...";
            ProgressLine progress = new(this);
            try
            {
                string result = await Task.Run(() => backend.RunAsync(info.Action, items, quantity, progress, cts.Token)).ConfigureAwait(true);
                Status = result;
                Log(result);
            }
            catch (OperationCanceledException)
            {
                Status = "Task cancelled.";
            }
            finally
            {
                _cts = null;
                OnPropertyChanged(nameof(CanCancel));
            }
        }
        catch (EngineException e)
        {
            Status = null;
            Message = e.Message;
            Log($"{what}: {e.Message}", EngineLogLevel.Warning);
        }
        catch (Exception e)
        {
            Status = null;
            Message = $"{what} failed: {e.Message}";
            Log($"{what} failed: {e.GetType().Name}: {e.Message}", EngineLogLevel.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Log(string text, EngineLogLevel level = EngineLogLevel.Info)
    {
        try
        {
            _host.Engine.WriteLog(level, "grabber", text);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    internal void Apply(EngineSnapshot s)
    {
        IsOnline = s.IsOnline;
        _scriptBusy = s.Script.IsBusy;
    }

    private sealed class ProgressLine : IProgress<string>
    {
        private readonly GrabberViewModel _page;

        public ProgressLine(GrabberViewModel page) => _page = page;

        public void Report(string value) => _page._dispatcher.Post(() =>
        {
            if (_page.IsBusy)
                _page.Status = value;
        });
    }
}
