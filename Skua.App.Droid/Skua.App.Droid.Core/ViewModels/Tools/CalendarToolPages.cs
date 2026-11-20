using System.ComponentModel;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The Calendar page of the Tools hub (desktop: CalendarView). Created once with the hub; it ticks only while it is
/// open, the hub is on screen and the app is in the foreground.
/// </summary>
public sealed class CalendarToolPages : IDisposable
{
    private readonly ToolsViewModel _tools;
    private bool _visible;

    private CalendarToolPages(ToolsViewModel tools, AppServices services)
    {
        _tools = tools;
        Calendar = new CalendarViewModel(services);
        tools.Entries.Add(new ToolEntry("Calendar", "Server time, the daily and weekly reset countdowns, and the AQW boosts running now and coming (artix.com's calendar).",
            () => tools.Show(Calendar, "Calendar")));
        tools.PropertyChanged += OnToolsChanged;
    }

    public CalendarViewModel Calendar { get; }

    /// <summary>Adds the page to <paramref name="tools"/>.</summary>
    public static CalendarToolPages AddTo(ToolsViewModel tools, AppServices services) => new(tools, services);

    /// <summary>Whether the Tools hub is on screen (Settings tab, hub open, app in the foreground).</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            Update();
        }
    }

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolsViewModel.CurrentPage))
            Update();
    }

    private void Update() => Calendar.Visible = _visible && ReferenceEquals(_tools.CurrentPage, Calendar);

    public void Dispose()
    {
        _tools.PropertyChanged -= OnToolsChanged;
        Calendar.Dispose();
    }
}
