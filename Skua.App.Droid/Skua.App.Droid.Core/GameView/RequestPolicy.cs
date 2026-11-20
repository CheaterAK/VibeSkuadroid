namespace Skua.App.Droid.Core.GameView;

/// <summary>What the WebView does with one request.</summary>
public enum RequestAction
{
    /// <summary>Serve a file bundled with the app (<see cref="RequestDecision.LocalPath"/>).</summary>
    ServeLocal,
    /// <summary>Let the WebView load it from the network itself (the game's own files and API on game.aq.com).</summary>
    PassThrough,
    /// <summary>Answer with an empty 403 and record it.</summary>
    Block,
    /// <summary>
    /// One of the few cross-origin requests the client makes by itself (<see cref="RequestPolicy.ExtraRequests"/>): the
    /// app performs it (GET only, no cookies) and answers with CORS headers for the page's origin only.
    /// </summary>
    AppFetch,
}

public readonly record struct RequestDecision(RequestAction Action, string? LocalPath = null, string? Reason = null, Uri? FetchUri = null);

/// <summary>
/// Decides every request the game view's WebView makes (WebViewClient.shouldInterceptRequest).
///
/// The game view page is served under the game's own origin, at https://game.aq.com/__vibeskua__/ (answered by the
/// app, never sent to the network), so everything the game loads from game.aq.com is same-origin: no CORS headers
/// are needed (game.aq.com sends none), the Referer is the game's site, and POSTs (the login) keep their bodies
/// (shouldInterceptRequest never sees request bodies, so the app could not forward them itself).
///
/// Allowed: https://game.aq.com/__vibeskua__/{bundled file} (local), https://game.aq.com/game/... (network), and
/// the exact cross-origin requests in <see cref="ExtraRequests"/> (fetched by the app). Everything else is blocked:
/// other paths on game.aq.com, every other host, plain http, other ports.
/// WebSockets never reach shouldInterceptRequest; the page tunnels game sockets through the private channel (see ChannelBridge).
/// </summary>
public static class RequestPolicy
{
    public const string GameHost = "game.aq.com";
    public const string LocalPrefix = "/__vibeskua__/";
    public const string GamePrefix = "/game/";
    public const string PageUrl = "https://" + GameHost + LocalPrefix + "index.html";

    /// <summary>The page's origin: the only origin the app's CORS answers allow.</summary>
    public const string PageOrigin = "https://" + GameHost;

    /// <summary>
    /// The requests the game client makes outside https://game.aq.com/game/ (searched in the decompiled client,
    /// 2026-11: every URLRequest/URLLoader/Loader with an absolute URL). Everything relative goes to
    /// params.sURL = https://game.aq.com/game/ (skua.swf sets it), so this list is all the live game needs:
    /// <list type="bullet">
    /// <item>GET https://www.aq.com/game/quest.asp?userid=N&amp;qvalue=N: ExternalCalls.logQuestProgress, the client's
    /// funnel tracking (Game.showTracking("1".."7"): login screen shown, server list, joined, ...), and showIt("signup").
    /// The spike blocked it (cross-origin, host not allowed).</item>
    /// </list>
    /// Not allowed (not needed by the game, or opened in a browser with navigateToURL, which Ruffle's openUrlMode
    /// "deny" refuses anyway): www.aq.com/gamedesignnotes/ (test client only), content.aq.com (only when the client
    /// runs from a file), graph.facebook.com and the Facebook policy file (Facebook login), account.aq.com, the wiki,
    /// SmartFox's HTTP upload and BlueBox (unused).
    /// </summary>
    public static readonly IReadOnlyList<string> ExtraRequests = ["https://www.aq.com/game/quest.asp?userid={digits}&qvalue={digits}"];

    public const string SiteHost = "www.aq.com";
    public const string QuestTrackingPath = "/game/quest.asp";

    public static RequestDecision Decide(string url) => Decide(url, "GET");

    /// <param name="url">The request's URL.</param>
    /// <param name="method">Its HTTP method (shouldInterceptRequest reports it); only matters for the app-fetched requests.</param>
    public static RequestDecision Decide(string url, string method)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return new(RequestAction.Block, Reason: "unparsable url");
        // data: and blob: URLs never leave the WebView; the page itself creates them (Ruffle does for workers/audio).
        if (uri.Scheme is "data" or "blob")
            return new(RequestAction.PassThrough);
        if (uri.Scheme != Uri.UriSchemeHttps)
            return new(RequestAction.Block, Reason: $"scheme {uri.Scheme}");
        if (string.Equals(uri.IdnHost, SiteHost, StringComparison.OrdinalIgnoreCase))
            return DecideSite(uri, method);
        if (!string.Equals(uri.IdnHost, GameHost, StringComparison.OrdinalIgnoreCase))
            return new(RequestAction.Block, Reason: "host not allowed");
        if (!uri.IsDefaultPort)
            return new(RequestAction.Block, Reason: "port not allowed");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return new(RequestAction.Block, Reason: "user info in url");

        string path = uri.AbsolutePath; // still percent-encoded
        if (path.StartsWith(LocalPrefix, StringComparison.Ordinal))
        {
            string? local = LocalAssetPath(path[LocalPrefix.Length..]);
            return local is null
                ? new(RequestAction.Block, Reason: "bad local path")
                : new(RequestAction.ServeLocal, LocalPath: local);
        }
        if (path.StartsWith(GamePrefix, StringComparison.Ordinal))
            return new(RequestAction.PassThrough);
        return new(RequestAction.Block, Reason: "path not allowed");
    }

    private static RequestDecision DecideSite(Uri uri, string method)
    {
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || uri.Fragment.Length > 0)
            return new(RequestAction.Block, Reason: "host not allowed");
        if (!string.Equals(uri.AbsolutePath, QuestTrackingPath, StringComparison.Ordinal) || !IsQuestTrackingQuery(uri.Query))
            return new(RequestAction.Block, Reason: "host not allowed");
        if (method is not ("GET" or "OPTIONS"))
            return new(RequestAction.Block, Reason: "method not allowed");
        // Rebuilt from the checked parts: nothing else of the page's URL reaches the network.
        return new(RequestAction.AppFetch, FetchUri: new Uri("https://" + SiteHost + QuestTrackingPath + uri.Query));
    }

    /// <summary>Exactly "?userid=DIGITS&amp;qvalue=DIGITS" (the client's order; userid up to 12 digits, qvalue up to 3).</summary>
    internal static bool IsQuestTrackingQuery(string query)
    {
        if (!query.StartsWith("?userid=", StringComparison.Ordinal))
            return false;
        string[] parts = query[1..].Split('&');
        if (parts.Length != 2 || !parts[1].StartsWith("qvalue=", StringComparison.Ordinal))
            return false;
        string user = parts[0]["userid=".Length..], value = parts[1]["qvalue=".Length..];
        return user.Length is > 0 and <= 12 && user.All(char.IsAsciiDigit) && value.Length is > 0 and <= 3 && value.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// A bundled file's path relative to the web root, or null. Only plain names are accepted: segments of
    /// letters, digits, '.', '_' and '-', no "." or ".." segments, no encoding, no empty segments.
    /// </summary>
    public static string? LocalAssetPath(string relative)
    {
        if (relative.Length == 0) return "index.html";
        if (relative.Length > 200) return null;
        foreach (char c in relative)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' or '/';
            if (!ok) return null;
        }
        string[] segments = relative.Split('/');
        foreach (string s in segments)
            if (s.Length == 0 || s == "." || s == "..") return null;
        return relative;
    }

    /// <summary>The MIME type for a bundled file.</summary>
    public static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".js" => "text/javascript",
        ".json" => "application/json",
        ".wasm" => "application/wasm",
        ".swf" => "application/x-shockwave-flash",
        ".md" or "" => "text/plain",
        _ => "application/octet-stream",
    };

    /// <summary>A short, secret-free key for counting blocked requests in the report (scheme://host/first-segment).</summary>
    public static string BlockKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return "(unparsable)";
        if (uri.Scheme is not ("http" or "https" or "ws" or "wss")) return uri.Scheme + ":";
        string first = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (first.Length > 40) first = first[..40];
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}/{first}";
    }
}
