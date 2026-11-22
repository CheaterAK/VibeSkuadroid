using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Skua.Core.Services;

/// <summary>One boost of the artix.com calendar.</summary>
public sealed class CalendarEvent
{
    public CalendarEvent(string title, string url, DateTime day, int? hours)
    {
        Title = title;
        Url = url;
        Day = day;
        Hours = hours;
    }

    /// <summary>The calendar's title ("Double ALL Boost 10.30.26").</summary>
    public string Title { get; }

    /// <summary>The event page.</summary>
    public string Url { get; }

    /// <summary>The day it starts (server time, US Eastern).</summary>
    public DateTime Day { get; }

    /// <summary>How long it runs in hours (null: one day; 0: not an AQW boost).</summary>
    public int? Hours { get; set; }
}

/// <summary>
/// The Calendar's rules (desktop Calendar window; the Android app's Calendar page uses the same): the daily and weekly
/// resets, and the AQW boost list read from https://www.artix.com/calendar/. Dailies reset at 12 AM server time;
/// weeklies at 12 AM server time when Thursday turns into Friday. AQW's server time is US Eastern (with daylight
/// saving). The game's own "server time" chat line is the PC's clock, so time is the PC clock corrected by the
/// <c>Date</c> header of the artix.com answer. The boosts are read from the script array of the calendar page.
/// </summary>
public static partial class AqwCalendar
{
    /// <summary>The calendar page (HTTPS).</summary>
    public const string CalendarUrl = "https://www.artix.com/calendar/";

    /// <summary>artix.com answers 403 to a request without a User-Agent.</summary>
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) VibeSkuffle";

    /// <summary>US Eastern (with daylight saving), or a fixed -5 h when the system has no such zone.</summary>
    public static TimeZoneInfo Eastern { get; } = FindEastern();

    private static TimeZoneInfo FindEastern()
    {
        foreach (string id in new[] { "Eastern Standard Time", "America/New_York" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("US Eastern", TimeSpan.FromHours(-5), "US Eastern", "US Eastern");
    }

    /// <summary>The UTC moment of a server-time (US Eastern) local time.</summary>
    public static DateTimeOffset ToUtc(DateTime easternLocal) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(easternLocal, DateTimeKind.Unspecified), Eastern), TimeSpan.Zero);

    /// <summary>The server-time date of <paramref name="utc"/>.</summary>
    public static DateTime EasternDate(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Eastern).Date;

    /// <summary>The next daily reset (server midnight) after <paramref name="utc"/>.</summary>
    public static DateTimeOffset NextDaily(DateTimeOffset utc) => ToUtc(EasternDate(utc).AddDays(1));

    /// <summary>The daily reset that last happened.</summary>
    public static DateTimeOffset PrevDaily(DateTimeOffset utc) => ToUtc(EasternDate(utc));

    /// <summary>The next weekly reset (server midnight at the start of Friday) after <paramref name="utc"/>.</summary>
    public static DateTimeOffset NextWeekly(DateTimeOffset utc)
    {
        DateTime day = EasternDate(utc).AddDays(1);
        while (day.DayOfWeek != DayOfWeek.Friday)
            day = day.AddDays(1);
        return ToUtc(day);
    }

    /// <summary>The weekly reset that last happened.</summary>
    public static DateTimeOffset PrevWeekly(DateTimeOffset utc)
    {
        DateTime day = EasternDate(utc);
        while (day.DayOfWeek != DayOfWeek.Friday)
            day = day.AddDays(-1);
        return ToUtc(day);
    }

    /// <summary>"6d 3h 12m", "3h 12m 05s", "12m 05s", "5s".</summary>
    public static string FormatSpan(TimeSpan span)
    {
        long s = Math.Max(0, (long)span.TotalSeconds);
        long d = s / 86400, h = s / 3600 % 24, m = s / 60 % 60, sec = s % 60;
        if (d > 0) return $"{d}d {h}h {m:00}m";
        if (h > 0) return $"{h}h {m:00}m {sec:00}s";
        if (m > 0) return $"{m}m {sec:00}s";
        return $"{sec}s";
    }

    /// <summary>The same without seconds.</summary>
    public static string FormatShort(TimeSpan span)
    {
        long s = Math.Max(0, (long)span.TotalSeconds);
        long d = s / 86400, h = s / 3600 % 24, m = s / 60 % 60;
        if (d > 0) return $"{d}d {h}h";
        if (h > 0) return $"{h}h {m:00}m";
        return $"{m}m";
    }

    /// <summary>How far <paramref name="now"/> is from <paramref name="prev"/> to <paramref name="next"/> (0 to 1).</summary>
    public static double Fraction(DateTimeOffset prev, DateTimeOffset next, DateTimeOffset now) =>
        Math.Clamp((now - prev).TotalSeconds / Math.Max(1, (next - prev).TotalSeconds), 0, 1);

    /// <summary>The boosts of the calendar page: every entry whose title says Boost, by day.</summary>
    public static List<CalendarEvent> ParseBoosts(string html)
    {
        List<CalendarEvent> rows = new();
        foreach (Match m in EventRegex().Matches(html))
        {
            string title = UnescapeTitle(m.Groups["t"].Value);
            if (!title.Contains("boost", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!DateTime.TryParseExact(m.Groups["s"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
                continue;
            if (rows.Any(r => string.Equals(r.Title, title, StringComparison.Ordinal) && r.Day == day))
                continue;
            rows.Add(new CalendarEvent(title, m.Groups["u"].Value, day, DefaultHours(title)));
        }
        rows.Sort((a, b) => a.Day.CompareTo(b.Day));
        return rows;
    }

    /// <summary>
    /// A title as the page's script writes it, its escapes undone (<see cref="Regex.Unescape"/>, as before); a title
    /// with an escape Regex.Unescape refuses (<c>\q</c>, <c>\x4</c>) is kept as it is instead of failing the whole
    /// calendar.
    /// </summary>
    public static string UnescapeTitle(string raw)
    {
        try
        {
            return Regex.Unescape(raw);
        }
        catch (ArgumentException)
        {
            return raw;
        }
    }

    /// <summary>The most read of the calendar page (it is a few hundred KB).</summary>
    public const int MaxCalendarBytes = 8 * 1024 * 1024;

    /// <summary>The most read of one event page.</summary>
    public const int MaxEventPageBytes = 2 * 1024 * 1024;

    /// <summary>The body as text (its charset, else UTF-8), at most <paramref name="max"/> bytes; <see cref="InvalidDataException"/> beyond.</summary>
    public static async Task<string> ReadTextAsync(HttpContent content, int max, CancellationToken cancellationToken = default)
    {
        if (content.Headers.ContentLength is long length && length > max)
            throw new InvalidDataException($"the page is larger than {max / 1024} KB");
        using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream copy = new();
        byte[] buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + n > max)
                throw new InvalidDataException($"the page is larger than {max / 1024} KB");
            copy.Write(buffer, 0, n);
        }
        System.Text.Encoding encoding = System.Text.Encoding.UTF8;
        if (content.Headers.ContentType?.CharSet is string charset)
        {
            try
            {
                encoding = System.Text.Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
            }
        }
        return encoding.GetString(copy.GetBuffer(), 0, (int)copy.Length);
    }

    /// <summary>How long a boost runs when the page does not say: Double ALL is posted for 72 hours, the other Double boosts for 48.</summary>
    public static int? DefaultHours(string title)
    {
        if (title.StartsWith("double all", StringComparison.OrdinalIgnoreCase)) return 72;
        if (title.StartsWith("double ", StringComparison.OrdinalIgnoreCase)) return 48;
        return null;
    }

    /// <summary>Reads "72 hour" or "available for a week" out of an event page's description.</summary>
    public static int? HoursFromDescription(string text)
    {
        Match m = Regex.Match(text, @"(\d+)\s*hour", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int hours))
            return hours;
        if (text.Contains("available for a week", StringComparison.OrdinalIgnoreCase) || text.Contains("for one week", StringComparison.OrdinalIgnoreCase))
            return 168;
        return null;
    }

    /// <summary>A description that names another Artix game: that boost is not an AQW one.</summary>
    public static bool IsOtherGame(string text)
    {
        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);
        return !Has("aqworlds") && !Has("adventurequest worlds") && !Has("artix entertainment develops")
            && new[] { "adventurequest 3d", "aq3d", "dragonfable", "mechquest", "epic", "battleon" }.Any(Has);
    }

    /// <summary>The title without the date the site puts at its end ("Double ALL Boost 10.30.26" becomes "Double ALL Boost").</summary>
    public static string PlainTitle(string title) => TrailingDate().Replace(title, "");

    /// <summary>An event page's meta description (HTML decoded), or null.</summary>
    public static string? Description(string page)
    {
        Match meta = DescriptionRegex().Match(page);
        return meta.Success ? WebUtility.HtmlDecode(meta.Groups[1].Value) : null;
    }

    /// <summary>An https:// page on artix.com or www.artix.com (the only event pages the Android app reads).</summary>
    public static bool IsArtixHttps(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && (string.Equals(uri.Host, "www.artix.com", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Host, "artix.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the calendar page and, for the boosts that began in the last ten days (or begin today), their event pages
    /// (how long they run, which game they are for); returns the boosts that are AQW's and the clock skew of the
    /// server's <c>Date</c> header. <paramref name="allowPage"/> limits which event pages are read (null: all).
    /// </summary>
    /// <exception cref="InvalidOperationException">The page could not be read or had no boosts (message fit for the user).</exception>
    public static async Task<(List<CalendarEvent> Rows, TimeSpan Skew)> FetchAsync(HttpClient http, Func<Uri, bool>? allowPage = null, CancellationToken cancellationToken = default)
    {
        TimeSpan skew = TimeSpan.Zero;
        string html;
        try
        {
            using HttpResponseMessage response = await http.GetAsync(CalendarUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Headers.Date is DateTimeOffset date)
                skew = date - DateTimeOffset.UtcNow;
            html = await ReadTextAsync(response.Content, MaxCalendarBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Could not reach artix.com ({ex.Message})");
        }
        List<CalendarEvent> rows = ParseBoosts(html);
        if (rows.Count == 0)
            throw new InvalidOperationException("The calendar page had no boosts (its layout may have changed)");
        DateTime today = EasternDate(DateTimeOffset.UtcNow + skew);
        foreach (CalendarEvent row in rows.Where(r => r.Day <= today.AddDays(1) && r.Day >= today.AddDays(-10)))
        {
            if (allowPage is not null && (!Uri.TryCreate(row.Url, UriKind.Absolute, out Uri? uri) || !allowPage(uri)))
                continue;
            try
            {
                using HttpResponseMessage response = await http.GetAsync(row.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string page = await ReadTextAsync(response.Content, MaxEventPageBytes, cancellationToken).ConfigureAwait(false);
                if (Description(page) is not string text)
                    continue;
                if (IsOtherGame(text))
                    row.Hours = 0;
                else if (HoursFromDescription(text) is int hours)
                    row.Hours = hours;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The default length stays.
            }
        }
        rows.RemoveAll(r => r.Hours == 0);
        return (rows, skew);
    }

    [GeneratedRegex(@"\{title: '(?<t>[^']*)',\s*url: '(?<u>[^']*)'(?:\s*\+\s*'[^']*')?\s*,\s*start: '(?<s>\d{4}-\d{2}-\d{2})'")]
    private static partial Regex EventRegex();

    [GeneratedRegex("name=\"description\"[^>]*?content=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionRegex();

    [GeneratedRegex(@"\s\d+\.\d+\.\d+$")]
    private static partial Regex TrailingDate();
}
