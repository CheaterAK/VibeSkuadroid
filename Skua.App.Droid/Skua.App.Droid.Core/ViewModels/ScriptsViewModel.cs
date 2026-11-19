using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;
using System.Collections.ObjectModel;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// Browse the app-private script folder, import through the system picker, sync the community repository, edit a
/// script's options, start/stop with a live status line.
/// </summary>
public sealed partial class ScriptsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly EngineHost _host;
    private readonly ScriptLibrary _library;

    public ScriptsViewModel(AppServices services)
    {
        _services = services;
        _host = services.Host;
        _library = services.Library;
        ScriptSupportText = _host.Engine.ScriptSupport.Available ? null : _host.Engine.ScriptSupport.Reason;
        RepositoryText = services.RepositorySync.Description;
        if (_host.Scheduler is { } scheduler)
        {
            Queue = new QueueViewModel(services, scheduler, editor => OptionsEditor = editor);
            Queue.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(QueueViewModel.TabTitle))
                    OnPropertyChanged(nameof(QueueTabTitle));
            };
        }
    }

    /// <summary>"Queue (3)".</summary>
    public string QueueTabTitle => Queue?.TabTitle ?? "Queue";

    /// <summary>The Queue tab (the engine's script queue), or null when the engine has none.</summary>
    public QueueViewModel? Queue { get; }

    public bool HasQueue => Queue is not null;

    /// <summary>The Queue tab is selected (else the Library).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowsing), nameof(IsQueueVisible), nameof(IsLibraryTab))]
    private bool _showQueue;

    public bool IsLibraryTab => !ShowQueue;

    public bool IsQueueVisible => OptionsEditor is null && ShowQueue && Queue is not null;

    /// <summary>The Library / Queue tabs (hidden behind the options editor).</summary>
    public bool ShowTabs => OptionsEditor is null && Queue is not null;

    [RelayCommand]
    private void SelectTab(string? tab)
    {
        ShowQueue = string.Equals(tab, "Queue", StringComparison.OrdinalIgnoreCase) && Queue is not null;
        if (ShowQueue)
            Queue!.RefreshPlaylists();
    }

    private bool CanAddToQueue() => SelectedEntry is { IsScript: true } && Queue is { IsRunning: false };

    /// <summary>Adds the selected script at the end of the queue.</summary>
    [RelayCommand(CanExecute = nameof(CanAddToQueue))]
    private void AddToQueue()
    {
        Queue!.Add(SelectedEntry!);
        Message = Queue.Message;
    }

    private CancellationTokenSource? _syncCts;
    private long _lastProgressTicks;

    public ObservableCollection<ScriptEntry> Entries { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderDisplay), nameof(CanGoUp))]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    private string _currentFolder = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(DeleteCommand), nameof(EditOptionsCommand), nameof(AddToQueueCommand))]
    private ScriptEntry? _selectedEntry;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(EditOptionsCommand))]
    private bool _scriptBusy;

    /// <summary>A script runs and is not paused: Pause is offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    private bool _canPause;

    /// <summary>The running script is paused: Resume is offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    private bool _isPaused;

    /// <summary>Kills, deaths, quests and drops of the running (or last) script.</summary>
    [ObservableProperty]
    private string? _countersText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncRepositoryCommand), nameof(CancelSyncCommand))]
    private bool _isSyncing;

    /// <summary>0..1 while files download; the bar is indeterminate when <see cref="SyncProgressKnown"/> is false.</summary>
    [ObservableProperty]
    private double _syncProgress;

    [ObservableProperty]
    private bool _syncProgressKnown;

    /// <summary>The options editor while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingOptions), nameof(IsBrowsing), nameof(IsQueueVisible), nameof(ShowTabs))]
    private ScriptOptionsViewModel? _optionsEditor;

    public bool IsEditingOptions => OptionsEditor is not null;

    public bool IsBrowsing => OptionsEditor is null && !(ShowQueue && Queue is not null);

    /// <summary>The script queue runs: manual starts wait (the queue owns the script slot).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(AddToQueueCommand))]
    private bool _queueRunning;

    [ObservableProperty]
    private string _scriptStatus = "Idle";

    [ObservableProperty]
    private string? _loadedScript;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isWorking;

    public string? ScriptSupportText { get; }

    public bool ScriptsUnavailable => ScriptSupportText is not null;

    public string RepositoryText { get; }

    public bool RepositoryAvailable => _services.RepositorySync.Available;

    public string FolderDisplay => CurrentFolder.Length == 0 ? "Scripts" : "Scripts/" + CurrentFolder;

    public bool CanGoUp => CurrentFolder.Length > 0;

    public string LibraryPath => _library.Root;

    partial void OnSearchTextChanged(string value) => Refresh();

    /// <summary>Reloads the current folder (or the search results).</summary>
    public void Refresh()
    {
        IReadOnlyList<ScriptEntry> items;
        try
        {
            items = string.IsNullOrWhiteSpace(SearchText) ? _library.List(CurrentFolder) : _library.Search(SearchText);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Message = e.Message;
            items = [];
        }
        Entries.Clear();
        foreach (ScriptEntry entry in items)
            Entries.Add(entry);
        if (Entries.Count == 0 && CurrentFolder.Length == 0 && string.IsNullOrWhiteSpace(SearchText))
            Message ??= "No scripts yet. Import a .cs file or a folder.";
    }

    /// <summary>One level up; false at the root (and on the Queue tab).</summary>
    public bool GoUp()
    {
        if (CurrentFolder.Length == 0 || ShowQueue)
            return false;
        int slash = CurrentFolder.LastIndexOf('/');
        CurrentFolder = slash < 0 ? string.Empty : CurrentFolder[..slash];
        SelectedEntry = null;
        Refresh();
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up() => GoUp();

    /// <summary>Opens a folder, or selects (and loads) a script.</summary>
    [RelayCommand]
    private void Open(ScriptEntry? entry)
    {
        if (entry is null)
            return;
        if (entry.IsFolder)
        {
            SearchText = string.Empty;
            CurrentFolder = entry.RelativePath;
            SelectedEntry = null;
            Message = null;
            Refresh();
            return;
        }
        SelectedEntry = entry;
        if (entry.IsScript)
        {
            _host.Engine.LoadScript(entry.FullPath);
            LoadedScript = entry.RelativePath;
        }
    }

    private bool CanStart() => SelectedEntry is { IsScript: true } && !ScriptBusy && !QueueRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Message = null;
        try
        {
            await _host.Engine.StartScriptAsync(SelectedEntry!.FullPath);
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(ScriptBusy))]
    private async Task StopAsync()
    {
        try
        {
            await _host.Engine.StopScriptAsync();
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseAsync()
    {
        try
        {
            await _host.Engine.PauseScriptAsync();
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private async Task ResumeAsync()
    {
        try
        {
            await _host.Engine.ResumeScriptAsync();
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    [RelayCommand]
    private Task ImportFilesAsync() => ImportAsync(_services.FilePicker.PickFilesAsync);

    [RelayCommand]
    private Task ImportFolderAsync() => ImportAsync(_services.FilePicker.PickFolderAsync);

    private async Task ImportAsync(Func<Task<IReadOnlyList<PickedFile>>> pick)
    {
        Message = null;
        IsWorking = true;
        try
        {
            IReadOnlyList<PickedFile> files = await pick();
            if (files.Count == 0)
                return;
            ImportResult result = await Task.Run(() => _library.ImportAsync(files, CurrentFolder));
            Message = result.Summary;
            Refresh();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            Message = $"Import failed: {e.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    private bool CanDelete() => SelectedEntry is not null && !(ScriptBusy && SelectedEntry.FullPath == _host.Engine.Snapshot.Script.ScriptPath);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (SelectedEntry is null)
            return;
        try
        {
            _library.Delete(SelectedEntry.RelativePath);
            Message = $"Deleted {SelectedEntry.Name}.";
            SelectedEntry = null;
            Refresh();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Message = e.Message;
        }
    }

    private bool CanSync() => !IsSyncing && _services.RepositorySync.Available;

    /// <summary>
    /// Downloads new and changed community scripts. Registered with the engine as background work, so the foreground
    /// service keeps the process alive when the app goes to the background; cancel keeps what was downloaded.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncRepositoryAsync()
    {
        CancellationTokenSource cts = new();
        _syncCts = cts;
        IsSyncing = true;
        SyncProgress = 0;
        SyncProgressKnown = false;
        Message = "Fetching the community script list...";
        IProgress<RepositorySyncProgress> progress = new DispatchedProgress(this);
        try
        {
            using IDisposable work = _host.Engine.BeginBackgroundWork("Syncing community scripts");
            RepositorySyncResult result = await Task.Run(() => _services.RepositorySync.SyncAsync(progress, cts.Token));
            Message = result.Summary + (result.Problems.Count > 0 && !result.RateLimited ? " " + string.Join("; ", result.Problems.Take(3)) : string.Empty);
            Refresh();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Message = $"Sync failed: {e.Message}";
        }
        finally
        {
            _syncCts = null;
            cts.Dispose();
            IsSyncing = false;
            SyncProgressKnown = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsSyncing))]
    private void CancelSync()
    {
        try
        {
            _syncCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Progress lines arrive per file from worker threads: at most ~8 updates a second reach the UI.
    private sealed class DispatchedProgress : IProgress<RepositorySyncProgress>
    {
        private readonly ScriptsViewModel _owner;

        public DispatchedProgress(ScriptsViewModel owner)
        {
            _owner = owner;
        }

        public void Report(RepositorySyncProgress value)
        {
            long now = Environment.TickCount64;
            bool last = value.Stage is "done" || value.Done >= value.Total;
            if (!last && now - Interlocked.Read(ref _owner._lastProgressTicks) < 120)
                return;
            Interlocked.Exchange(ref _owner._lastProgressTicks, now);
            _owner._services.Dispatcher.Post(() =>
            {
                if (!_owner.IsSyncing)
                    return;
                _owner.Message = value.Text;
                _owner.SyncProgressKnown = value.Stage == "downloading" && value.Total > 0;
                _owner.SyncProgress = value.Fraction;
            });
        }
    }

    private bool CanEditOptions() => SelectedEntry is { IsScript: true } && !ScriptBusy && !ScriptsUnavailable;

    /// <summary>Opens the options editor: compiles the script (later starts reuse the compile) and reads its options.</summary>
    [RelayCommand(CanExecute = nameof(CanEditOptions))]
    private async Task EditOptionsAsync()
    {
        ScriptEntry entry = SelectedEntry!;
        Message = $"Reading the options of {entry.Name} (compiling)...";
        IsWorking = true;
        try
        {
            ScriptOptionSet set = await _host.Engine.GetScriptOptionsAsync(entry.FullPath);
            if (set.Items.Count == 0)
            {
                Message = $"{entry.Name} has no script options.";
                return;
            }
            OptionsEditor = new ScriptOptionsViewModel(set, entry.Name);
            Message = null;
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    private string OptionsDir => Path.Combine(_services.Paths.SkuaDir, "options");

    private string? CurrentAccount => _host.Engine.Snapshot.Character?.Name ?? _host.Engine.Snapshot.AccountName;

    private bool CanEditCoreBotsOptions() => CoreBotsOptions.Available(_library.Root) && CurrentAccount is not null;

    /// <summary>CoreBots' own settings (class and gear sets, private rooms, delays) for the logged-in account.</summary>
    [RelayCommand(CanExecute = nameof(CanEditCoreBotsOptions))]
    private void EditCoreBotsOptions()
    {
        try
        {
            ScriptOptionSet set = CoreBotsOptions.Load(_library.Root, OptionsDir, CurrentAccount!);
            if (set.Items.Count == 0)
            {
                Message = "CoreBots.cs declares no settings (sync the community scripts first).";
                return;
            }
            OptionsEditor = new ScriptOptionsViewModel(set, "CoreBots", isCoreBots: true);
            Message = null;
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

    [RelayCommand]
    private async Task SaveOptionsAsync()
    {
        if (OptionsEditor is not ScriptOptionsViewModel editor)
            return;
        editor.Error = null;
        try
        {
            if (editor.IsCoreBots && editor.QueueEntryId is Guid entry && Queue is not null)
                Queue.SaveCoreBotsProfile(entry, editor.ToSet());
            else if (editor.IsCoreBots)
                await Task.Run(() => CoreBotsOptions.Save(editor.ToSet(), OptionsDir));
            else
                await _host.Engine.SaveScriptOptionsAsync(editor.ToSet());
            OptionsEditor = null;
            Message = editor.ProfileOf is string profile ? $"{editor.ScriptName} options of queue entry \"{profile}\" saved." : $"Options of {editor.ScriptName} saved.";
            if (Queue is not null && editor.ProfileOf is not null)
                Queue.Message = Message;
        }
        catch (EngineException e)
        {
            editor.Error = e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            editor.Error = e.Message;
        }
    }

    [RelayCommand]
    private void CloseOptions() => OptionsEditor = null;

    /// <summary>System back closes the options editor first, then the queue entry editor.</summary>
    public bool CloseOverlay()
    {
        if (OptionsEditor is not null)
        {
            OptionsEditor = null;
            return true;
        }
        return ShowQueue && Queue is not null && Queue.CloseOverlay();
    }

    internal void Apply(EngineSnapshot s)
    {
        ScriptBusy = s.Script.IsBusy;
        QueueRunning = Queue?.IsRunning == true;
        Queue?.Tick();
        IsPaused = s.Script.IsPaused;
        CanPause = s.Script.State == ScriptRunState.Running && !s.Script.Paused && _host.Engine.CanPauseScript;
        ScriptStatus = Formatting.ScriptLine(s.Script);
        CountersText = s.ScriptCounters?.Display;
        EditCoreBotsOptionsCommand.NotifyCanExecuteChanged();
        if (s.Script.ScriptPath is string path && path.StartsWith(_library.Root, StringComparison.Ordinal))
            LoadedScript = Path.GetRelativePath(_library.Root, path).Replace(Path.DirectorySeparatorChar, '/');
        DeleteCommand.NotifyCanExecuteChanged();
    }
}
