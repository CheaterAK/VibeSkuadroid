using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Tools;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>A quest of the list, selectable, with its own Load and Fake complete.</summary>
public sealed partial class LoaderQuestRow : ObservableObject
{
    public LoaderQuestRow(QuestListEntry quest, LoaderViewModel page)
    {
        Quest = quest;
        LoadCommand = new AsyncRelayCommand(() => page.LoadQuestsAsync([quest]));
        FakeCompleteCommand = new AsyncRelayCommand(() => page.FakeCompleteAsync(quest));
        _page = page;
    }

    private readonly LoaderViewModel _page;

    public QuestListEntry Quest { get; }

    public string Title => $"[{Quest.Id}] {Quest.Name}";

    public string Detail => $"level {Quest.Level}{(Quest.Member ? " · member" : string.Empty)}{(Quest.Once ? " · once" : string.Empty)}";

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _page.SelectionChanged();

    public IAsyncRelayCommand LoadCommand { get; }

    public IAsyncRelayCommand FakeCompleteCommand { get; }
}

/// <summary>
/// Loader (port of the desktop Loader window): load a shop or quests by ID, and Skua's quest list (QuestData.json)
/// with search, select, copy names / IDs / "// name | id" lines, load, "fake complete" (this client only) and the
/// list's update from the game (new IDs, or a range). Loading changes nothing in the account, so it is one tap (after
/// "a script is running; do it anyway?" while a script runs); the list update, which sends a request every 1.5 s for
/// a while, asks first. One action at a time.
/// </summary>
public sealed partial class LoaderViewModel : ObservableObject
{
    /// <summary>Rows shown at once (the list has thousands of quests; the search narrows it).</summary>
    public const int MaxShown = 200;

    private readonly EngineHost _host;
    private readonly IFilePicker _clipboard;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<QuestListEntry> _all = new();
    private CancellationTokenSource? _updateCts;
    private bool _scriptBusy;
    private bool _listLoaded;

    public LoaderViewModel(AppServices services)
    {
        _host = services.Host;
        _clipboard = services.FilePicker;
        _dispatcher = services.Dispatcher;
        Apply(_host.Engine.Snapshot);
    }

    private ILoaderBackend? Backend => _host.Engine.Plugins?.GameTools?.Loader;

    public bool IsAvailable => Backend is not null;

    public ConfirmHost Confirm { get; } = new();

    /// <summary>A shop was loaded: the page offers to show it in the Grabber (raised with the kind loaded).</summary>
    public event EventHandler<LoaderKind>? ViewInGrabberRequested;

    public IReadOnlyList<string> Kinds { get; } = Enum.GetNames<LoaderKind>();

    [ObservableProperty]
    private string _selectedKind = nameof(LoaderKind.Shop);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private string _inputIds = string.Empty;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(UpdateCommand), nameof(UpdateRangeCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _status;

    /// <summary>What was loaded last (shown with "View in Grabber").</summary>
    [ObservableProperty]
    private LoaderKind? _lastLoaded;

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplySearch();

    public ObservableCollection<LoaderQuestRow> Quests { get; } = new();

    [ObservableProperty]
    private string _listText = string.Empty;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private string _rangeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdating))]
    private string? _updateProgress;

    public bool IsUpdating => _updateCts is not null;

    /// <summary>A script compiles, runs or stops (last snapshot, or the engine's own state now).</summary>
    public bool ScriptBusy => _scriptBusy || _host.Engine.Snapshot.Script.IsBusy;

    /// <summary>The page opened: reads the quest list (once; Reload reads it again).</summary>
    public void Refresh()
    {
        if (Backend is null)
        {
            Message = "The Loader needs the Skua script host, which is not available in this engine.";
            return;
        }
        if (!_listLoaded)
            _ = ReloadListAsync();
    }

    [RelayCommand]
    private async Task ReloadListAsync()
    {
        if (Backend is not ILoaderBackend backend)
            return;
        try
        {
            IReadOnlyList<QuestListEntry> list = await backend.GetQuestListAsync().ConfigureAwait(true);
            SetList(list);
        }
        catch (Exception e)
        {
            Message = $"Could not read the quest list: {e.Message}";
        }
    }

    private void SetList(IReadOnlyList<QuestListEntry> list)
    {
        _listLoaded = true;
        _all.Clear();
        _all.AddRange(list);
        ApplySearch();
    }

    private void ApplySearch()
    {
        Quests.Clear();
        ILoaderBackend? backend = Backend;
        int matches = 0;
        foreach (QuestListEntry q in _all)
        {
            if (backend is not null && !backend.Matches(q, SearchText))
                continue;
            matches++;
            if (Quests.Count < MaxShown)
                Quests.Add(new LoaderQuestRow(q, this));
        }
        ListText = _all.Count == 0
            ? "No quest list yet (Scripts > Sync community scripts brings QuestData.json, or Update below)."
            : matches > Quests.Count ? $"{Quests.Count} of {matches} matches shown (of {_all.Count}); search to narrow." : $"{matches} of {_all.Count} quests";
        SelectionChanged();
    }

    internal void SelectionChanged() => SelectedCount = Quests.Count(q => q.IsSelected);

    private List<QuestListEntry> Selected() => Quests.Where(q => q.IsSelected).Select(q => q.Quest).ToList();

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (LoaderQuestRow row in Quests.Where(q => q.IsSelected).ToList())
            row.IsSelected = false;
    }

    private bool CanLoad() => !IsBusy && Backend is ILoaderBackend b && b.IsIdList(InputIds);

    /// <summary>Loads the shop or the quests typed in.</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task LoadAsync()
    {
        LoaderKind kind = Enum.TryParse(SelectedKind, out LoaderKind k) ? k : LoaderKind.Shop;
        string input = InputIds;
        return RunAsync($"Load {kind.ToString().ToLowerInvariant()} {input.Trim()}", b => b.Load(kind, input), () => LastLoaded = kind);
    }

    [RelayCommand]
    private Task LoadSelectedAsync()
    {
        List<QuestListEntry> selected = Selected();
        if (selected.Count == 0)
        {
            Message = "Select quests first.";
            return Task.CompletedTask;
        }
        return LoadQuestsAsync(selected);
    }

    internal Task LoadQuestsAsync(IReadOnlyList<QuestListEntry> quests) =>
        RunAsync(quests.Count == 1 ? $"Load quest {quests[0].Id}" : $"Load {quests.Count} quests",
            b => b.LoadQuests(quests.Select(q => q.Id).ToList()), () => LastLoaded = LoaderKind.Quest);

    internal Task FakeCompleteAsync(QuestListEntry quest) =>
        RunAsync($"Fake complete {quest.Name} ({quest.Id})", b => b.FakeComplete(quest.Id));

    [RelayCommand]
    private void ViewInGrabber()
    {
        if (LastLoaded is LoaderKind kind)
            ViewInGrabberRequested?.Invoke(this, kind);
    }

    [RelayCommand]
    private Task CopyNamesAsync() => CopyAsync(QuestCopyFormat.Names);

    [RelayCommand]
    private Task CopyIdsAsync() => CopyAsync(QuestCopyFormat.Ids);

    [RelayCommand]
    private Task CopyNamesAndIdsAsync() => CopyAsync(QuestCopyFormat.NamesAndIds);

    private async Task CopyAsync(QuestCopyFormat format)
    {
        Message = null;
        List<QuestListEntry> selected = Selected();
        if (Backend is not ILoaderBackend backend)
            return;
        if (selected.Count == 0)
        {
            Message = "Select quests first.";
            return;
        }
        try
        {
            await _clipboard.CopyTextAsync(backend.CopyText(selected, format)).ConfigureAwait(true);
            Status = $"Copied {selected.Count} quest(s).";
        }
        catch (Exception e)
        {
            Message = $"Could not copy: {e.Message}";
        }
    }

    private bool CanUpdate() => !IsBusy;

    /// <summary>Skua's updater: quests newer than the list's highest ID (asks first: it runs for a while).</summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateAsync() =>
        UpdateWithAsync("Update the quest list", "Ask the game for quests newer than the list's highest ID and save them to Skua's QuestData.json? It sends one request every 1.5 s until no new quests come back.",
            (b, p, ct) => b.UpdateAsync(p, ct));

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateRangeAsync()
    {
        Message = null;
        if (Backend is not ILoaderBackend backend)
            return Task.CompletedTask;
        if (backend.ParseRange(RangeText, out int start, out int end) is string error)
        {
            Message = error;
            return Task.CompletedTask;
        }
        return UpdateWithAsync($"Update quests {start}-{end}", $"Ask the game for quests {start} to {end} and save them to Skua's QuestData.json? It sends one request per 29 quests, every 1.5 s.",
            (b, p, ct) => b.UpdateRangeAsync(start, end, p, ct));
    }

    [RelayCommand]
    private void CancelUpdate()
    {
        _updateCts?.Cancel();
        if (_updateCts is not null)
            UpdateProgress = "Cancelling...";
    }

    private async Task UpdateWithAsync(string what, string question, Func<ILoaderBackend, IProgress<string>, CancellationToken, Task<IReadOnlyList<QuestListEntry>>> update)
    {
        Message = null;
        if (IsBusy || Backend is not ILoaderBackend backend)
            return;
        IsBusy = true;
        try
        {
            if (!await Confirm.AllowWhileScriptRunsAsync(ScriptBusy, what).ConfigureAwait(true))
                return;
            if (!await Confirm.AskAsync(what, question, "Update").ConfigureAwait(true))
                return;
            using CancellationTokenSource cts = new();
            _updateCts = cts;
            UpdateProgress = "Starting...";
            Progress progress = new(this);
            try
            {
                IReadOnlyList<QuestListEntry> list = await Task.Run(() => update(backend, progress, cts.Token)).ConfigureAwait(true);
                SetList(list);
                Status = cts.IsCancellationRequested ? "Update cancelled; the list has what was saved." : $"Quest list updated: {list.Count} quests.";
                Log(Status);
            }
            catch (OperationCanceledException)
            {
                Status = "Update cancelled.";
            }
            catch (Exception e)
            {
                Message = $"{what} failed: {e.Message}";
                Log(Message, EngineLogLevel.Warning);
            }
            finally
            {
                _updateCts = null;
                UpdateProgress = null;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync(string what, Func<ILoaderBackend, string> action, Action? done = null)
    {
        Message = null;
        if (Backend is not ILoaderBackend backend)
        {
            Message = "The Loader needs the Skua script host, which is not available in this engine.";
            return;
        }
        if (IsBusy)
        {
            Message = "Wait: the last action is still running.";
            return;
        }
        IsBusy = true;
        try
        {
            if (!await Confirm.AllowWhileScriptRunsAsync(ScriptBusy, what).ConfigureAwait(true))
            {
                Status = $"Cancelled: {what}.";
                return;
            }
            Status = what + "...";
            string result = await Task.Run(() => action(backend)).ConfigureAwait(true);
            Status = result;
            Log(result);
            done?.Invoke();
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
            _host.Engine.WriteLog(level, "tools", text);
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

    /// <summary>Progress lines from the updater's thread, shown on the UI thread.</summary>
    private sealed class Progress : IProgress<string>
    {
        private readonly LoaderViewModel _page;

        public Progress(LoaderViewModel page) => _page = page;

        public void Report(string value) => _page._dispatcher.Post(() =>
        {
            if (_page._updateCts is not null)
                _page.UpdateProgress = value;
        });
    }
}
