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

    /// <summary>
    /// Performs one app-fetched request (<see cref="RequestAction.AppFetch"/>): GET, browser User-Agent, the game's
    /// site as Referer, no cookies, at most <see cref="MaxFetchBytes"/> of the answer. Never throws for HTTP or network
    /// failures (502 then).
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
            await using Stream body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] buffer = new byte[MaxFetchBytes];
            int total = 0, n;
            while (total < buffer.Length && (n = await body.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
                total += n;
            string type = res.Content.Headers.ContentType?.MediaType ?? "text/plain";
            return ((int)res.StatusCode, type, buffer[..total]);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or ObjectDisposedException)
        {
            return (502, "text/plain", []);
        }
    }

    public static async Task<ServerAllowlist> FetchServerListAsync(HttpClient http, CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, ServerListUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.Referrer = new Uri(Referer);
        using HttpResponseMessage res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ServerAllowlist.Parse(json, out _);
    }
}
