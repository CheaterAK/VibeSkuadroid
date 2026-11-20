using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Calendar;
using Skua.App.Droid.Core.Platform;
using Skua.Core.Services;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>A boost as the Calendar shows it.</summary>
/// <param name="Name">The title without its date.</param>
/// <param name="Detail">"3 d · starts Thu 19 Nov".</param>
/// <param name="Status">"38h 12m left" or "in 14h 00m".</param>
/// <param name="ProgressPercent">How far through its run an active boost is.</param>
/// <param name="Active">Running now.</param>
/// <param name="EndingSoon">Active with under 12 hours left.</param>
public sealed record CalendarBoostRow(string Name, string Detail, string Status, double ProgressPercent, bool Active, bool EndingSoon);

/// <summary>
/// The desktop's Calendar (CalendarViewModel): server time (US Eastern), the daily and weekly reset countdowns with
/// their progress, and the AQW boosts of artix.com's calendar (active and upcoming), read through Skua's
/// <see cref="AqwCalendar"/> over HTTPS. The countdowns tick every second while the page is shown and the app is in
/// the foreground; the calendar is read when the page opens and the last read is 30 minutes old, or on Refresh.
/// </summary>
public sealed partial class CalendarViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(30);

    private readonly ICalendarSource _source;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private IReadOnlyList<CalendarEvent> _events = [];
    private TimeSpan _skew = TimeSpan.Zero;
    private DateTimeOffset _fetched = DateTimeOffset.MinValue;
    private CancellationTokenSource? _loop;

    public CalendarViewModel(AppServices services, TimeProvider? time = null)
    {
        _source = services.Calendar ?? new ArtixCalendarSource();
        _dispatcher = services.Dispatcher;
        _time = time ?? TimeProvider.System;
        Tick();
    }

    /// <summary>Ticks every second while true (page shown, app in the foreground). Set by the Tools hub.</summary>
    [ObservableProperty]
    private bool _visible;

    [ObservableProperty]
    private string _serverTime = string.Empty;

    [ObservableProperty]
    private string _dailyText = string.Empty;

    [ObservableProperty]
    private string _weeklyText = string.Empty;

    [ObservableProperty]
    private double _dailyProgress;

    [ObservableProperty]
    private double _weeklyProgress;

    [ObservableProperty]
    private bool _dailyUrgent;

    [ObservableProperty]
    private bool _weeklyUrgent;

    [ObservableProperty]
    private IReadOnlyList<CalendarBoostRow> _activeBoosts = [];

    [ObservableProperty]
    private IReadOnlyList<CalendarBoostRow> _upcomingBoosts = [];

    [ObservableProperty]
    private bool _hasNoBoosts;

    [ObservableProperty]
    private string _updatedText = string.Empty;

    [ObservableProperty]
    private string _note = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    public bool HasNote => Note.Length > 0;

    public bool HasActive => ActiveBoosts.Count > 0;

    public bool HasUpcoming => UpcomingBoosts.Count > 0;

    /// <summary>The source's host, for the page's footer.</summary>
    public string SourceText => "Boosts from https://www.artix.com/calendar/ (HTTPS; event pages only on artix.com). Resets: dailies at midnight server time (US Eastern), weeklies when Thursday turns into Friday.";

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    partial void OnActiveBoostsChanged(IReadOnlyList<CalendarBoostRow> value) => OnPropertyChanged(nameof(HasActive));

    partial void OnUpcomingBoostsChanged(IReadOnlyList<CalendarBoostRow> value) => OnPropertyChanged(nameof(HasUpcoming));

    partial void OnVisibleChanged(bool value)
    {
        CancellationTokenSource? old = Interlocked.Exchange(ref _loop, null);
        old?.Cancel();
        old?.Dispose();
        if (!value)
            return;
        Tick();
        if (_time.GetUtcNow() - _fetched >= RefreshAfter)
            _ = LoadAsync();
        CancellationTokenSource cts = new();
        _loop = cts;
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                _dispatcher.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                        Tick();
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    internal async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        if (IsLoading)
            return;
        IsLoading = true;
        Tick();
        try
        {
            (IReadOnlyList<CalendarEvent> events, TimeSpan skew) = await Task.Run(() => _source.FetchAsync(CancellationToken.None)).ConfigureAwait(true);
            _skew = skew;
            _events = events;
            Note = string.Empty;
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            Note = _events.Count == 0 ? e.Message : $"{e.Message}; showing the last list";
        }
        finally
        {
            _fetched = _time.GetUtcNow();
            IsLoading = false;
            Tick();
        }
    }

    /// <summary>Updates the clock, the countdowns and the boost rows (every second while shown).</summary>
    internal void Tick()
    {
        DateTimeOffset now = _time.GetUtcNow() + _skew;
        DateTimeOffset nextDaily = AqwCalendar.NextDaily(now), nextWeekly = AqwCalendar.NextWeekly(now);
        DailyText = AqwCalendar.FormatSpan(nextDaily - now);
        WeeklyText = AqwCalendar.FormatSpan(nextWeekly - now);
        DailyProgress = AqwCalendar.Fraction(AqwCalendar.PrevDaily(now), nextDaily, now) * 100;
        WeeklyProgress = AqwCalendar.Fraction(AqwCalendar.PrevWeekly(now), nextWeekly, now) * 100;
        DailyUrgent = nextDaily - now < TimeSpan.FromHours(1);
        WeeklyUrgent = nextWeekly - now < TimeSpan.FromHours(1);
        ServerTime = TimeZoneInfo.ConvertTime(now, AqwCalendar.Eastern).ToString("ddd d MMM HH:mm:ss", CultureInfo.InvariantCulture);
        UpdatedText = IsLoading ? "Reading the calendar..."
            : _fetched == DateTimeOffset.MinValue ? string.Empty
            : $"Read {(int)(_time.GetUtcNow() - _fetched).TotalMinutes} min ago from artix.com/calendar";
        RefreshRows(now);
    }

    // The desktop's RefreshRows: boosts that have not ended, active ones first, at most 14 upcoming.
    private void RefreshRows(DateTimeOffset now)
    {
        List<CalendarBoostRow> active = new();
        List<CalendarBoostRow> upcoming = new();
        foreach (CalendarEvent e in _events)
        {
            DateTimeOffset start = AqwCalendar.ToUtc(e.Day);
            DateTimeOffset end = start + TimeSpan.FromHours(e.Hours ?? 24);
            if (now >= end)
                continue;
            bool isActive = now >= start;
            string length = e.Hours is int h ? (h % 24 == 0 ? $"{h / 24} d  ·  " : $"{h} h  ·  ") : string.Empty;
            CalendarBoostRow row = new(
                AqwCalendar.PlainTitle(e.Title),
                $"{length}starts {e.Day.ToString("ddd d MMM", CultureInfo.InvariantCulture)}",
                isActive ? $"{AqwCalendar.FormatShort(end - now)} left" : $"in {AqwCalendar.FormatShort(start - now)}",
                isActive ? Math.Clamp((now - start).TotalSeconds / Math.Max(1, (end - start).TotalSeconds), 0, 1) * 100 : 0,
                isActive,
                isActive && end - now < TimeSpan.FromHours(12));
            (isActive ? active : upcoming).Add(row);
        }
        if (!active.SequenceEqual(ActiveBoosts))
            ActiveBoosts = active;
        List<CalendarBoostRow> shown = upcoming.Take(14).ToList();
        if (!shown.SequenceEqual(UpcomingBoosts))
            UpcomingBoosts = shown;
        HasNoBoosts = active.Count == 0 && upcoming.Count == 0 && !IsLoading;
    }

    public void Dispose() => Visible = false;
}
