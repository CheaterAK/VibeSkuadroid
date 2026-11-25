using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Control;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The Control page: a schematic map of the current map (cells as tiles with monster and player counts; tap to move
/// there), the current cell (monsters with Attack, players with Go to), drops, skills, and manual actions (join a map,
/// go to a player, shop, quest, bank, rest, resurrect).
/// </summary>
/// <remarks>
/// <para>
/// Live but cheap: it reads the game only while the page is open and the app is in the foreground, at most once per
/// <see cref="RefreshInterval"/> (reads run on a worker thread; only the result is posted to the UI thread), and a
/// list is replaced only when its content changed. In the background it does nothing at all; the first refresh after
/// the app returns reads the latest state once.
/// </para>
/// <para>
/// Safety: one action at a time; while a script runs every action first asks "a script is running; do it anyway?";
/// every action that spends or moves something (buy, sell, bank moves, quest turn-in, reject a drop) asks for
/// confirmation with what it costs; the actions themselves wait for the game's own throttles (see
/// <see cref="IControlBackend"/>). No raw packets. Failures are shown on the page and logged to the Console; nothing
/// here throws into the UI.
/// </para>
/// </remarks>
public sealed partial class ControlViewModel : ObservableObject
{
    /// <summary>Least time between two refreshes of the page (at most this many UI updates per second: 2.5).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(400);

    public const int MaxRecentMaps = 8;

    private readonly AppServices _services;
    private readonly EngineHost _host;
    private readonly TimeProvider _time;
    private volatile bool _active;
    private volatile bool _uiVisible = true;
    private volatile ControlTab _tabMirror = ControlTab.Travel;
    private volatile int _questIdMirror;
    private int _refreshState;
    private volatile bool _refreshAgain;
    private long _latestWorldVersion;
    private long _shownWorldVersion = long.MinValue;
    private long _lastRefreshMs = long.MinValue / 2;
    private ControlState _state = ControlState.Offline;
    private Ask? _ask;
    private string? _inFlight;
    private object? _inFlightToken;
    private TaskCompletionSource<int?>? _answer;

    public ControlViewModel(AppServices services, TimeProvider? time = null, GameViewViewModel? gameView = null)
    {
        _services = services;
        GameView = gameView ?? new GameViewViewModel(services);
        _host = services.Host;
        _time = time ?? TimeProvider.System;
        IsAvailable = Backend is not null;
        RecentMaps = (_host.Settings.Current.RecentMaps ?? []).Select(m => new NameChip(m, false, JoinRecentCommand)).ToList();
        if (!IsAvailable)
            Message = "The Control page needs the Skua script host, which is not available in this engine.";
        Apply(_host.Engine.Snapshot);
    }

    private IControlBackend? Backend => _host.Engine.Plugins?.Control;

    /// <summary>Asks the shell to show the World lists (inventory, bank, quests, drops, monsters).</summary>
    public event EventHandler? OpenListsRequested;

    /// <summary>The last action started (tests wait for it).</summary>
    internal Task LastAction { get; private set; } = Task.CompletedTask;

    /// <summary>Refreshes the page has read so far (tests).</summary>
    internal int Refreshes;

    public bool IsAvailable { get; }

    /// <summary>The Game view card (shared with the other page).</summary>
    public GameViewViewModel GameView { get; }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private bool _scriptBusy;

    [ObservableProperty]
    private string _scriptText = string.Empty;

    [ObservableProperty]
    private string _mapTitle = "Not in a map";

    [ObservableProperty]
    private string _mapDetail = string.Empty;

    [ObservableProperty]
    private string _cellSourceText = string.Empty;

    [ObservableProperty]
    private string _currentCell = string.Empty;

    [ObservableProperty]
    private string _cellTitle = string.Empty;

    [ObservableProperty]
    private string _selfText = string.Empty;

    [ObservableProperty]
    private string? _targetText;

    [ObservableProperty]
    private bool _isDead;

    [ObservableProperty]
    private IReadOnlyList<CellTile> _tiles = [];

    [ObservableProperty]
    private IReadOnlyList<NameChip> _pads = [];

    [ObservableProperty]
    private IReadOnlyList<MonsterLine> _monsters = [];

    [ObservableProperty]
    private IReadOnlyList<PlayerLine> _players = [];

    [ObservableProperty]
    private IReadOnlyList<PlayerLine> _roomPlayers = [];

    [ObservableProperty]
    private IReadOnlyList<DropLine> _drops = [];

    [ObservableProperty]
    private IReadOnlyList<SkillButton> _skills = [];

    [ObservableProperty]
    private IReadOnlyList<NameChip> _recentMaps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTravelTab), nameof(IsShopTab), nameof(IsQuestTab), nameof(IsBankTab))]
    private ControlTab _tab = ControlTab.Travel;

    [ObservableProperty]
    private string _mapInput = string.Empty;

    [ObservableProperty]
    private string _playerInput = string.Empty;

    [ObservableProperty]
    private string _shopIdInput = string.Empty;

    [ObservableProperty]
    private string _questIdInput = string.Empty;

    [ObservableProperty]
    private string _shopTitle = "No shop loaded.";

    [ObservableProperty]
    private IReadOnlyList<ShopLine> _shopItems = [];

    [ObservableProperty]
    private bool _shopLoaded;

    [ObservableProperty]
    private ControlQuest? _quest;

    [ObservableProperty]
    private string _questTitle = "No quest loaded.";

    [ObservableProperty]
    private bool _bankLoaded;

    [ObservableProperty]
    private string _bankSummary = "The bank is not loaded.";

    /// <summary>
    /// The most bank lines the Bank tab shows at once. The tab is a plain list inside the page's scroll view (no
    /// virtualization), and a 3000-item bank built 3000 rows with buttons on the UI thread; search finds the rest.
    /// </summary>
    public const int MaxBankLines = 100;

    [ObservableProperty]
    private IReadOnlyList<ItemLine> _bankItems = [];

    /// <summary>Search text for the Bank tab (item name, case-insensitive).</summary>
    [ObservableProperty]
    private string _bankFilter = string.Empty;

    /// <summary>"Showing 100 of 2950 ..." when the search matches more than <see cref="MaxBankLines"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBankShownText))]
    private string _bankShownText = string.Empty;

    public bool HasBankShownText => BankShownText.Length > 0;

    private IReadOnlyList<InventoryRow> _bankSource = [];

    partial void OnBankFilterChanged(string value) => ShowBankLines();

    private void ShowBankLines()
    {
        string f = BankFilter.Trim();
        List<ItemLine> lines = new(Math.Min(MaxBankLines, _bankSource.Count));
        int matched = 0;
        foreach (InventoryRow row in _bankSource)
        {
            if (f.Length > 0 && !row.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
                continue;
            if (++matched <= MaxBankLines)
                lines.Add(new ItemLine(row, "To backpack", ToInventoryCommand, false, SellCommand));
        }
        BankShownText = matched > MaxBankLines ? $"Showing {MaxBankLines} of {matched}; search to find the others." : string.Empty;
        if (!lines.SequenceEqual(BankItems))
            BankItems = lines;
    }

    [ObservableProperty]
    private IReadOnlyList<ItemLine> _inventoryItems = [];

    /// <summary>An error or refusal from the last action (shown inline, red).</summary>
    [ObservableProperty]
    private string? _message;

    /// <summary>What the last action did.</summary>
    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private string _workingText = string.Empty;

    // Confirmation overlay.
    [ObservableProperty]
    private bool _isConfirming;

    [ObservableProperty]
    private string _confirmTitle = string.Empty;

    [ObservableProperty]
    private string _confirmText = string.Empty;

    [ObservableProperty]
    private string _confirmButton = "OK";

    [ObservableProperty]
    private bool _confirmDanger;

    [ObservableProperty]
    private bool _confirmHasQuantity;

    [ObservableProperty]
    private decimal? _confirmQuantity = 1;

    [ObservableProperty]
    private decimal _confirmQuantityMax = 1;

    public bool IsTravelTab => Tab == ControlTab.Travel;

    public bool IsShopTab => Tab == ControlTab.Shop;

    public bool IsQuestTab => Tab == ControlTab.Quest;

    public bool IsBankTab => Tab == ControlTab.Bank;

    public bool HasQuest => Quest is not null;

    public bool CanAcceptQuest => Quest is { Accepted: false };

    public bool CanTurnInQuest => Quest is { Accepted: true };

    public IReadOnlyList<string> QuestRequirements => Quest?.Requirements is { Count: > 0 } r ? r : ["None"];

    public IReadOnlyList<string> QuestProgress => Quest?.Progress is { Count: > 0 } p ? p.Select(x => x.Text + (x.Done ? "  ✓" : string.Empty)).ToList() : ["No items to turn in"];

    public IReadOnlyList<string> QuestRewards => Quest?.Rewards is { Count: > 0 } r ? r : ["None listed"];

    public string QuestStatus => Quest is null ? string.Empty : Quest.StatusText + (Quest.AcceptRefusal is string why && !Quest.Accepted ? $" · {why}" : string.Empty);

    public bool NoMonsters => Monsters.Count == 0;

    public bool NoPlayers => Players.Count == 0;

    public bool HasDrops => Drops.Count > 0;

    public bool HasRoomPlayers => RoomPlayers.Count > 0;

    public bool HasRecentMaps => RecentMaps.Count > 0;

    public bool HasTarget => TargetText is not null;

    partial void OnMonstersChanged(IReadOnlyList<MonsterLine> value) => OnPropertyChanged(nameof(NoMonsters));

    partial void OnPlayersChanged(IReadOnlyList<PlayerLine> value) => OnPropertyChanged(nameof(NoPlayers));

    partial void OnDropsChanged(IReadOnlyList<DropLine> value) => OnPropertyChanged(nameof(HasDrops));

    partial void OnRoomPlayersChanged(IReadOnlyList<PlayerLine> value) => OnPropertyChanged(nameof(HasRoomPlayers));

    partial void OnRecentMapsChanged(IReadOnlyList<NameChip> value) => OnPropertyChanged(nameof(HasRecentMaps));

    partial void OnTargetTextChanged(string? value) => OnPropertyChanged(nameof(HasTarget));

    partial void OnQuestChanged(ControlQuest? value)
    {
        QuestTitle = value is null ? "No quest loaded." : $"{value.Name} (#{value.QuestId})";
        OnPropertyChanged(nameof(HasQuest));
        OnPropertyChanged(nameof(CanAcceptQuest));
        OnPropertyChanged(nameof(CanTurnInQuest));
        OnPropertyChanged(nameof(QuestRequirements));
        OnPropertyChanged(nameof(QuestProgress));
        OnPropertyChanged(nameof(QuestRewards));
        OnPropertyChanged(nameof(QuestStatus));
    }

    partial void OnIsActiveChanged(bool value)
    {
        _active = value;
        if (value)
            RequestRefresh();
    }

    partial void OnTabChanged(ControlTab value)
    {
        _tabMirror = value;
        RequestRefresh();
    }

    partial void OnConfirmQuantityChanged(decimal? value)
    {
        if (_ask?.TextFor is Func<int, string> text && value is decimal q)
            ConfirmText = text((int)Math.Clamp(q, 1, Math.Max(1, ConfirmQuantityMax)));
    }

    /// <summary>The platform reports whether the UI is showing; while hidden the page reads nothing.</summary>
    public void SetUiVisible(bool visible)
    {
        _uiVisible = visible;
        if (visible)
            RequestRefresh();
    }

    /// <summary>System back: closes the confirmation (as Cancel). False when nothing was open.</summary>
    public bool CloseOverlay()
    {
        if (!IsConfirming)
            return false;
        CancelConfirm();
        return true;
    }

    internal void Apply(EngineSnapshot s)
    {
        IsOnline = s.IsOnline;
        ScriptBusy = s.Script.IsBusy;
        ScriptText = s.Script.IsBusy ? $"Script running: {s.Script.ScriptName}. Actions here ask first." : string.Empty;
        MapDetail = s.ServerName is string server && s.IsOnline ? server : string.Empty;
        Volatile.Write(ref _latestWorldVersion, s.WorldVersion);
        if (s.WorldVersion != Volatile.Read(ref _shownWorldVersion))
            RequestRefresh();
    }

    // ------------------------------------------------------------------ refresh (throttled, foreground only)

    /// <summary>Schedules a read of the game: at once if the last one is old enough, else when it is.</summary>
    public void RequestRefresh()
    {
        if (!_active || !_uiVisible || Backend is null)
            return;
        if (Interlocked.CompareExchange(ref _refreshState, 1, 0) != 0)
        {
            _refreshAgain = true;
            return;
        }
        long wait = Volatile.Read(ref _lastRefreshMs) + (long)RefreshInterval.TotalMilliseconds - NowMs;
        Task delay = wait > 0 ? Task.Delay(TimeSpan.FromMilliseconds(wait), _time) : Task.CompletedTask;
        delay.ContinueWith(_ => ReadInBackground(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private void ReadInBackground()
    {
        try
        {
            IControlBackend? backend = Backend;
            if (!_active || !_uiVisible || backend is null)
            {
                Volatile.Write(ref _refreshState, 0);
                return;
            }
            _refreshAgain = false;
            long version = Volatile.Read(ref _latestWorldVersion);
            Reading reading = Collect(backend, _tabMirror, _questIdMirror);
            Interlocked.Increment(ref Refreshes);
            _services.Dispatcher.Post(() =>
            {
                Volatile.Write(ref _lastRefreshMs, NowMs);
                Volatile.Write(ref _shownWorldVersion, version);
                Volatile.Write(ref _refreshState, 0);
                try
                {
                    if (_active && _uiVisible)
                        Show(reading);
                }
                catch (Exception e)
                {
                    // A bad read must not take the UI thread down; the next refresh tries again.
                    Message = $"Showing the game failed: {e.Message}";
                    Log(EngineLogLevel.Error, $"Control page refresh failed: {e.GetType().Name}: {e.Message}");
                }
                if (_refreshAgain || Volatile.Read(ref _latestWorldVersion) != version)
                    RequestRefresh();
            });
        }
        catch (Exception e)
        {
            Volatile.Write(ref _refreshState, 0);
            Log(EngineLogLevel.Error, $"Control page refresh failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Reads now (tests, and after an action): same path as the throttled refresh, without the wait.</summary>
    internal async Task RefreshNowAsync()
    {
        if (Backend is not IControlBackend backend)
            return;
        ControlTab tab = Tab;
        int questId = _questIdMirror;
        Reading reading = await Task.Run(() => Collect(backend, tab, questId)).ConfigureAwait(true);
        Show(reading);
    }

    private sealed record Reading(ControlState State, ControlShop? Shop, bool ShopRead, ControlQuest? Quest, bool QuestRead, ControlBank? Bank, string? Error);

    private static Reading Collect(IControlBackend backend, ControlTab tab, int questId)
    {
        try
        {
            ControlState state = backend.Read();
            ControlShop? shop = tab == ControlTab.Shop ? backend.ReadShop() : null;
            ControlQuest? quest = tab == ControlTab.Quest && questId > 0 ? backend.ReadQuest(questId) : null;
            ControlBank? bank = tab == ControlTab.Bank ? backend.ReadBank() : null;
            return new Reading(state, shop, tab == ControlTab.Shop, quest, tab == ControlTab.Quest && questId > 0, bank, null);
        }
        catch (Exception e)
        {
            return new Reading(ControlState.Offline, null, false, null, false, null, $"Reading the game failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private void Show(Reading r)
    {
        if (r.Error is not null)
        {
            Message = r.Error;
            return;
        }
        ControlState s = r.State;
        ControlState old = _state;
        _state = s;

        MapTitle = s.Online ? (string.IsNullOrEmpty(s.Area) ? s.Map : s.Area) : "Not in a map";
        CellSourceText = s.Source switch
        {
            CellSource.MapFile => $"{s.Cells.Count} cells from the map file",
            CellSource.RoomData => "Map file not readable: cells seen in this room only",
            _ => string.Empty,
        };
        CurrentCell = s.Cell;
        CellTitle = s.Online ? $"{s.Cell} · {s.Pad}" : string.Empty;
        IsDead = s.Online && !s.Alive;
        SelfText = s.Online ? $"HP {s.Hp:N0}/{s.HpMax:N0}{(s.Alive ? s.InCombat ? " · in combat" : string.Empty : " · dead")}" : string.Empty;
        TargetText = s.Target;

        if (!s.Cells.SequenceEqual(old.Cells))
            Tiles = s.Cells.Select(c => new CellTile(c.Name, CountsOf(c), c.IsCurrent, c.MonstersAlive > 0, JumpToCellCommand)).ToList();
        if (!s.Pads.SequenceEqual(old.Pads) || s.Pad != old.Pad)
            Pads = s.Pads.Select(p => new NameChip(p, p == s.Pad, UsePadCommand)).ToList();
        if (!s.Monsters.SequenceEqual(old.Monsters))
            Monsters = s.Monsters.Select(m => new MonsterLine(m, AttackCommand)).ToList();
        if (!s.Players.SequenceEqual(old.Players))
            Players = s.Players.Select(p => new PlayerLine(p, GotoCommand)).ToList();
        if (!s.RoomPlayers.SequenceEqual(old.RoomPlayers))
            RoomPlayers = s.RoomPlayers.Select(p => new PlayerLine(p, GotoCommand)).ToList();
        if (!s.Drops.SequenceEqual(old.Drops))
            Drops = s.Drops.Select(d => new DropLine(d, PickupCommand, RejectCommand)).ToList();
        if (!s.Skills.SequenceEqual(old.Skills))
            Skills = s.Skills.Select(k => new SkillButton(k, UseSkillCommand)).ToList();

        bool sellable = s.ShopLoaded;
        if (r.ShopRead)
        {
            ShopLoaded = r.Shop is not null;
            ShopTitle = r.Shop is null ? "No shop loaded." : $"{r.Shop.Name} (#{r.Shop.ShopId})";
            List<ShopLine> lines = r.Shop?.Items.Select(i => new ShopLine(i, BuyCommand)).ToList() ?? [];
            if (!lines.Select(l => l.Item).SequenceEqual(ShopItems.Select(l => l.Item)))
                ShopItems = lines;
        }
        if (r.QuestRead && r.Quest != Quest)
            Quest = r.Quest;
        if (r.Bank is ControlBank bank)
        {
            BankLoaded = bank.Loaded;
            BankSummary = bank.Loaded ? $"Bank {bank.Bank.Count}/{bank.BankSlots} · backpack {bank.Inventory.Count}/{bank.BagSlots}" : "The bank is not loaded.";
            // The backend hands back the same list while the bank is unchanged: nothing to rebuild then.
            if (!ReferenceEquals(bank.Bank, _bankSource))
            {
                _bankSource = bank.Bank;
                ShowBankLines();
            }
            List<ItemLine> held = bank.Inventory.Select(i => new ItemLine(i, "To bank", ToBankCommand, sellable && !i.Equipped, SellCommand)).ToList();
            if (!held.SequenceEqual(InventoryItems))
                InventoryItems = held;
        }
    }

    private static string CountsOf(ControlCell c)
    {
        string monsters = c.MonstersTotal > 0 ? $"{c.MonstersAlive}/{c.MonstersTotal} mon" : string.Empty;
        string players = c.Players > 0 ? $"{c.Players} pl" : string.Empty;
        string text = string.Join(" · ", new[] { monsters, players }.Where(x => x.Length > 0));
        return c.IsCurrent ? (text.Length > 0 ? $"you · {text}" : "you") : text.Length > 0 ? text : "empty";
    }

    // ------------------------------------------------------------------ the action pipeline

    /// <summary>A question for the confirmation overlay; with <see cref="QuantityMax"/> above 0 it asks for a quantity.</summary>
    private sealed record Ask(string Title, string Text, string Button, bool Danger = false, int QuantityMax = 0, int QuantityStart = 1, Func<int, string>? TextFor = null);

    private void Start(string what, Func<IControlBackend, int, string> action, Ask? ask = null, Action<string>? done = null)
    {
        LastAction = RunAsync(what, action, ask, done);
    }

    private async Task RunAsync(string what, Func<IControlBackend, int, string> action, Ask? ask, Action<string>? done)
    {
        object token = new();
        try
        {
            Message = null;
            if (Backend is not IControlBackend backend)
            {
                Message = "The Control page needs the Skua script host, which is not available in this engine.";
                return;
            }
            // One action at a time, from the first question to the end (a second tap is refused, not queued).
            if (_inFlight is string running)
            {
                if (!IsConfirming)
                    Message = $"Wait: \"{running}\" is still running.";
                return;
            }
            _inFlight = what;
            _inFlightToken = token;
            if (ScriptBusy || _host.Engine.Snapshot.Script.IsBusy)
            {
                int? anyway = await AskAsync(new Ask("A script is running", $"A script is running; do it anyway?\n\n{what}", "Do it anyway", Danger: true)).ConfigureAwait(true);
                if (anyway is null)
                {
                    Status = $"Cancelled: {what}.";
                    return;
                }
            }
            int quantity = 1;
            if (ask is not null)
            {
                int? answer = await AskAsync(ask).ConfigureAwait(true);
                if (answer is null)
                {
                    Status = $"Cancelled: {what}.";
                    return;
                }
                quantity = answer.Value;
            }

            IsWorking = true;
            WorkingText = what;
            Status = null;
            try
            {
                string result = await Task.Run(() => action(backend, quantity)).ConfigureAwait(true);
                Status = result;
                Log(EngineLogLevel.Info, result);
                done?.Invoke(result);
            }
            catch (EngineException e)
            {
                Message = e.Message;
                Log(EngineLogLevel.Warning, $"{what}: {e.Message}");
            }
            catch (Exception e)
            {
                Message = $"{what} failed: {e.Message}";
                Log(EngineLogLevel.Error, $"{what} failed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                IsWorking = false;
                WorkingText = string.Empty;
            }
            await RefreshNowAsync().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            // Nothing on this page may take the app down.
            Message = $"{what} failed: {e.Message}";
            Log(EngineLogLevel.Error, $"{what} failed: {e.GetType().Name}: {e.Message}");
            IsWorking = false;
        }
        finally
        {
            if (ReferenceEquals(_inFlightToken, token))
            {
                _inFlight = null;
                _inFlightToken = null;
            }
        }
    }

    private Task<int?> AskAsync(Ask ask)
    {
        _answer?.TrySetResult(null);
        TaskCompletionSource<int?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _answer = answer;
        _ask = ask;
        ConfirmTitle = ask.Title;
        ConfirmButton = ask.Button;
        ConfirmDanger = ask.Danger;
        ConfirmHasQuantity = ask.QuantityMax > 0;
        ConfirmQuantityMax = Math.Max(1, ask.QuantityMax);
        ConfirmQuantity = Math.Clamp(ask.QuantityStart, 1, Math.Max(1, ask.QuantityMax));
        ConfirmText = ask.TextFor is Func<int, string> text ? text((int)ConfirmQuantity.Value) : ask.Text;
        IsConfirming = true;
        return answer.Task;
    }

    [RelayCommand]
    private void Confirm()
    {
        TaskCompletionSource<int?>? answer = _answer;
        int quantity = ConfirmHasQuantity ? (int)Math.Clamp(ConfirmQuantity ?? 1, 1, ConfirmQuantityMax) : 1;
        _answer = null;
        _ask = null;
        IsConfirming = false;
        answer?.TrySetResult(quantity);
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        TaskCompletionSource<int?>? answer = _answer;
        _answer = null;
        _ask = null;
        IsConfirming = false;
        answer?.TrySetResult(null);
    }

    private void Log(EngineLogLevel level, string text)
    {
        try
        {
            _host.Engine.WriteLog(level, "control", text);
        }
        catch (Exception)
        {
            // The log is best effort.
        }
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void SelectTab(string? tab)
    {
        if (Enum.TryParse(tab, ignoreCase: true, out ControlTab t))
            Tab = t;
    }

    [RelayCommand]
    private void OpenLists() => OpenListsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void JumpToCell(string? cell)
    {
        if (string.IsNullOrEmpty(cell))
            return;
        Start($"Move to {cell}", (b, _) => b.Jump(cell, null));
    }

    [RelayCommand]
    private void UsePad(string? pad)
    {
        if (string.IsNullOrEmpty(pad) || string.IsNullOrEmpty(CurrentCell))
            return;
        string cell = CurrentCell;
        Start($"Move to {cell} ({pad})", (b, _) => b.Jump(cell, pad));
    }

    [RelayCommand]
    private void Attack(int monMapId)
    {
        string name = Monsters.FirstOrDefault(m => m.MonMapId == monMapId)?.Name ?? $"monster #{monMapId}";
        Start($"Attack {name}", (b, _) => b.Attack(monMapId));
    }

    [RelayCommand]
    private void Goto(string? player)
    {
        if (string.IsNullOrWhiteSpace(player))
            return;
        Start($"Go to {player}", (b, _) => b.GotoPlayer(player));
    }

    [RelayCommand]
    private void GotoPlayer()
    {
        string name = PlayerInput.Trim();
        if (name.Length == 0)
        {
            Message = "Enter a player name.";
            return;
        }
        Goto(name);
    }

    [RelayCommand]
    private void UseSkill(int index) => Start($"Skill {index}", (b, _) => b.UseSkill(index));

    [RelayCommand]
    private void CancelTarget() => Start("Cancel target", (b, _) => b.CancelTarget());

    [RelayCommand]
    private void Rest() => Start("Rest", (b, _) => b.Rest());

    [RelayCommand]
    private void Resurrect() => Start("Resurrect", (b, _) => b.Resurrect());

    [RelayCommand]
    private void Pickup(int itemId)
    {
        string name = Drops.FirstOrDefault(d => d.ItemId == itemId)?.Name ?? $"item #{itemId}";
        Start($"Pick up {name}", (b, _) => b.PickupDrop(itemId));
    }

    [RelayCommand]
    private void Reject(int itemId)
    {
        string name = Drops.FirstOrDefault(d => d.ItemId == itemId)?.Name ?? $"item #{itemId}";
        Start($"Reject {name}", (b, _) => b.RejectDrop(itemId),
            new Ask("Reject drop", $"Reject {name}? The drop is gone for good.", "Reject", Danger: true));
    }

    [RelayCommand]
    private void JoinMap()
    {
        string map = MapInput.Trim();
        if (map.Length == 0)
        {
            Message = "Enter a map name.";
            return;
        }
        Join(map);
    }

    [RelayCommand]
    private void JoinRecent(string? map)
    {
        if (!string.IsNullOrWhiteSpace(map))
        {
            MapInput = map;
            Join(map);
        }
    }

    private void Join(string map) => Start($"Join {map}", (b, _) => b.JoinMap(map), done: _ => RememberMap(map));

    private void RememberMap(string map)
    {
        try
        {
            List<string> recent = new[] { map }
                .Concat((_host.Settings.Current.RecentMaps ?? []).Where(m => !m.Equals(map, StringComparison.OrdinalIgnoreCase)))
                .Take(MaxRecentMaps)
                .ToList();
            _host.Settings.Update(s => s with { RecentMaps = recent });
            RecentMaps = recent.Select(m => new NameChip(m, false, JoinRecentCommand)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(EngineLogLevel.Warning, $"Could not save the recent maps: {e.Message}");
        }
    }

    [RelayCommand]
    private void LoadShop()
    {
        if (!int.TryParse(ShopIdInput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0)
        {
            Message = "Enter a shop ID (a number).";
            return;
        }
        Start($"Load shop {id}", (b, _) => b.LoadShop(id));
    }

    [RelayCommand]
    private void Buy(ControlShopItem? item)
    {
        if (item is null)
            return;
        if (!item.CanBuy)
        {
            Message = $"{item.Name}: {item.RequirementText}";
            return;
        }
        bool stack = item.MaxStack > 1 && item.MaxBuys > 1;
        int start = Math.Min(item.Bundle, item.MaxBuys);
        string For(int q) => $"Buy {q} x {item.Name} for {item.PriceOf(q):N0} {item.Currency}?" + (item.Coins ? "\n\nThis spends AC." : string.Empty);
        Start($"Buy {item.Name}",
            (b, q) => b.Buy(item.ItemId, item.ShopItemId, !stack || q == item.Bundle ? -1 : q),
            new Ask("Buy", For(start), "Buy", Danger: item.Coins, QuantityMax: stack ? item.MaxBuys : 0, QuantityStart: start, TextFor: For));
    }

    [RelayCommand]
    private void Sell(int itemId)
    {
        InventoryRow? row = InventoryItems.FirstOrDefault(i => i.ItemId == itemId)?.Row;
        if (row is null)
            return;
        string For(int q) => $"Sell {q} x {row.Name}? A sale cannot be undone.";
        Start($"Sell {row.Name}",
            (b, q) => b.Sell(itemId, q),
            new Ask("Sell", For(row.Quantity), "Sell", Danger: true, QuantityMax: row.Quantity > 1 ? row.Quantity : 0, QuantityStart: row.Quantity, TextFor: For));
    }

    [RelayCommand]
    private void LoadQuest()
    {
        if (!int.TryParse(QuestIdInput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0)
        {
            Message = "Enter a quest ID (a number).";
            return;
        }
        _questIdMirror = id;
        Start($"Load quest {id}", (b, _) => b.LoadQuest(id));
    }

    [RelayCommand]
    private void AcceptQuest()
    {
        if (Quest is not ControlQuest q)
            return;
        Start($"Accept {q.Name}", (b, _) => b.AcceptQuest(q.QuestId));
    }

    [RelayCommand]
    private void TurnInQuest()
    {
        if (Quest is not ControlQuest q)
            return;
        string uses = q.Progress.Count > 0 ? string.Join(", ", q.Progress.Select(p => $"{p.Need} x {p.Name}")) : "no items";
        string gets = q.Rewards.Count > 0 ? string.Join(", ", q.Rewards) : "nothing listed";
        Start($"Turn in {q.Name}", (b, _) => b.TurnInQuest(q.QuestId),
            new Ask("Turn in quest", $"Turn in {q.Name}?\n\nUses: {uses}\nRewards: {gets}", "Turn in"));
    }

    [RelayCommand]
    private void LoadBank() => Start("Load the bank", (b, _) => b.LoadBank());

    [RelayCommand]
    private void ToInventory(int itemId)
    {
        string name = BankItems.FirstOrDefault(i => i.ItemId == itemId)?.Name ?? $"item #{itemId}";
        Start($"Move {name} to the backpack", (b, _) => b.BankToInventory(itemId),
            new Ask("Bank", $"Move {name} from the bank to the backpack?", "Move"));
    }

    [RelayCommand]
    private void ToBank(int itemId)
    {
        string name = InventoryItems.FirstOrDefault(i => i.ItemId == itemId)?.Name ?? $"item #{itemId}";
        Start($"Move {name} to the bank", (b, _) => b.InventoryToBank(itemId),
            new Ask("Bank", $"Move {name} from the backpack to the bank?", "Move"));
    }
}
