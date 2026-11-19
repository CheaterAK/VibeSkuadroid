using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Plugins;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>One tracked quest on the Daily Tracker page.</summary>
public sealed partial class DailyQuestRow : ObservableObject
{
    public DailyQuestRow(DailyQuestInfo info, IAsyncRelayCommand<string?> start)
    {
        Info = info;
        StartCommand = start;
    }

    /// <summary>Starts the script given as parameter (the page's command).</summary>
    public IAsyncRelayCommand<string?> StartCommand { get; }

    public DailyQuestInfo Info { get; }

    public int Id => Info.Id;

    public string Name => Info.Name;

    public string? Script => Info.Script;

    /// <summary>Done today; null before the first check (or when every check of it failed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowStart), nameof(IsCompleted), nameof(IsIncomplete))]
    private bool? _isDone;

    public bool IsCompleted => IsDone == true;

    public bool IsIncomplete => IsDone == false;

    public string StatusText => IsDone switch
    {
        true => "Completed",
        false => "Incomplete",
        null => "Unknown",
    };

    /// <summary>The row's own Start button (as on the desktop: a script, not done, not a Lord of Order step).</summary>
    public bool ShowStart => Script is { Length: > 0 } && IsDone == false && !Info.IsLordOfOrder;
}

/// <summary>One category on the Daily Tracker page.</summary>
public sealed class DailyQuestGroupRow
{
    public DailyQuestGroupRow(DailyQuestGroupInfo info, IAsyncRelayCommand<string?> start)
    {
        Info = info;
        StartCommand = start;
        Quests = info.Quests.Select(q => new DailyQuestRow(q, start)).ToList();
    }

    /// <summary>Starts the script given as parameter (the page's command).</summary>
    public IAsyncRelayCommand<string?> StartCommand { get; }

    public DailyQuestGroupInfo Info { get; }

    public string Name => Info.Name;

    public string? Script => Info.Script;

    public bool HasScript => Info.Script is { Length: > 0 };

    public IReadOnlyList<DailyQuestRow> Quests { get; }
}

/// <summary>
/// Daily &amp; Weekly Tracker (port of the desktop plugin): daily and weekly quest status and ultra boss lockouts of the
/// logged-in character, refreshed on demand, every five minutes while the page is open, after a quest turn-in and after
/// a script stops; each undone quest can start the community script that does it.
/// </summary>
public sealed partial class DailyTrackerViewModel : ObservableObject, IDisposable
{
    /// <summary>The desktop window's live update interval.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly AppServices _services;
    private readonly EngineHost _host;
    private readonly IDailyTrackerBackend? _backend;
    private readonly Dictionary<int, DailyQuestRow> _rows = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly TimeSpan _turnInDelay;
    private readonly TimeSpan _scriptStopDelay;
    private Timer? _timer;
    private bool _scriptWasBusy;
    private bool _loadedOnce;
    private bool _subscribed;
    private int _disposed;

    public DailyTrackerViewModel(AppServices services, TimeSpan? turnInDelay = null, TimeSpan? scriptStopDelay = null)
    {
        _services = services;
        _host = services.Host;
        _turnInDelay = turnInDelay ?? TimeSpan.FromMilliseconds(800);
        _scriptStopDelay = scriptStopDelay ?? TimeSpan.FromMilliseconds(500);
        _backend = _host.Engine.Plugins?.DailyTracker;
        Groups = _backend?.Catalog.Select(g => new DailyQuestGroupRow(g, StartScriptCommand)).ToList() ?? [];
        foreach (DailyQuestRow row in Groups.SelectMany(g => g.Quests))
            _rows[row.Id] = row;
        if (_backend is not null)
            _backend.QuestTurnedIn += OnQuestTurnedIn;
        _subscribed = _backend is not null;
        IsAvailable = _backend is not null;
        Message = IsAvailable ? null : "The Daily Tracker needs the Skua script host, which is not available in this engine.";
        Apply(_host.Engine.Snapshot);
    }

    public IReadOnlyList<DailyQuestGroupRow> Groups { get; }

    public bool IsAvailable { get; }

    /// <summary>
    /// The plugin is switched on (Plugins page). Off: no refresh at all, no turn-in listener (the desktop plugin
    /// unloaded).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(StartScriptCommand))]
    private bool _isEnabled = true;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_backend is null)
            return;
        if (value && !_subscribed)
            _backend.QuestTurnedIn += OnQuestTurnedIn;
        else if (!value && _subscribed)
            _backend.QuestTurnedIn -= OnQuestTurnedIn;
        _subscribed = value;
        if (!value)
            IsActive = false;
    }

    /// <summary>The page is shown: refreshes when opened and every five minutes while open.</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isOnline;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyPropertyChangedFor(nameof(RefreshText))]
    private bool _isRefreshing;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string _summary = "Not checked yet.";

    public string RefreshText => IsRefreshing ? "Checking..." : "Refresh";

    partial void OnIsActiveChanged(bool value)
    {
        if (value && IsEnabled)
        {
            if (IsOnline)
                _ = RefreshCoreAsync();
            _timer ??= new Timer(_ => _ = RefreshCoreAsync(), null, RefreshInterval, RefreshInterval);
        }
        else
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private bool CanRefresh() => IsAvailable && IsEnabled && IsOnline && !IsRefreshing;

    private bool CanStart(string? script) => IsAvailable && IsEnabled;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RefreshCoreAsync();

    /// <summary>
    /// Reads every state (batch-loading the quests first). A refresh while one runs is skipped, as on the desktop.
    /// Completes after the rows were updated (on the UI dispatcher).
    /// </summary>
    public async Task RefreshCoreAsync()
    {
        if (_backend is null || !IsEnabled || Volatile.Read(ref _disposed) != 0 || !_host.Engine.Snapshot.IsOnline)
            return;
        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            Post(() => IsRefreshing = true);
            IReadOnlyDictionary<int, bool?> states;
            try
            {
                states = await Task.Run(() => _backend.ReadStates()).ConfigureAwait(false);
            }
            catch (EngineException e)
            {
                Post(() => Message = e.Message);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Post(() => Message = $"Could not read the quest states: {e.Message}");
                return;
            }
            TaskCompletionSource applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try
                {
                    ApplyStates(states);
                }
                finally
                {
                    applied.TrySetResult();
                }
            });
            await applied.Task.ConfigureAwait(false);
        }
        finally
        {
            Post(() => IsRefreshing = false);
            _refreshGate.Release();
        }
    }

    private void ApplyStates(IReadOnlyDictionary<int, bool?> states)
    {
        foreach ((int id, bool? done) in states)
        {
            // A failed check keeps what was shown (desktop: the row keeps its state).
            if (done is bool d && _rows.TryGetValue(id, out DailyQuestRow? row))
                row.IsDone = d;
        }
        _loadedOnce = true;
        int completed = _rows.Values.Count(r => r.IsDone == true);
        int unknown = _rows.Values.Count(r => r.IsDone is null);
        Summary = $"{completed} of {_rows.Count} done · checked {DateTimeOffset.Now:HH:mm}" + (unknown > 0 ? $" · {unknown} unknown" : string.Empty);
        Message = null;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartScriptAsync(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return;
        string path = ScriptPath(script);
        if (!File.Exists(path))
        {
            Message = $"{script} is not in the script folder. Sync the community scripts (Scripts page) first.";
            return;
        }
        Message = null;
        try
        {
            await _host.Engine.StartScriptAsync(path);
            Message = $"Started {Path.GetFileName(path)}.";
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    /// <summary>The full path of a catalog script ('/' or '\' separated, relative to the script folder).</summary>
    public string ScriptPath(string script) =>
        Path.Combine(_services.Paths.ScriptsDir, script.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));

    internal void Apply(EngineSnapshot s)
    {
        bool wasOnline = IsOnline;
        IsOnline = s.IsOnline;
        if (!s.IsOnline && wasOnline)
        {
            // Another login may be another character: forget what was shown.
            foreach (DailyQuestRow row in _rows.Values)
                row.IsDone = null;
            _loadedOnce = false;
            Summary = "Not checked yet.";
        }
        else if (s.IsOnline && !wasOnline && IsActive)
        {
            _ = RefreshCoreAsync();
        }

        bool busy = s.Script.IsBusy;
        if (_scriptWasBusy && !busy && _loadedOnce)
            _ = RefreshLaterAsync(_scriptStopDelay);
        _scriptWasBusy = busy;
    }

    private void OnQuestTurnedIn(object? sender, int questId)
    {
        if (_rows.TryGetValue(questId, out DailyQuestRow? row))
            Post(() => row.IsDone = true);
        _ = RefreshLaterAsync(_turnInDelay);
    }

    private async Task RefreshLaterAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
            await RefreshCoreAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Background refresh: the next one tries again.
        }
    }

    private void Post(Action action) => _services.Dispatcher.Post(action);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _timer?.Dispose();
        _timer = null;
        if (_backend is not null && _subscribed)
            _backend.QuestTurnedIn -= OnQuestTurnedIn;
        _subscribed = false;
    }
}
