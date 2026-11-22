namespace Skua.App.Droid.Core.GameView;

/// <summary>The requests the game view makes itself: the live server list (the socket bridge's allowlist) and the app-fetched ones.</summary>
public static class GameApi
{
    public const string ServerListUrl = "https://game.aq.com/game/api/data/servers";

    // game.aq.com answers requests without a browser User-Agent (and Referer) with HTTP 403 (checked 2026-11-17).
    public const string UserAgent = "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Mobile Safari/537.36";
    public const string Referer = "https://game.aq.com/game/gamefiles/";

    /// <summary>The most the app reads of an app-fetched response (quest tracking answers a few bytes).</summary>
    public const int MaxFetchBytes = 64 * 1024;

    /// <summary>The most the app reads of the server list (about 2 KB in 2026-11).</summary>
    public const int MaxServerListBytes = 1024 * 1024;

    /// <summary>
    /// The handler of the game view's own requests: redirects are never followed (an app-fetched request is answered
    /// to the page with CORS headers, so a followed redirect would hand the page the body of whatever URL it named).
    /// </summary>
    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false };

    /// <summary>The game view's client for <see cref="FetchAllowedAsync"/> and <see cref="FetchServerListAsync"/>.</summary>
    public static HttpClient CreateClient() => new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Performs one app-fetched request (<see cref="RequestAction.AppFetch"/>): GET, browser User-Agent, the game's
    /// site as Referer, no cookies, at most <see cref="MaxFetchBytes"/> of the answer. Never throws for HTTP or network
    /// failures (502 then). A redirect is not followed and answered with 502, as is an answer for any other URL (from a
    /// handler that followed one anyway).
    /// </summary>
    public static async Task<(int Status, string ContentType, byte[] Body)> FetchAllowedAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        if (RequestPolicy.Decide(uri.AbsoluteUri).Action != RequestAction.AppFetch)
            throw new ArgumentException("Not an app-fetched request.", nameof(uri));
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.Referrer = new Uri(Referer);
            using HttpResponseMessage res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int status = (int)res.StatusCode;
            if (status is >= 300 and < 400 || (res.RequestMessage?.RequestUri is { } answered && answered != uri))
                return (502, "text/plain", []);
            await using Stream body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] bytes = await ReadCappedAsync(body, MaxFetchBytes, truncate: true, ct).ConfigureAwait(false);
            string type = res.Content.Headers.ContentType?.MediaType ?? "text/plain";
            return (status, type, bytes);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or ObjectDisposedException)
        {
            return (502, "text/plain", []);
        }
    }

    /// <exception cref="HttpRequestException">Not answered with success, or the answer is larger than <see cref="MaxServerListBytes"/>.</exception>
    public static async Task<ServerAllowlist> FetchServerListAsync(HttpClient http, CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, ServerListUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.Referrer = new Uri(Referer);
        using HttpResponseMessage res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using Stream body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] bytes;
        try
        {
            bytes = await ReadCappedAsync(body, MaxServerListBytes, truncate: false, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException e)
        {
            throw new HttpRequestException("Server list: " + e.Message);
        }
        return ServerAllowlist.Parse(System.Text.Encoding.UTF8.GetString(bytes), out _);
    }

    /// <summary>
    /// Reads at most <paramref name="max"/> bytes of <paramref name="body"/>: the first <paramref name="max"/> when
    /// <paramref name="truncate"/>, else <see cref="InvalidDataException"/> when there is more.
    /// </summary>
    public static async Task<byte[]> ReadCappedAsync(Stream body, int max, bool truncate, CancellationToken ct)
    {
        byte[] buffer = new byte[Math.Min(max, 81920)];
        int total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                if (buffer.Length >= max)
                {
                    if (truncate)
                        break;
                    byte[] probe = new byte[1]; // one more byte says whether there is more
                    if (await body.ReadAsync(probe, ct).ConfigureAwait(false) > 0)
                        throw new InvalidDataException($"the answer is larger than {max / 1024} KB");
                    break;
                }
                Array.Resize(ref buffer, (int)Math.Min(max, buffer.Length * 2L));
            }
            int n = await body.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
        }
        return buffer[..total];
    }
}
