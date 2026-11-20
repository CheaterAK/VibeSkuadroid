using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Plugins;
using Skua.App.Droid.Core.Tools;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>A destination card: tap to travel, edit, delete.</summary>
public sealed class FastTravelRow
{
    public FastTravelRow(TravelDestination destination, FastTravelViewModel page)
    {
        Destination = destination;
        TravelCommand = new AsyncRelayCommand(() => page.TravelAsync(destination));
        EditCommand = new RelayCommand(() => page.BeginEdit(destination));
        DeleteCommand = new AsyncRelayCommand(() => page.DeleteAsync(destination));
    }

    public TravelDestination Destination { get; }

    public string Name => Destination.Name;

    public string Where => $"{Destination.Map} · {Destination.Cell} · {Destination.Pad}";

    public IAsyncRelayCommand TravelCommand { get; }

    public IRelayCommand EditCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }
}

/// <summary>
/// Fast Travel (port of the desktop Fast Travel window): the saved destinations (Skua's own "FastTravels" setting, so the
/// desktop's list and format), search, one tap to travel through Skua's map service (private room optional), add, edit,
/// delete, clear and Skua's presets. Travel only moves the character, so it is one tap (after "a script is running; do
/// it anyway?" while a script runs); deleting and replacing the list ask first.
/// </summary>
public sealed partial class FastTravelViewModel : ObservableObject
{
    public const string StoreId = "fasttravel";
    internal const string KeyPrivate = "usePrivateRoom";
    internal const string KeyRoom = "privateRoomNumber";
    public const int DefaultRoomNumber = 111111; // Skua's map service default

    private readonly EngineHost _host;
    private readonly PluginStore _store;
    private readonly List<TravelDestination> _all = new();
    private bool _loading;

    public FastTravelViewModel(AppServices services, PluginStore store)
    {
        _host = services.Host;
        _store = store;
        _loading = true;
        UsePrivateRoom = !bool.TryParse(_store.GetOption(StoreId, KeyPrivate), out bool p) || p; // desktop default: on
        PrivateRoomNumber = int.TryParse(_store.GetOption(StoreId, KeyRoom), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : DefaultRoomNumber;
        _loading = false;
        Apply(_host.Engine.Snapshot);
    }

    private IFastTravelBackend? Backend => _host.Engine.Plugins?.GameTools?.FastTravel;

    public bool IsAvailable => Backend is not null;

    public ConfirmHost Confirm { get; } = new();

    public ObservableCollection<FastTravelRow> Destinations { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplySearch();

    [ObservableProperty]
    private bool _usePrivateRoom = true;

    [ObservableProperty]
    private decimal? _privateRoomNumber = DefaultRoomNumber;

    partial void OnUsePrivateRoomChanged(bool value) => SaveRoom();

    partial void OnPrivateRoomNumberChanged(decimal? value) => SaveRoom();

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _status;

    // ---- editor (add, or edit the destination in EditingOriginal)

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editMap = string.Empty;

    [ObservableProperty]
    private string _editCell = "Enter";

    [ObservableProperty]
    private string _editPad = "Spawn";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(EditorTitle), nameof(SaveText))]
    private TravelDestination? _editingOriginal;

    public bool IsEditing => EditingOriginal is not null;

    public string EditorTitle => IsEditing ? $"EDIT {EditingOriginal!.Name.ToUpperInvariant()}" : "ADD A DESTINATION";

    public string SaveText => IsEditing ? "Save" : "Add";

    public int Count => _all.Count;

    /// <summary>Reads the saved list again (when the page opens).</summary>
    public void Refresh()
    {
        Message = null;
        if (Backend is not IFastTravelBackend backend)
        {
            Message = "Fast Travel needs the Skua script host, which is not available in this engine.";
            return;
        }
        try
        {
            _all.Clear();
            _all.AddRange(backend.Load());
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
        ApplySearch();
    }

    private void ApplySearch()
    {
        Destinations.Clear();
        foreach (TravelDestination d in _all)
        {
            if (Matches(d, SearchText))
                Destinations.Add(new FastTravelRow(d, this));
        }
        OnPropertyChanged(nameof(Count));
    }

    /// <summary>The desktop window's search (Skua.Core.Models.FastTravelEntry.Matches): name, map or cell.</summary>
    internal static bool Matches(TravelDestination d, string? filter)
    {
        string f = filter?.Trim() ?? string.Empty;
        return f.Length == 0
            || d.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || d.Map.Contains(f, StringComparison.OrdinalIgnoreCase)
            || d.Cell.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    internal async Task TravelAsync(TravelDestination d)
    {
        Message = null;
        if (Backend is not IFastTravelBackend backend)
            return;
        if (!await Confirm.AllowWhileScriptRunsAsync(ScriptBusy, $"Travel to {d.Name} ({d.Map}, {d.Cell})").ConfigureAwait(true))
        {
            Status = $"Cancelled: travel to {d.Name}.";
            return;
        }
        int room = (int)Math.Clamp(PrivateRoomNumber ?? DefaultRoomNumber, 1, 99999999);
        try
        {
            backend.Travel(d, UsePrivateRoom, room);
            Status = UsePrivateRoom && !d.Map.Contains('-') ? $"Travelling to {d.Name}: {d.Map}-{room}, {d.Cell}." : $"Travelling to {d.Name}: {d.Map}, {d.Cell}.";
            Log($"Fast travel to {d.Name} ({d.Map}, {d.Cell}, {d.Pad}).");
        }
        catch (EngineException e)
        {
            Message = e.Message;
        }
    }

    internal void BeginEdit(TravelDestination d)
    {
        EditingOriginal = d;
        EditName = d.Name;
        EditMap = d.Map;
        EditCell = d.Cell;
        EditPad = d.Pad;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EditingOriginal = null;
        EditName = string.Empty;
        EditMap = string.Empty;
        EditCell = "Enter";
        EditPad = "Spawn";
    }

    /// <summary>Fills the editor's map, cell and pad with where the character is (desktop: "Current").</summary>
    [RelayCommand]
    private void UseCurrentLocation()
    {
        Message = null;
        if (Backend?.CurrentLocation() is not TravelLocation here)
        {
            Message = "Log in first: the current location is only known in game.";
            return;
        }
        EditMap = here.Map;
        EditCell = here.Cell;
        EditPad = here.Pad;
        if (string.IsNullOrWhiteSpace(EditName))
            EditName = here.Map;
    }

    /// <summary>Adds the editor's destination, or saves the edited one in its place.</summary>
    [RelayCommand]
    private void SaveEdit()
    {
        Message = null;
        string name = EditName.Trim(), map = EditMap.Trim(), cell = EditCell.Trim(), pad = EditPad.Trim();
        if (name.Length == 0 || map.Length == 0 || cell.Length == 0 || pad.Length == 0)
        {
            Message = "Fill in the name, map, cell and pad.";
            return;
        }
        if ((name + map + cell + pad).Contains(','))
        {
            Message = "Commas are not allowed (Skua saves each destination as one comma separated line).";
            return;
        }
        TravelDestination d = new(name, map, cell, pad);
        List<TravelDestination> next = new(_all);
        int index = EditingOriginal is null ? -1 : next.IndexOf(EditingOriginal);
        if (index >= 0)
            next[index] = d;
        else
            next.Add(d);
        if (Save(next))
        {
            Status = index >= 0 ? $"Saved {d.Name}." : $"Added {d.Name}.";
            CancelEdit();
        }
    }

    internal async Task DeleteAsync(TravelDestination d)
    {
        Message = null;
        if (!await Confirm.AskAsync("Delete destination", $"Delete {d.Name} ({d.Map}, {d.Cell}, {d.Pad}) from the list?", "Delete", danger: true).ConfigureAwait(true))
            return;
        List<TravelDestination> next = new(_all);
        if (next.Remove(d) && Save(next))
        {
            Status = $"Deleted {d.Name}.";
            if (EditingOriginal == d)
                CancelEdit();
        }
    }

    /// <summary>Desktop "Clear": removes every destination (asks first).</summary>
    [RelayCommand]
    private async Task ClearAsync()
    {
        Message = null;
        if (_all.Count == 0)
            return;
        if (!await Confirm.AskAsync("Clear Fast Travels", "This will clear all fast travels from the list. Continue?", "Clear", danger: true).ConfigureAwait(true))
            return;
        if (Save([]))
            Status = "Cleared the list.";
    }

    /// <summary>Adds Skua's preset destinations that are not in the list (asks first).</summary>
    [RelayCommand]
    private async Task AddPresetsAsync()
    {
        Message = null;
        if (Backend is not IFastTravelBackend backend)
            return;
        IReadOnlyList<TravelDestination> presets;
        try
        {
            presets = backend.Presets;
        }
        catch (EngineException e)
        {
            Message = e.Message;
            return;
        }
        List<TravelDestination> missing = presets.Where(p => !_all.Contains(p)).ToList();
        if (missing.Count == 0)
        {
            Status = "Every preset is in the list.";
            return;
        }
        if (!await Confirm.AskAsync("Add presets", $"Add Skua's presets to the list?\n\n{string.Join("\n", missing.Select(m => m.Name))}", "Add").ConfigureAwait(true))
            return;
        if (Save([.. _all, .. missing]))
            Status = $"Added {missing.Count} preset(s).";
    }

    private bool Save(IReadOnlyList<TravelDestination> next)
    {
        if (Backend is not IFastTravelBackend backend)
            return false;
        try
        {
            backend.Save(next);
        }
        catch (EngineException e)
        {
            Message = e.Message;
            return false;
        }
        _all.Clear();
        _all.AddRange(next);
        ApplySearch();
        return true;
    }

    private void SaveRoom()
    {
        if (_loading)
            return;
        try
        {
            _store.SetOptions(StoreId, new Dictionary<string, string>
            {
                [KeyPrivate] = UsePrivateRoom.ToString(),
                [KeyRoom] = ((int)Math.Clamp(PrivateRoomNumber ?? DefaultRoomNumber, 1, 99999999)).ToString(CultureInfo.InvariantCulture),
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not save: {e.Message}";
        }
    }

    private void Log(string text)
    {
        try
        {
            _host.Engine.WriteLog(EngineLogLevel.Info, "tools", text);
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

    private bool _scriptBusy;

    /// <summary>A script compiles, runs or stops (last snapshot, or the engine's own state now).</summary>
    public bool ScriptBusy => _scriptBusy || _host.Engine.Snapshot.Script.IsBusy;
}
