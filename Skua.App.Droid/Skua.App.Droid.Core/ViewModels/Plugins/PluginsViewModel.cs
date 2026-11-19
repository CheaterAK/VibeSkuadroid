using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Plugins;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>A built-in plugin on the Plugins page.</summary>
public sealed partial class PluginEntry : ObservableObject
{
    private readonly Action<string> _open;

    public PluginEntry(string id, string name, string author, string description, Action<string> open)
    {
        _open = open;
        Id = id;
        Name = name;
        Author = author;
        Description = description;
    }

    /// <summary>Stable key (persisted state, navigation).</summary>
    public string Id { get; }

    public string Name { get; }

    public string Author { get; }

    public string Description { get; }

    public string Byline => $"by {Author}";

    /// <summary>Opens its page.</summary>
    [RelayCommand]
    private void Open() => _open(Id);

    /// <summary>Its page can be opened.</summary>
    [ObservableProperty]
    private bool _canOpen;

    /// <summary>Why it cannot be used here, or what it covers here.</summary>
    [ObservableProperty]
    private string? _note;
}

/// <summary>
/// The Plugins page: the built-in plugins (ports of the desktop plugins) and their pages. Third-party plugin DLLs are
/// not loaded (see Skua.App.Droid/README.md).
/// </summary>
public sealed partial class PluginsViewModel : ObservableObject, IDisposable
{
    public const string DailyTrackerId = "dailytracker";
    public const string ButlerId = ButlerViewModel.PluginId;

    public PluginsViewModel(AppServices services)
    {
        Store = new PluginStore(services.Paths.PluginsFile);
        DailyTracker = new DailyTrackerViewModel(services);
        Butler = new ButlerViewModel(services, Store);
        Entries =
        [
            new PluginEntry(DailyTrackerId, "Daily & Weekly Tracker", "NinjaXz", "Tracks the status of Daily and Weekly quests.", OpenPage)
            {
                CanOpen = DailyTracker.IsAvailable,
                Note = DailyTracker.IsAvailable
                    ? "Daily and weekly quests, ultra boss lockouts, refresh and batch loading; starts the matching community script."
                    : "Needs the Skua script host.",
            },
            new PluginEntry(ButlerId, "ButlerV5", "l0newolf12", "Follow another player by name, or send this account to them.", OpenPage)
            {
                CanOpen = Butler.IsAvailable,
                Note = Butler.IsAvailable
                    ? "One account: follow any player (goto, then their cell, fighting beside them), go to or jump to a player, follow options and the log. Summoning and following your own other accounts (army) are not in this version."
                    : "Needs the Skua script host.",
            },
        ];
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
        CurrentPage = id switch
        {
            DailyTrackerId => DailyTracker,
            ButlerId => Butler,
            _ => null,
        };
    }

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
