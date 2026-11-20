using Skua.Core.Services;

namespace Skua.App.Droid.Core.Calendar;

/// <summary>Where the Calendar page reads the boosts from.</summary>
public interface ICalendarSource
{
    /// <summary>The boosts that are AQW's and the clock skew of the source's <c>Date</c> header.</summary>
    /// <exception cref="InvalidOperationException">The calendar could not be read (message fit for the user).</exception>
    Task<(IReadOnlyList<CalendarEvent> Events, TimeSpan Skew)> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The desktop Calendar's source: https://www.artix.com/calendar/ and, for recent boosts, their event pages, through
/// Skua's <see cref="AqwCalendar.FetchAsync"/>. HTTPS only: event pages are read only when they are https:// on
/// artix.com or www.artix.com (<see cref="AqwCalendar.IsArtixHttps"/>); others keep the default length.
/// </summary>
public sealed class ArtixCalendarSource : ICalendarSource
{
    private readonly HttpClient _http;

    /// <param name="handler">The HTTP handler (tests); null for the platform's (on Android, its network security policy applies).</param>
    public ArtixCalendarSource(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(12);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AqwCalendar.UserAgent);
    }

    public async Task<(IReadOnlyList<CalendarEvent> Events, TimeSpan Skew)> FetchAsync(CancellationToken cancellationToken)
    {
        (List<CalendarEvent> rows, TimeSpan skew) = await AqwCalendar.FetchAsync(_http, AqwCalendar.IsArtixHttps, cancellationToken).ConfigureAwait(false);
        return (rows, skew);
    }
}
