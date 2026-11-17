using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;
using System.Collections.ObjectModel;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>Browse the app-private script folder, import through the system picker, start/stop.</summary>
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
    }

    public ObservableCollection<ScriptEntry> Entries { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderDisplay), nameof(CanGoUp))]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    private string _currentFolder = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(DeleteCommand))]
    private ScriptEntry? _selectedEntry;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _scriptBusy;

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

    /// <summary>One level up; false at the root.</summary>
    public bool GoUp()
    {
        if (CurrentFolder.Length == 0)
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

    private bool CanStart() => SelectedEntry is { IsScript: true } && !ScriptBusy;

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

    [RelayCommand]
    private async Task SyncRepositoryAsync()
    {
        IsWorking = true;
        try
        {
            Message = await _services.RepositorySync.SyncAsync(null, CancellationToken.None);
            Refresh();
        }
        finally
        {
            IsWorking = false;
        }
    }

    internal void Apply(EngineSnapshot s)
    {
        ScriptBusy = s.Script.IsBusy;
        ScriptStatus = s.Script.State.ToString() + (s.Script.Message is null ? string.Empty : $": {s.Script.Message}");
        if (s.Script.ScriptPath is string path && path.StartsWith(_library.Root, StringComparison.Ordinal))
            LoadedScript = Path.GetRelativePath(_library.Root, path).Replace(Path.DirectorySeparatorChar, '/');
        DeleteCommand.NotifyCanExecuteChanged();
    }
}
