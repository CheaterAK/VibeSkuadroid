using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Shared.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Threading;
using Skua.Core.Services;

namespace Skua.App.Avalonia.ViewModels;

/// <summary>
/// Calendar: the daily and weekly reset countdowns and the AQW boost list.
/// Dailies reset at 12 AM server time; weeklies at 12 AM server time when Thursday turns into Friday. AQW's server time is US Eastern
/// (with daylight saving). The game's own "server time" chat line is the PC's clock, so time is the PC clock corrected by the
/// <c>Date</c> header of the artix.com answer. The boosts are read from the script array of https://www.artix.com/calendar/.
/// The rules and the fetch are shared with the Android app (<see cref="AqwCalendar"/>); this window keeps its signatures.
/// </summary>
public partial class CalendarViewModel : BotControlViewModelBase
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(30);
    // artix.com answers 403 to a request without a User-Agent.
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AqwCalendar.UserAgent);
        return http;
    }
    private static readonly TimeZoneInfo Eastern = AqwCalendar.Eastern;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<CalendarBoost> _rows = new();
    private TimeSpan _skew = TimeSpan.Zero;
    private DateTime _fetched = DateTime.MinValue;
    private string _signature = string.Empty;

    public CalendarViewModel() : base("Calendar", 520, 640, canResize: true)
    {
        _timer.Tick += (_, _) => Tick();
        ActiveBoosts = new ObservableCollection<CalendarBoost>();
        UpcomingBoosts = new ObservableCollection<CalendarBoost>();
        Tick();
    }

    public ObservableCollection<CalendarBoost> ActiveBoosts { get; }
    public ObservableCollection<CalendarBoost> UpcomingBoosts { get; }

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
    private string _serverTime = string.Empty;

    [ObservableProperty]
    private string _updatedText = string.Empty;

    [ObservableProperty]
    private string _note = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasNoBoosts;

    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool HasActive => ActiveBoosts.Count > 0;
    public bool HasUpcoming => UpcomingBoosts.Count > 0;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    protected override void OnActivated()
    {
        _timer.Start();
        Tick();
        if (DateTime.UtcNow - _fetched >= RefreshAfter)
            _ = LoadAsync();
    }

    protected override void OnDeactivated()
    {
        _timer.Stop();
    }

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh() => LoadAsync();

    // ------------------------------------------------------------------------------------------------ time

    private DateTimeOffset ServerUtc => DateTimeOffset.UtcNow + _skew;

    private static DateTimeOffset ToUtc(DateTime easternLocalMidnight) => AqwCalendar.ToUtc(easternLocalMidnight);

    /// <summary>The next daily reset (server midnight) after <paramref name="utc"/>.</summary>
    public static DateTimeOffset NextDaily(DateTimeOffset utc) => AqwCalendar.NextDaily(utc);

    /// <summary>The daily reset that last happened.</summary>
    public static DateTimeOffset PrevDaily(DateTimeOffset utc) => AqwCalendar.PrevDaily(utc);

    /// <summary>The next weekly reset (server midnight at the start of Friday) after <paramref name="utc"/>.</summary>
    public static DateTimeOffset NextWeekly(DateTimeOffset utc) => AqwCalendar.NextWeekly(utc);

    /// <summary>The weekly reset that last happened.</summary>
    public static DateTimeOffset PrevWeekly(DateTimeOffset utc) => AqwCalendar.PrevWeekly(utc);

    /// <summary>"6d 3h 12m", "3h 12m 05s", "12m 05s", "5s".</summary>
    public static string FormatSpan(TimeSpan span) => AqwCalendar.FormatSpan(span);

    /// <summary>The same without seconds.</summary>
    public static string FormatShort(TimeSpan span) => AqwCalendar.FormatShort(span);

    private void Tick()
    {
        DateTimeOffset now = ServerUtc;
        DateTimeOffset nextDaily = NextDaily(now), nextWeekly = NextWeekly(now);
        DailyText = FormatSpan(nextDaily - now);
        WeeklyText = FormatSpan(nextWeekly - now);
        DailyProgress = Fraction(PrevDaily(now), nextDaily, now);
        WeeklyProgress = Fraction(PrevWeekly(now), nextWeekly, now);
        DailyUrgent = nextDaily - now < TimeSpan.FromHours(1);
        WeeklyUrgent = nextWeekly - now < TimeSpan.FromHours(1);
        ServerTime = TimeZoneInfo.ConvertTime(now, Eastern).ToString("ddd d MMM HH:mm:ss", CultureInfo.InvariantCulture);
        UpdatedText = IsLoading ? "Reading the calendar..." : _fetched == DateTime.MinValue ? string.Empty : $"Read {(int)(DateTime.UtcNow - _fetched).TotalMinutes} min ago from artix.com/calendar";
        RefreshRows(now);
    }

    private static double Fraction(DateTimeOffset prev, DateTimeOffset next, DateTimeOffset now) => AqwCalendar.Fraction(prev, next, now);

    // ------------------------------------------------------------------------------------------------ boosts

    private void RefreshRows(DateTimeOffset now)
    {
        List<CalendarBoost> live = new();
        foreach (CalendarBoost row in _rows)
        {
            DateTimeOffset start = ToUtc(row.Day);
            DateTimeOffset end = start + TimeSpan.FromHours(row.Hours ?? 24);
            if (now >= end)
                continue;
            bool active = now >= start;
            row.Active = active;
            row.Status = active ? $"{FormatShort(end - now)} left" : $"in {FormatShort(start - now)}";
            row.Progress = active ? Math.Clamp((now - start).TotalSeconds / Math.Max(1, (end - start).TotalSeconds), 0, 1) : 0;
            row.EndingSoon = active && end - now < TimeSpan.FromHours(12);
            live.Add(row);
        }
        string signature = string.Join('|', live.Select(r => $"{r.Title}{r.Day:yyyyMMdd}{r.Active}"));
        if (signature == _signature)
            return;
        _signature = signature;
        ActiveBoosts.Clear();
        UpcomingBoosts.Clear();
        foreach (CalendarBoost row in live.Where(r => r.Active))
            ActiveBoosts.Add(row);
        foreach (CalendarBoost row in live.Where(r => !r.Active).Take(14))
            UpcomingBoosts.Add(row);
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(HasUpcoming));
        HasNoBoosts = live.Count == 0;
    }

    private async Task LoadAsync()
    {
        if (IsLoading)
            return;
        IsLoading = true;
        try
        {
            (List<CalendarBoost> rows, TimeSpan skew) = await Task.Run(FetchAsync);
            _skew = skew;
            _rows.Clear();
            _rows.AddRange(rows);
            _signature = string.Empty;
            Note = string.Empty;
        }
        catch (Exception ex)
        {
            Note = _rows.Count == 0 ? ex.Message : $"{ex.Message}; showing the last list";
        }
        finally
        {
            _fetched = DateTime.UtcNow;
            IsLoading = false;
            Tick();
        }
    }

    private static async Task<(List<CalendarBoost> Rows, TimeSpan Skew)> FetchAsync()
    {
        (List<CalendarEvent> events, TimeSpan skew) = await AqwCalendar.FetchAsync(Http);
        return (events.Select(e => new CalendarBoost(e.Title, e.Url, e.Day, e.Hours)).ToList(), skew);
    }

    /// <summary>The boosts of the calendar page: every entry whose title says Boost.</summary>
    public static List<CalendarBoost> ParseBoosts(string html) =>
        AqwCalendar.ParseBoosts(html).Select(e => new CalendarBoost(e.Title, e.Url, e.Day, e.Hours)).ToList();

    /// <summary>How long a boost runs when the page does not say: Double ALL is posted for 72 hours, the other Double boosts for 48.</summary>
    public static int? DefaultHours(string title) => AqwCalendar.DefaultHours(title);

    /// <summary>Reads "72 hour" or "available for a week" out of an event page's description.</summary>
    public static int? HoursFromDescription(string text) => AqwCalendar.HoursFromDescription(text);

    /// <summary>A description that names another Artix game: that boost is not an AQW one.</summary>
    public static bool IsOtherGame(string text) => AqwCalendar.IsOtherGame(text);

    /// <summary>The title without the date the site puts at its end ("Double ALL Boost 10.30.26" becomes "Double ALL Boost").</summary>
    public static string PlainTitle(string title) => AqwCalendar.PlainTitle(title);
}

/// <summary>One boost of the list.</summary>
public sealed partial class CalendarBoost : ObservableObject
{
    public CalendarBoost(string title, string url, DateTime day, int? hours)
    {
        Title = title;
        Url = url;
        Day = day;
        Hours = hours;
    }

    public string Title { get; }
    public string Url { get; }
    public DateTime Day { get; }
    public int? Hours { get; set; }
    public string Name => CalendarViewModel.PlainTitle(Title);

    public string Detail
    {
        get
        {
            string length = Hours is int h ? (h % 24 == 0 ? $"{h / 24} d  ·  " : $"{h} h  ·  ") : string.Empty;
            return $"{length}starts {Day.ToString("ddd d MMM", CultureInfo.InvariantCulture)}";
        }
    }

    [ObservableProperty]
    private bool _active;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>How far through its run an active boost is (0 to 1).</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>An active boost with under 12 hours left.</summary>
    [ObservableProperty]
    private bool _endingSoon;
}
