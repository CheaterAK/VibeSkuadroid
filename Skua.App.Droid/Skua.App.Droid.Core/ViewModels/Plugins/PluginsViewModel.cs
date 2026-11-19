using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Plugins;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>A built-in plugin on the Plugins page: its description, on/off switch (saved) and its page.</summary>
public sealed partial class PluginEntry : ObservableObject
{
    private readonly Action<string> _open;
    private readonly Action<PluginEntry> _enabledChanged;
    private readonly bool _loading = true;

    /// <param name="id">Stable key (saved state, navigation).</param>
    /// <param name="name">Display name (the desktop plugin's).</param>
    /// <param name="author">The desktop plugin's author.</param>
    /// <param name="description">The desktop plugin's description.</param>
    /// <param name="available">It can run here (false: <paramref name="note"/> says why; it can then not be switched on).</param>
    /// <param name="hasPage">It has a page to open.</param>
    /// <param name="note">What it covers here, or why it is not available.</param>
    /// <param name="enabled">Saved on/off state.</param>
    /// <param name="open">Opens the page.</param>
    /// <param name="enabledChanged">Told when the user switches it.</param>
    public PluginEntry(string id, string name, string author, string description, bool available, bool hasPage, string? note, bool enabled,
        Action<string> open, Action<PluginEntry> enabledChanged)
    {
        Id = id;
        Name = name;
        Author = author;
        Description = description;
        IsAvailable = available;
        HasPage = hasPage;
        Note = note;
        _open = open;
        _enabledChanged = enabledChanged;
        IsEnabled = available && enabled;
        _loading = false;
    }

    public string Id { get; }

    public string Name { get; }

    public string Author { get; }

    public string Description { get; }

    public string Byline => $"by {Author}";

    /// <summary>It can run in this app (Cosmetics cannot: it only changes what a game view draws).</summary>
    public bool IsAvailable { get; }

    public bool HasPage { get; }

    /// <summary>What it covers here, or why it is not available.</summary>
    public string? Note { get; }

    /// <summary>Switched on (saved). Off: no background work, page closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpen), nameof(StateText))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private bool _isEnabled;

    public bool CanOpen => IsAvailable && HasPage && IsEnabled;

    public string StateText => !IsAvailable ? "Not available" : IsEnabled ? "On" : "Off";

    partial void OnIsEnabledChanged(bool value)
    {
        // An unavailable plugin cannot be switched on (the switch is disabled; this guards the ViewModel too).
        if (value && !IsAvailable)
        {
            IsEnabled = false;
            return;
        }
        if (!_loading)
            _enabledChanged(this);
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => _open(Id);
}

/// <summary>
/// The Plugins page: the built-in plugins (ports of the desktop plugins), each with an on/off switch and its page, the
/// state and options saved in app-private storage (<see cref="PluginStore"/>). Third-party plugin DLLs are not loaded
/// (see Skua.App.Droid/README.md).
/// </summary>
public sealed partial class PluginsViewModel : ObservableObject, IDisposable
{
    public const string DailyTrackerId = "dailytracker";
    public const string ButlerId = ButlerViewModel.PluginId;
    public const string CosmeticsId = "cosmetics";

    /// <summary>
    /// Why Cosmetics cannot run here. Every action of the desktop plugin changes only what its own client draws: it
    /// writes the avatar's equipment slots (objData.eqp) and loads their art (loadMovieAtES), recolours the avatar,
    /// removes the pet clip, and feeds the client fake "genderSwap" packets (sendClientPacket). It never sends a request
    /// to the server (no equip, no gender change, no outfit save); saved outfits are sets of catalog art, not items the
    /// account owns. Other players never see any of it, and this app draws nothing.
    /// </summary>
    public const string CosmeticsNote =
        "Not available: it only changes what a game window draws (looks you do not own, colours, gender preview, saved looks). " +
        "It never asks the server for anything, so other players never see it, and this app has no game view.";

    public PluginsViewModel(AppServices services)
    {
        Store = new PluginStore(services.Paths.PluginsFile);
        DailyTracker = new DailyTrackerViewModel(services);
        Butler = new ButlerViewModel(services, Store);
        Entries =
        [
            new PluginEntry(DailyTrackerId, "Daily & Weekly Tracker", "NinjaXz", "Tracks the status of Daily and Weekly quests.",
                DailyTracker.IsAvailable, hasPage: true,
                DailyTracker.IsAvailable
                    ? "Daily and weekly quests and ultra boss lockouts of this character: refresh, batch loading, and Start for the matching community script."
                    : "Needs the Skua script host.",
                Store.IsEnabled(DailyTrackerId), OpenPage, OnEnabledChanged),
            new PluginEntry(ButlerId, "ButlerV5", "l0newolf12", "Follow another player by name, or send this account to them.",
                Butler.IsAvailable, hasPage: true,
                Butler.IsAvailable
                    ? "One account: follow any player (goto, then their cell, fighting beside them), go to or jump to a player, follow options and the log. Summoning and following your own other accounts (army) are not in this version."
                    : "Needs the Skua script host.",
                Store.IsEnabled(ButlerId), OpenPage, OnEnabledChanged),
            new PluginEntry(CosmeticsId, "Cosmetics", "NinjaXz & Lord Exelot", "Visually loads any cosmetic item without owning it and manages saved outfit loadouts.",
                available: false, hasPage: false, CosmeticsNote, enabled: false, OpenPage, OnEnabledChanged),
        ];
        foreach (PluginEntry entry in Entries)
            ApplyEnabled(entry);
    }

    public IReadOnlyList<PluginEntry> Entries { get; }

    /// <summary>The plugins' saved state and options (app-private).</summary>
    public PluginStore Store { get; }

    public DailyTrackerViewModel DailyTracker { get; }

    public ButlerViewModel Butler { get; }

    /// <summary>The open plugin page (one of the plugin ViewModels), or null for the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListVisible), nameof(Title))]
    private object? _currentPage;

    /// <summary>The Plugins tab is shown.</summary>
    [ObservableProperty]
    private bool _isActive;

    public bool IsListVisible => CurrentPage is null;

    public string Title => CurrentPage switch
    {
        DailyTrackerViewModel => "Daily Tracker",
        ButlerViewModel => "ButlerV5",
        _ => "Plugins",
    };

    [RelayCommand]
    private void Open(string? id) => OpenPage(id);

    private void OpenPage(string? id)
    {
        PluginEntry? entry = Entries.FirstOrDefault(e => e.Id == id);
        if (entry is not { CanOpen: true })
            return;
        CurrentPage = PageOf(entry.Id);
    }

    private object? PageOf(string id) => id switch
    {
        DailyTrackerId => DailyTracker,
        ButlerId => Butler,
        _ => null,
    };

    [RelayCommand]
    private void Close() => CurrentPage = null;

    /// <summary>System back on this tab: closes an open plugin page; false when the list is shown.</summary>
    public bool CloseSubPage()
    {
        if (CurrentPage is null)
            return false;
        CurrentPage = null;
        return true;
    }

    private void OnEnabledChanged(PluginEntry entry)
    {
        Store.SetEnabled(entry.Id, entry.IsEnabled);
        ApplyEnabled(entry);
    }

    private void ApplyEnabled(PluginEntry entry)
    {
        switch (entry.Id)
        {
            case DailyTrackerId:
                DailyTracker.IsEnabled = entry.IsEnabled;
                break;
            case ButlerId:
                Butler.IsEnabled = entry.IsEnabled;
                break;
        }
        if (!entry.IsEnabled && CurrentPage is not null && ReferenceEquals(CurrentPage, PageOf(entry.Id)))
            CurrentPage = null;
        UpdateActivePages();
    }

    partial void OnCurrentPageChanged(object? value) => UpdateActivePages();

    partial void OnIsActiveChanged(bool value) => UpdateActivePages();

    private void UpdateActivePages()
    {
        DailyTracker.IsActive = IsActive && ReferenceEquals(CurrentPage, DailyTracker);
        Butler.IsActive = IsActive && ReferenceEquals(CurrentPage, Butler);
    }

    internal void Apply(EngineSnapshot s)
    {
        DailyTracker.Apply(s);
        Butler.Apply(s);
    }

    public void Dispose()
    {
        DailyTracker.Dispose();
        Butler.Dispose();
    }
}
