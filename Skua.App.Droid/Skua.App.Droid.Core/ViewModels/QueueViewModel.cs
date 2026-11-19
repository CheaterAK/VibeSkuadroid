using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Scheduler;
using Skua.App.Droid.Core.Scripts;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The Scripts page's Queue tab: the script queue's status (current entry, progress, elapsed, next), its entries,
/// their editor and playlists. The queue itself lives in <see cref="EngineHost.Scheduler"/>; this only shows it, so it
/// keeps running with the app in the background and a recreated activity shows the same state.
/// </summary>
public sealed partial class QueueViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly EngineHost _host;
    private readonly ScriptScheduler _queue;
    private readonly Action<ScriptOptionsViewModel> _openOptions;
    private QueueStatus? _pending;
    private int _posted;
    private volatile bool _uiVisible = true;

    /// <param name="services">The app's services.</param>
    /// <param name="queue">The engine's script queue.</param>
    /// <param name="openOptions">Opens the Scripts page's options editor (an entry's script or CoreBots profile).</param>
    public QueueViewModel(AppServices services, ScriptScheduler queue, Action<ScriptOptionsViewModel> openOptions)
    {
        _services = services;
        _host = services.Host;
        _queue = queue;
        _openOptions = openOptions;
        _queue.StatusChanged += OnStatusChanged;
        RefreshPlaylists();
        Apply(_queue.Status);
    }

    public ObservableCollection<QueueItemViewModel> Items { get; } = new();

    public ObservableCollection<string> Playlists { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(EditCommand), nameof(RemoveCommand))]
    private QueueItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(ContinueCommand), nameof(StopCommand), nameof(MoveUpCommand), nameof(MoveDownCommand),
        nameof(EditCommand), nameof(RemoveCommand), nameof(ClearCommand), nameof(LoadPlaylistCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>"Gold farm" while running, else null.</summary>
    [ObservableProperty]
    private string? _currentName;

    /// <summary>"Entry 2 of 5 · run 1/3".</summary>
    [ObservableProperty]
    private string _progressText = string.Empty;

    /// <summary>"Entry 00:12:30 · queue 01:02:03".</summary>
    [ObservableProperty]
    private string? _elapsedText;

    [ObservableProperty]
    private string? _nextText;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string _tabTitle = "Queue";

    [ObservableProperty]
    private bool _loop;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePlaylistCommand))]
    private string _playlistName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadPlaylistCommand), nameof(DeletePlaylistCommand))]
    private string? _selectedPlaylist;

    [ObservableProperty]
    private string? _message;

    /// <summary>The entry editor while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsListing))]
    private QueueEntryEditorViewModel? _editor;

    public bool IsEditing => Editor is not null;

    public bool IsListing => Editor is null;

    private bool _applyingLoop;

    partial void OnLoopChanged(bool value)
    {
        if (!_applyingLoop && _queue.Loop != value)
            _queue.Loop = value;
    }

    /// <summary>While the UI is hidden, status changes are not applied (the latest is applied when it shows again).</summary>
    public void SetUiVisible(bool visible)
    {
        _uiVisible = visible;
        if (visible)
            Post();
    }

    private void OnStatusChanged(object? sender, QueueStatus status)
    {
        Volatile.Write(ref _pending, status);
        Post();
    }

    private void Post()
    {
        if (_uiVisible && Interlocked.Exchange(ref _posted, 1) == 0)
        {
            _services.Dispatcher.Post(() =>
            {
                Volatile.Write(ref _posted, 0);
                if (Volatile.Read(ref _pending) is QueueStatus latest)
                    Apply(latest);
            });
        }
    }

    /// <summary>Elapsed times tick with the engine's snapshots (called by the Scripts page).</summary>
    internal void Tick()
    {
        if (IsRunning)
            Apply(_queue.Status);
    }

    internal void Apply(QueueStatus s)
    {
        IsRunning = s.IsRunning;
        StatusText = s.Text;
        Count = s.Count;
        TabTitle = s.Count == 0 ? "Queue" : $"Queue ({s.Count})";
        CurrentName = s.IsRunning ? s.CurrentName : null;
        string run = s.Runs == 1 ? string.Empty : s.Runs == 0 ? $" · run {s.Run}" : $" · run {s.Run}/{s.Runs}";
        ProgressText = s.Count == 0
            ? "Add scripts from the Library tab."
            : s.IsRunning ? $"Entry {Math.Min(s.Index + 1, s.Count)} of {s.Count}{run}" : $"{s.Count} {(s.Count == 1 ? "entry" : "entries")}{(s.Loop ? " · loops" : string.Empty)}";
        TimeSpan queueTime = s.StartedAt is DateTimeOffset started ? DateTimeOffset.UtcNow - started : TimeSpan.Zero;
        ElapsedText = s.IsRunning
            ? $"Entry {QueueText.Clock(s.EntryElapsed)}{(s.StartedAt is null ? string.Empty : $" · queue {QueueText.Clock(queueTime < TimeSpan.Zero ? TimeSpan.Zero : queueTime)}")}"
            : null;
        NextText = s.IsRunning && s.NextName is string next ? $"Next: {next}" : null;
        _applyingLoop = true;
        Loop = s.Loop;
        _applyingLoop = false;
        if (s.PlaylistName is string name && PlaylistName.Length == 0)
            PlaylistName = name;

        // Rebuild when the entries changed, else update the rows in place (keeps the selection).
        bool same = Items.Count == s.Entries.Count && Items.Zip(s.Entries).All(p => p.First.Id == p.Second.Entry.Id);
        if (!same)
        {
            Guid? selected = SelectedItem?.Id;
            Items.Clear();
            foreach (QueueEntryView v in s.Entries)
                Items.Add(new QueueItemViewModel(v));
            SelectedItem = Items.FirstOrDefault(i => i.Id == selected);
        }
        else
        {
            for (int i = 0; i < Items.Count; i++)
                Items[i].Apply(s.Entries[i]);
        }
        StartCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => !IsRunning && Count > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => Run(_queue.Start);

    private bool CanContinue() => !IsRunning && Count > 0 && _queue.Status.Index > 0 && _queue.Status.Index < Count;

    /// <summary>Runs from the entry the queue stopped at (after a stop, a failure or an interruption).</summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Continue() => Run(_queue.Continue);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        Message = null;
        try
        {
            await _queue.StopAsync();
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    private bool CanEditSelected() => !IsRunning && SelectedItem is not null;

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void MoveUp() => Run(() => _queue.Move(SelectedItem!.Id, -1));

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void MoveDown() => Run(() => _queue.Move(SelectedItem!.Id, +1));

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void Remove() => Run(() => _queue.Remove(SelectedItem!.Id));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Clear() => Run(_queue.Clear);

    /// <summary>Adds a library script at the end of the queue.</summary>
    public void Add(ScriptEntry entry)
    {
        Run(() =>
        {
            QueueEntry added = _queue.Add(entry.FullPath);
            Message = $"{added.Name} added to the queue.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void Edit()
    {
        QueueEntry? entry = _queue.Entries.FirstOrDefault(e => e.Id == SelectedItem!.Id);
        if (entry is not null)
            Editor = new QueueEntryEditorViewModel(entry);
    }

    [RelayCommand]
    private void SaveEntry()
    {
        if (Editor is not QueueEntryEditorViewModel editor)
            return;
        if (!editor.TryBuild(out QueueEntry? entry))
            return;
        try
        {
            _queue.Update(entry!);
            Editor = null;
            Message = $"{entry!.Name} saved.";
        }
        catch (EngineException e)
        {
            editor.Error = e.Message;
        }
    }

    [RelayCommand]
    private void CloseEditor() => Editor = null;

    /// <summary>System back closes the entry editor first.</summary>
    public bool CloseOverlay()
    {
        if (Editor is null)
            return false;
        Editor = null;
        return true;
    }

    /// <summary>The entry's script options (its own option storage, the desktop's "edit options" of a queue item).</summary>
    [RelayCommand]
    private async Task EditScriptOptionsAsync()
    {
        if (Editor is not QueueEntryEditorViewModel editor)
            return;
        editor.Error = null;
        editor.IsWorking = true;
        try
        {
            QueueEntry entry = editor.Entry;
            ScriptOptionSet set = await _host.Engine.GetScriptOptionsAsync(_queue.FullPath(entry), entry.OptionStorage);
            if (set.Items.Count == 0)
            {
                editor.Error = $"{entry.Name} has no script options.";
                return;
            }
            _openOptions(new ScriptOptionsViewModel(set, entry.Name, profileOf: entry.Name));
        }
        catch (EngineException e)
        {
            editor.Error = e.Message;
        }
        finally
        {
            editor.IsWorking = false;
        }
    }

    private string? CurrentAccount => _host.Engine.Snapshot.Character?.Name ?? _host.Engine.Snapshot.AccountName ?? _host.Settings.Current.AccountName;

    /// <summary>The entry's CoreBots profile (CoreBots' settings applied right before each run of the entry).</summary>
    [RelayCommand]
    private void EditCoreBotsOptions()
    {
        if (Editor is not QueueEntryEditorViewModel editor)
            return;
        editor.Error = null;
        try
        {
            if (!CoreBotsOptions.Available(_services.Library.Root))
            {
                editor.Error = "CoreBots.cs is not in the script folder (sync the community scripts first).";
                return;
            }
            if (CurrentAccount is not string account)
            {
                editor.Error = "Log in first: CoreBots settings belong to an account.";
                return;
            }
            ScriptOptionSet set = _queue.LoadCoreBotsProfile(editor.Entry, account);
            if (set.Items.Count == 0)
            {
                editor.Error = "CoreBots.cs declares no settings.";
                return;
            }
            _openOptions(new ScriptOptionsViewModel(set, "CoreBots", isCoreBots: true, profileOf: editor.Entry.Name, queueEntry: editor.Entry.Id));
        }
        catch (Exception e) when (e is EngineException or IOException or UnauthorizedAccessException)
        {
            editor.Error = e.Message;
        }
    }

    [RelayCommand]
    private void ClearCoreBotsProfile()
    {
        if (Editor is not QueueEntryEditorViewModel editor)
            return;
        try
        {
            _queue.SetCoreBotsProfile(editor.Entry.Id, null);
            editor.ReloadProfile(_queue.Entries.First(e => e.Id == editor.Entry.Id));
        }
        catch (EngineException e)
        {
            editor.Error = e.Message;
        }
    }

    /// <summary>Saves an entry's CoreBots profile from the options editor (not the CBO file).</summary>
    internal void SaveCoreBotsProfile(Guid entry, ScriptOptionSet set)
    {
        _queue.SetCoreBotsProfile(entry, set);
        if (Editor is QueueEntryEditorViewModel editor && editor.Entry.Id == entry)
            editor.ReloadProfile(_queue.Entries.First(e => e.Id == entry));
    }

    // ------------------------------------------------------------------ playlists

    private bool CanSavePlaylist() => QueueStore.IsValidName(PlaylistName);

    [RelayCommand(CanExecute = nameof(CanSavePlaylist))]
    private void SavePlaylist()
    {
        Run(() =>
        {
            _queue.SavePlaylist(PlaylistName.Trim());
            RefreshPlaylists();
            SelectedPlaylist = PlaylistName.Trim();
            Message = $"Playlist \"{PlaylistName.Trim()}\" saved.";
        });
    }

    private bool CanLoadPlaylist() => !IsRunning && SelectedPlaylist is not null;

    [RelayCommand(CanExecute = nameof(CanLoadPlaylist))]
    private void LoadPlaylist()
    {
        Run(() =>
        {
            string name = SelectedPlaylist!;
            _queue.LoadPlaylist(name);
            PlaylistName = name;
            Message = _queue.Status.Text;
        });
    }

    private bool CanDeletePlaylist() => SelectedPlaylist is not null;

    [RelayCommand(CanExecute = nameof(CanDeletePlaylist))]
    private void DeletePlaylist()
    {
        Run(() =>
        {
            string name = SelectedPlaylist!;
            _queue.DeletePlaylist(name);
            RefreshPlaylists();
            Message = $"Playlist \"{name}\" deleted.";
        });
    }

    public void RefreshPlaylists()
    {
        string? selected = SelectedPlaylist;
        Playlists.Clear();
        try
        {
            foreach (string p in _queue.ListPlaylists())
                Playlists.Add(p);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = e.Message;
        }
        SelectedPlaylist = Playlists.Contains(selected ?? string.Empty) ? selected : null;
    }

    private void Run(Action action)
    {
        Message = null;
        try
        {
            action();
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = e.Message;
        }
    }

    public void Dispose() => _queue.StatusChanged -= OnStatusChanged;
}

/// <summary>A row of the queue list.</summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    public QueueItemViewModel(QueueEntryView view)
    {
        Id = view.Entry.Id;
        Apply(view);
    }

    public Guid Id { get; }

    [ObservableProperty]
    private string _position = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _script = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblem), nameof(IsDone), nameof(IsActive))]
    private string _status = "Queued";

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isCurrent;

    public bool IsProblem => Status is "Failed" or "Crashed" or "File Not Found" or "Disconnected" or "Interrupted";

    public bool IsDone => Status == "Completed";

    public bool IsActive => Status is "Running" or "Paused" or "Retrying";

    internal void Apply(QueueEntryView v)
    {
        Position = (v.Index + 1).ToString(CultureInfo.InvariantCulture) + ".";
        Name = v.Entry.Name;
        Script = v.Entry.Script;
        Summary = v.Entry.Summary;
        Status = v.Result.Status;
        IsCurrent = v.IsCurrent;
        Error = v.Result.Error;
        List<string> parts = new();
        if (v.Result.RunsDone > 0)
            parts.Add($"{v.Result.RunsDone} run{(v.Result.RunsDone == 1 ? string.Empty : "s")} done");
        if (v.Result.Elapsed > TimeSpan.Zero)
            parts.Add(QueueText.Clock(v.Result.Elapsed));
        Detail = parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

/// <summary>Edits one queue entry: display name, runs, stop-after, delay and error rule; opens its option profiles.</summary>
public sealed partial class QueueEntryEditorViewModel : ObservableObject
{
    public static IReadOnlyList<string> ErrorChoices { get; } = ["Skip to the next entry", "Stop the queue", "Retry"];

    public QueueEntryEditorViewModel(QueueEntry entry)
    {
        Entry = entry;
        _name = entry.Name;
        _repeat = entry.Runs == 0;
        _runs = (entry.Runs == 0 ? 1 : entry.Runs).ToString(CultureInfo.InvariantCulture);
        _stopAfterMinutes = entry.StopAfter is TimeSpan t ? Math.Round(t.TotalMinutes, 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
        _delaySeconds = Math.Round(entry.DelayAfter.TotalSeconds, 1).ToString(CultureInfo.InvariantCulture);
        _onError = ErrorChoices[(int)entry.OnError];
        _retries = entry.MaxRetries.ToString(CultureInfo.InvariantCulture);
        ReloadProfile(entry);
    }

    public QueueEntry Entry { get; private set; }

    public string Header => $"Queue entry: {Entry.Name}";

    public string ScriptText => $"Script: {Entry.Script} · options profile {Entry.OptionStorage}";

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _runs;

    [ObservableProperty]
    private bool _repeat;

    [ObservableProperty]
    private string _stopAfterMinutes;

    [ObservableProperty]
    private string _delaySeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRetry))]
    private string _onError;

    [ObservableProperty]
    private string _retries;

    [ObservableProperty]
    private string _coreBotsText = string.Empty;

    [ObservableProperty]
    private bool _hasCoreBotsProfile;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isWorking;

    public bool IsRetry => OnError == ErrorChoices[2];

    internal void ReloadProfile(QueueEntry entry)
    {
        Entry = Entry with { CoreBots = entry.CoreBots };
        HasCoreBotsProfile = entry.CoreBots is not null;
        CoreBotsText = entry.CoreBots is { } p
            ? $"CoreBots profile: {p.Count} settings, written to the account's CoreBots file before each run."
            : "No CoreBots profile: CoreBots uses the account's saved settings.";
    }

    /// <summary>The edited entry, or false with <see cref="Error"/> set.</summary>
    public bool TryBuild(out QueueEntry? entry)
    {
        entry = null;
        List<string> problems = new();
        int runs = 0;
        if (!Repeat && (!int.TryParse(Runs.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out runs) || runs < 1 || runs > 10000))
            problems.Add("Runs: a whole number from 1 to 10000");
        TimeSpan? stopAfter = null;
        if (StopAfterMinutes.Trim().Length > 0)
        {
            if (double.TryParse(StopAfterMinutes.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0 && m <= 7 * 24 * 60)
                stopAfter = TimeSpan.FromMinutes(m);
            else
                problems.Add("Stop after: minutes (more than 0), or empty for no limit");
        }
        if (!double.TryParse(DelaySeconds.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double delay) || delay < 0 || delay > 86400)
            problems.Add("Delay: seconds from 0 to 86400");
        int index = Math.Max(0, ErrorChoices.ToList().IndexOf(OnError));
        int retries = Entry.MaxRetries;
        if (index == 2 && (!int.TryParse(Retries.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out retries) || retries < 1 || retries > 100))
            problems.Add("Retries: a whole number from 1 to 100");
        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("Name: not empty");
        if (problems.Count > 0)
        {
            Error = string.Join("; ", problems) + ".";
            return false;
        }
        Error = null;
        entry = Entry with
        {
            Name = Name.Trim(),
            Runs = Repeat ? 0 : runs,
            StopAfter = stopAfter,
            DelayAfter = TimeSpan.FromSeconds(delay),
            OnError = (QueueErrorAction)index,
            MaxRetries = retries,
        };
        return true;
    }
}
