using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Core.Platform;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>One filter chip: a kind (null for all), its label, and how many entries it holds (the desktop's NotificationFilter).</summary>
public sealed partial class NotificationFilter : ObservableObject
{
    public NotificationFilter(NotificationKind? kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    public NotificationKind? Kind { get; }

    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private int _count;

    [ObservableProperty]
    private bool _isSelected;

    public string Label => $"{Title} {Count}";

    public bool Matches(NotificationEntry entry) => Kind is null || entry.Kind == Kind;
}

/// <summary>
/// The Notification Center (port of the desktop's NotificationCenterViewModel): drops, deaths, relogins, quests,
/// maps and script starts, stops and errors, newest first, with the desktop's filters, kept across restarts
/// (<see cref="NotificationHistory"/>). Copy and Share give the shown entries in the desktop's text format.
/// </summary>
/// <remarks>Collects nothing itself (the process-wide hub does); while the page is hidden it does no UI work.</remarks>
public sealed partial class NotificationCenterViewModel : ObservableObject, IDisposable
{
    private readonly NotificationHistory _history;
    private readonly IUiDispatcher _dispatcher;
    private readonly IFilePicker _picker;
    private readonly IPlatformServices _platform;

    public NotificationCenterViewModel(NotificationHistory history, IUiDispatcher dispatcher, IFilePicker picker, IPlatformServices platform)
    {
        _history = history;
        _dispatcher = dispatcher;
        _picker = picker;
        _platform = platform;
        Filters =
        [
            new NotificationFilter(null, "All"),
            new NotificationFilter(NotificationKind.Item, "Items"),
            new NotificationFilter(NotificationKind.Activity, "Activity"),
            new NotificationFilter(NotificationKind.Warning, "Warnings"),
            new NotificationFilter(NotificationKind.Error, "Errors"),
            new NotificationFilter(NotificationKind.System, "Maps"),
        ];
        _selectedFilter = Filters[0];
        Filters[0].IsSelected = true;
        _history.Added += OnAdded;
        _history.Cleared += OnCleared;
    }

    public IReadOnlyList<NotificationFilter> Filters { get; }

    /// <summary>The entries the selected filter lets through, newest first.</summary>
    public ObservableCollection<NotificationEntry> FilteredNotifications { get; } = new();

    [ObservableProperty]
    private NotificationFilter _selectedFilter;

    /// <summary>The page is shown (set by the Tools hub).</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string? _message;

    public string EmptyText => SelectedFilter.Kind is null
        ? "No events yet. Drops, deaths, relogins, quests and script starts and stops show up here."
        : $"No {SelectedFilter.Kind switch
        {
            NotificationKind.Item => "item",
            NotificationKind.Warning => "warning",
            NotificationKind.Error => "error",
            NotificationKind.System => "map",
            _ => "activity",
        }} events yet.";

    /// <summary>The shown entries, oldest first, one per line (the desktop's Copy/Save format).</summary>
    public static string ToText(IEnumerable<NotificationEntry> entries) =>
        string.Join(Environment.NewLine, entries.OrderBy(e => e.Timestamp).Select(e => e.Line));

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
            Rebuild();
    }

    partial void OnSelectedFilterChanged(NotificationFilter value)
    {
        foreach (NotificationFilter f in Filters)
            f.IsSelected = ReferenceEquals(f, value);
        Rebuild();
        OnPropertyChanged(nameof(EmptyText));
    }

    [RelayCommand]
    private void SelectFilter(NotificationFilter? filter)
    {
        if (filter is not null)
            SelectedFilter = filter;
    }

    [RelayCommand]
    private void Clear()
    {
        _history.Clear();
        Message = null;
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (FilteredNotifications.Count == 0)
            return;
        await _picker.CopyTextAsync(ToText(FilteredNotifications));
        Message = $"Copied {FilteredNotifications.Count} entries.";
    }

    [RelayCommand]
    private void Share()
    {
        if (FilteredNotifications.Count > 0)
            _platform.ShareText("VibeSkua notifications", ToText(FilteredNotifications));
    }

    /// <summary>Reads the whole history again (when shown, and after the filter changed).</summary>
    public void Rebuild()
    {
        IReadOnlyList<NotificationEntry> all = _history.Entries;
        FilteredNotifications.Clear();
        foreach (NotificationEntry entry in all.Where(SelectedFilter.Matches))
            FilteredNotifications.Add(entry);
        foreach (NotificationFilter f in Filters)
            f.Count = all.Count(f.Matches);
        IsEmpty = FilteredNotifications.Count == 0;
    }

    private void OnAdded(object? sender, NotificationEntry entry)
    {
        if (!IsActive)
            return;
        _dispatcher.Post(() =>
        {
            if (!IsActive)
                return;
            Filters[0].Count++;
            if (Filters.FirstOrDefault(f => f.Kind == entry.Kind) is NotificationFilter kind)
                kind.Count++;
            if (SelectedFilter.Matches(entry))
                FilteredNotifications.Insert(0, entry);
            // Only the oldest entry is touched, never the whole list (the desktop's trimming).
            while (FilteredNotifications.Count > NotificationHistory.Capacity)
                FilteredNotifications.RemoveAt(FilteredNotifications.Count - 1);
            IsEmpty = FilteredNotifications.Count == 0;
        });
    }

    private void OnCleared(object? sender, EventArgs e) => _dispatcher.Post(Rebuild);

    public void Dispose()
    {
        _history.Added -= OnAdded;
        _history.Cleared -= OnCleared;
    }
}
