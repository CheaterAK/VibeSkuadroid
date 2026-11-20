namespace Skua.App.Droid.Core.GameView;

/// <summary>The one request the app itself makes: the live server list, for the socket bridge's allowlist.</summary>
public static class GameApi
{
    public const string ServerListUrl = "https://game.aq.com/game/api/data/servers";

    // game.aq.com answers requests without a browser User-Agent (and Referer) with HTTP 403 (checked 2026-11-17).
    public const string UserAgent = "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Mobile Safari/537.36";
    public const string Referer = "https://game.aq.com/game/gamefiles/";

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
