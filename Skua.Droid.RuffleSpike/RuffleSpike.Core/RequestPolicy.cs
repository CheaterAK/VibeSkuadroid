namespace RuffleSpike.Core;

/// <summary>What the WebView does with one request.</summary>
public enum RequestAction
{
    /// <summary>Serve a file bundled with the app (<see cref="RequestDecision.LocalPath"/>).</summary>
    ServeLocal,
    /// <summary>Let the WebView load it from the network itself (the game's own files and API on game.aq.com).</summary>
    PassThrough,
    /// <summary>Answer with an empty 403 and record it.</summary>
    Block,
}

public readonly record struct RequestDecision(RequestAction Action, string? LocalPath = null, string? Reason = null);

/// <summary>
/// Decides every request the game view's WebView makes (WebViewClient.shouldInterceptRequest).
///
/// The spike page is served under the game's own origin, at https://game.aq.com/__vibeskua__/ (answered by the
/// app, never sent to the network), so everything the game loads from game.aq.com is same-origin: no CORS headers
/// are needed (game.aq.com sends none), the Referer is the game's site, and POSTs (the login) keep their bodies
/// (shouldInterceptRequest never sees request bodies, so the app could not forward them itself).
///
/// Allowed: https://game.aq.com/__vibeskua__/{bundled file} (local) and https://game.aq.com/game/... (network).
/// Everything else is blocked: other paths on game.aq.com, every other host, plain http, other ports.
/// WebSockets never reach shouldInterceptRequest; the page only opens the loopback bridge (see SocketBridge).
/// </summary>
public static class RequestPolicy
{
    public const string GameHost = "game.aq.com";
    public const string LocalPrefix = "/__vibeskua__/";
    public const string GamePrefix = "/game/";
    public const string PageUrl = "https://" + GameHost + LocalPrefix + "index.html";

    public static RequestDecision Decide(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return new(RequestAction.Block, Reason: "unparsable url");
        // data: and blob: URLs never leave the WebView; the page itself creates them (Ruffle does for workers/audio).
        if (uri.Scheme is "data" or "blob")
            return new(RequestAction.PassThrough);
        if (uri.Scheme != Uri.UriSchemeHttps)
            return new(RequestAction.Block, Reason: $"scheme {uri.Scheme}");
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
