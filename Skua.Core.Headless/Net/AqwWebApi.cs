using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Net;

/// <summary>Settings for <see cref="AqwWebApi"/>.</summary>
public sealed class AqwWebApiOptions
{
    /// <summary>Root of the game's web API and files (the AS3 loader uses <c>https://game.aq.com/game/</c>).</summary>
    public Uri BaseUri { get; set; } = new("https://game.aq.com/game/");

    /// <summary>
    /// Sent with every request. Required: checked live on 2026-11-17, a login POST without a browser User-Agent
    /// (and Referer) gets HTTP 403 with an HTML page; with these defaults login, the server list and game files work.
    /// </summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    /// <summary>Referer header; the Flash client's requests come from the gamefiles folder.</summary>
    public string? Referer { get; set; } = "https://game.aq.com/game/gamefiles/";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Whole-download limit for a game file (headers and body).</summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Redirects followed for GET requests (only to https URLs on the same host); a POST is never redirected.</summary>
    public int MaxRedirects { get; set; } = 3;

    /// <summary>Upper bound for a downloaded game SWF (client version discovery).</summary>
    public long MaxGameFileBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Upper bound for the <c>api/char/bank</c> reply (the whole bank as JSON). Accounts with expanded bank space hold
    /// more than <c>iMaxBankSlots</c>: 3000 items of ~1.2 KB measured-shape JSON are 3.5 MB, long descriptions can
    /// triple that, so 5000 items stay well inside 32 MB. Nothing else limits the item count.
    /// </summary>
    public long MaxBankBytes { get; set; } = 32L * 1024 * 1024;
}

/// <summary>A bank the <c>api/char/bank</c> call returned.</summary>
/// <param name="Items">The bank items as the server sent them (the game's <c>JSON.parse(data) as Array</c>).</param>
/// <param name="HttpStatus">The reply's HTTP status.</param>
/// <param name="BodyBytes">The reply's size in bytes.</param>
/// <param name="Elapsed">From sending the request to the parsed reply.</param>
/// <param name="Shape">"array" (the game's own shape), or "object.items" when the items came wrapped in an object.</param>
public sealed record BankReply(JsonArray Items, int HttpStatus, long BodyBytes, TimeSpan Elapsed, string Shape);

/// <summary>The <c>api/char/bank</c> call failed. The message never carries the login token or the reply's body.</summary>
public sealed class AqwBankException : Exception
{
    public AqwBankException(string message, int? httpStatus = null, long? bodyBytes = null, TimeSpan? elapsed = null)
        : base(message)
    {
        HttpStatus = httpStatus;
        BodyBytes = bodyBytes;
        Elapsed = elapsed;
    }

    /// <summary>The reply's HTTP status, when one arrived.</summary>
    public int? HttpStatus { get; }

    /// <summary>The reply's size in bytes, when one arrived.</summary>
    public long? BodyBytes { get; }

    public TimeSpan? Elapsed { get; }
}

/// <summary>What the HTTP login returns: the socket token and the server list.</summary>
public sealed class LoginResult
{
    internal LoginResult(string token, string username, int userId, int level, int upgradeDays, IReadOnlyList<GameServer> servers, JsonObject raw)
    {
        Token = token;
        Username = username;
        UserId = userId;
        Level = level;
        UpgradeDays = upgradeDays;
        Servers = servers;
        Raw = raw;
    }

    /// <summary>The <c>sToken</c> used as the SmartFox login password. Never logged by this library.</summary>
    public string Token { get; }

    /// <summary>The account name as the server spells it (<c>unm</c>).</summary>
    public string Username { get; }

    public int UserId { get; }

    public int Level { get; }

    /// <summary><c>iUpgDays</c>: negative for a free account.</summary>
    public int UpgradeDays { get; }

    public bool IsMember => UpgradeDays >= 0;

    public IReadOnlyList<GameServer> Servers { get; }

    /// <summary>The <c>login</c> object without its token.</summary>
    public JsonObject Raw { get; }

    /// <summary>
    /// Which web login of its <see cref="AqwSession"/> this is (1, 2, ...; 0 outside a session). Each web login gives
    /// a new token and the server only accepts the latest one.
    /// </summary>
    public long Generation { get; internal set; }

    /// <summary>When the reply arrived (set by <see cref="AqwSession"/>).</summary>
    public DateTimeOffset ObtainedAt { get; internal set; }

    /// <summary>A socket login has used (or is using) this token.</summary>
    public bool UsedBySocket { get; internal set; }

    public override string ToString()
    {
        return $"LoginResult({Username}, level {Level}, {Servers.Count} servers, token <redacted>)";
    }
}

/// <summary>The <c>api/data/gameversion</c> reply.</summary>
public sealed record GameVersionInfo(string? GameFile, string? Title, JsonObject Raw);

/// <summary>Why a login failed.</summary>
public enum LoginFailureKind
{
    /// <summary>The HTTP request itself failed (DNS, proxy, TLS, timeout, non-JSON reply).</summary>
    Http,

    /// <summary>The web login refused the credentials (or the account is locked).</summary>
    Rejected,

    /// <summary>The socket was refused at verChk (<c>apiKO</c>).</summary>
    ApiVersionRejected,

    /// <summary>The game server refused the client version and named the one it requires.</summary>
    ClientVersionRejected,

    /// <summary>The game server refused the socket login for another reason (<c>logKO</c> or loginResponse false).</summary>
    SocketRejected,

    /// <summary>The character did not finish loading in time.</summary>
    Timeout,

    /// <summary>The socket closed or failed during login.</summary>
    ConnectionLost,

    /// <summary>No usable server.</summary>
    NoServer,
}

public sealed class AqwLoginException : Exception
{
    public AqwLoginException(LoginFailureKind kind, string message, string? requiredClientVersion = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        RequiredClientVersion = requiredClientVersion;
    }

    public LoginFailureKind Kind { get; }

    /// <summary>For <see cref="LoginFailureKind.ClientVersionRejected"/>: the version the server asked for.</summary>
    public string? RequiredClientVersion { get; }
}

/// <summary>
/// The game's HTTP endpoints: <c>api/login/now</c> (token and server list), <c>api/data/servers</c>,
/// <c>api/data/gameversion</c>, <c>api/char/bank</c> and <c>gamefiles/</c>.
/// </summary>
public sealed class AqwWebApi : IDisposable
{
    /// <summary>The only host the login token is ever sent to over HTTP (and only over HTTPS).</summary>
    public const string TokenHost = "game.aq.com";

    private readonly HttpClient _http;

    public AqwWebApi(AqwWebApiOptions? options = null, HttpMessageHandler? handler = null)
    {
        Options = options ?? new AqwWebApiOptions();
        // Redirects are handled here, not by the handler: the login POST carries the password and must never be
        // re-sent to whatever host a 307/308 names.
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Options.Timeout;
    }

    public AqwWebApiOptions Options { get; }

    /// <summary>Logs in over HTTP: <c>POST api/login/now</c> with <c>user</c>, <c>pass</c>, <c>option=1</c>.</summary>
    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        string ran = "0." + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);
        using HttpRequestMessage request = NewRequest(HttpMethod.Post, $"api/login/now?ran={ran}");
        request.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("user", username),
            new KeyValuePair<string, string>("pass", password),
            new KeyValuePair<string, string>("option", "1"),
        });

        JsonObject root = await SendForObjectAsync(request, "login", cancellationToken).ConfigureAwait(false);
        JsonObject login = root["login"] as JsonObject ?? root;
        if (!JsonUtil.AsBool(login["bSuccess"]))
        {
            string why = JsonUtil.AsString(login["sMsg"]) ?? JsonUtil.AsString(login["message"]) ?? "unknown reason";
            throw new AqwLoginException(LoginFailureKind.Rejected, $"Login refused: {why}");
        }

        string? token = JsonUtil.AsString(login["sToken"]);
        if (string.IsNullOrEmpty(token))
            throw new AqwLoginException(LoginFailureKind.Rejected, "Login reply carried no sToken.");

        IReadOnlyList<GameServer> servers = GameServer.ListFromJson(root["servers"] ?? login["servers"]);
        JsonObject raw = (JsonObject)login.DeepClone();
        raw.Remove("sToken");
        return new LoginResult(
            token,
            JsonUtil.AsString(login["unm"]) ?? username,
            JsonUtil.AsInt(login["userid"]),
            JsonUtil.AsInt(login["iLevel"], 1),
            JsonUtil.AsInt(login["iUpgDays"], -1),
            servers,
            raw);
    }

    /// <summary>The public server list (no account needed).</summary>
    public async Task<IReadOnlyList<GameServer>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = NewRequest(HttpMethod.Get, "api/data/servers");
        JsonNode node = await SendForNodeAsync(request, "server list", cancellationToken).ConfigureAwait(false);
        return GameServer.ListFromJson(node);
    }

    /// <summary>Which game SWF is live (<c>sFile</c>), as the AS3 loader asks before loading the game.</summary>
    public async Task<GameVersionInfo> GetGameVersionAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = NewRequest(HttpMethod.Get, "api/data/gameversion");
        JsonObject o = await SendForObjectAsync(request, "game version", cancellationToken).ConfigureAwait(false);
        return new GameVersionInfo(JsonUtil.AsString(o["sFile"]), JsonUtil.AsString(o["sTitle"]), o);
    }

    /// <summary>Downloads a file under <c>gamefiles/</c> (bounded by <see cref="AqwWebApiOptions.MaxGameFileBytes"/>).</summary>
    public async Task<byte[]> DownloadGameFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        CancellationToken originalToken = cancellationToken;
        string path = "gamefiles/" + relativePath.TrimStart('/');
        using HttpRequestMessage request = NewRequest(HttpMethod.Get, path);
        try
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Options.DownloadTimeout);
            cancellationToken = limit.Token;
            using HttpResponseMessage response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new AqwLoginException(LoginFailureKind.Http, $"Game file {relativePath} returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is long length && length > Options.MaxGameFileBytes)
                throw new AqwLoginException(LoginFailureKind.Http, $"Game file {relativePath} is {length} bytes, over the limit.");
            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using MemoryStream copy = new();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (copy.Length + read > Options.MaxGameFileBytes)
                    throw new AqwLoginException(LoginFailureKind.Http, $"Game file {relativePath} exceeds the size limit.");
                copy.Write(buffer, 0, read);
            }
            return copy.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !originalToken.IsCancellationRequested)
        {
            throw new AqwLoginException(LoginFailureKind.Http, $"Game file request failed: {Describe(e)}", inner: e);
        }
    }

    /// <summary>
    /// The bank, as the game loads it: Game.getBank, <c>requestAPI("bank", {layout: {cat: "all"}}, ..., true)</c> in
    /// Game4000r9.swf. That is a <c>POST</c> to <c>api/char/bank?v=&lt;Math.random()&gt;</c> under the game path with the
    /// request headers <c>ccid</c> (<c>world.myAvatar.objData.CharID</c>) and <c>token</c> (<c>loginInfo.strToken</c>,
    /// the web login's <c>sToken</c>), and the one form field <c>layout={"cat":"all"}</c> (URLVariables, so
    /// <c>application/x-www-form-urlencoded</c>). The reply is a JSON array of bank items.
    /// </summary>
    /// <remarks>
    /// The token goes only to <see cref="TokenHost"/> over HTTPS (anything else is refused before a request is made);
    /// the POST is never redirected; the reply is bounded by <see cref="AqwWebApiOptions.MaxBankBytes"/>. Neither the
    /// token nor the reply's body appear in an exception message.
    /// </remarks>
    /// <exception cref="AqwBankException">Refused, failed, not HTTP 200, or not a bank.</exception>
    public async Task<BankReply> GetBankAsync(int charId, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (charId <= 0)
            throw new AqwBankException("No character id (objData.CharID) to ask the bank for.");
        Uri uri = new(Options.BaseUri, "api/char/bank?v=" + As3Random());
        if (!IsTokenUri(uri))
            throw new AqwBankException($"Refused to send the login token to {uri.Scheme}://{uri.Authority}: the bank request only goes to https://{TokenHost}.");

        using HttpRequestMessage request = NewRequest(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("ccid", charId.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("token", token);
        request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("layout", """{"cat":"all"}""") });

        long started = Stopwatch.GetTimestamp();
        int status;
        byte[]? body;
        try
        {
            using HttpResponseMessage response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            if (response.Content.Headers.ContentLength is long length && length > Options.MaxBankBytes)
                throw new AqwBankException($"The bank reply is {length} bytes, over the {Options.MaxBankBytes} byte limit.", status, length, Stopwatch.GetElapsedTime(started));
            body = await ReadBoundedAsync(response.Content, Options.MaxBankBytes, cancellationToken).ConfigureAwait(false);
            if (body is null)
                throw new AqwBankException($"The bank reply exceeds the {Options.MaxBankBytes} byte limit.", status, null, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // No inner exception: whatever a handler put in its messages stays out of logs and crash reports.
            throw new AqwBankException($"The bank request to {uri.Host} failed: {Describe(e).Replace(token, "<token>", StringComparison.Ordinal)}", elapsed: Stopwatch.GetElapsedTime(started));
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        if (status != 200)
            throw new AqwBankException($"The bank request returned HTTP {status} ({body.Length} bytes).", status, body.Length, elapsed);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            string kind = body.Length > 0 && body[0] == (byte)'<' ? "an HTML page" : "not JSON";
            throw new AqwBankException($"The bank request returned HTTP {status} with a body that is {kind} ({body.Length} bytes).", status, body.Length, elapsed);
        }

        // Game.onBankComplete: world.addItemsToBank(JSON.parse(data) as Array). Anything but an array is no bank there;
        // an object wrapping the items under "items" is accepted too, and reported as such.
        switch (node)
        {
            case JsonArray array:
                return new BankReply(array, status, body.Length, elapsed, "array");
            case JsonObject o when o["items"] is JsonArray wrapped:
                o.Remove("items");
                return new BankReply(wrapped, status, body.Length, elapsed, "object.items");
            case JsonObject o:
                throw new AqwBankException($"The bank request returned HTTP {status} with a JSON object (keys: {string.Join(", ", o.Select(p => p.Key).Take(12))}) "
                    + $"instead of an item array ({body.Length} bytes).", status, body.Length, elapsed);
            default:
                throw new AqwBankException($"The bank request returned HTTP {status} with JSON {node?.GetValueKind().ToString() ?? "null"} instead of an item array ({body.Length} bytes).",
                    status, body.Length, elapsed);
        }
    }

    /// <summary>True for the only place the login token may go: https://<see cref="TokenHost"/> on the default port.</summary>
    public static bool IsTokenUri(Uri uri)
    {
        return uri.IsAbsoluteUri
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, TokenHost, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort;
    }

    // AS3 Math.random() as the game appends it ("?v=0.8312...").
    private static string As3Random()
    {
        return Random.Shared.NextDouble().ToString("0.0###############", CultureInfo.InvariantCulture);
    }

    // The body, or null when it is longer than max.
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, long max, CancellationToken cancellationToken)
    {
        await using Stream body = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream copy = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > max)
                return null;
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    // GETs follow up to MaxRedirects redirects to https URLs on the same host; anything else (a POST, another host,
    // plain http) is answered with the redirect itself, which callers treat as a failure.
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        for (int hops = 0; hops < Options.MaxRedirects && IsRedirect(response.StatusCode) && request.Method == HttpMethod.Get; hops++)
        {
            Uri? target = response.Headers.Location is Uri location ? new Uri(request.RequestUri!, location) : null;
            if (target is null || target.Scheme != Uri.UriSchemeHttps || !string.Equals(target.Host, request.RequestUri!.Host, StringComparison.OrdinalIgnoreCase))
                return response;
            response.Dispose();
            HttpRequestMessage next = new(HttpMethod.Get, target);
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request = next;
            response = await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }
        return response;
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    private HttpRequestMessage NewRequest(HttpMethod method, string relative)
    {
        return NewRequest(method, new Uri(Options.BaseUri, relative));
    }

    private HttpRequestMessage NewRequest(HttpMethod method, Uri uri)
    {
        HttpRequestMessage request = new(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", Options.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        if (Options.Referer is not null)
            request.Headers.Referrer = new Uri(Options.Referer);
        return request;
    }

    private async Task<JsonObject> SendForObjectAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        JsonNode node = await SendForNodeAsync(request, what, cancellationToken).ConfigureAwait(false);
        return node as JsonObject ?? throw new AqwLoginException(LoginFailureKind.Http, $"The {what} reply is not a JSON object.");
    }

    private async Task<JsonNode> SendForNodeAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        string body;
        HttpStatusCode status;
        try
        {
            using HttpResponseMessage response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AqwLoginException(LoginFailureKind.Http, $"The {what} request to {request.RequestUri?.Host} failed: {Describe(e)}", inner: e);
        }

        try
        {
            return JsonNode.Parse(body) ?? throw new JsonException("empty body");
        }
        catch (JsonException)
        {
            string head = body.Length > 120 ? body[..120] : body;
            // Checked live (2026-11-17): game.aq.com answers a request without a browser User-Agent with HTTP 403 and an HTML page.
            string hint = status == HttpStatusCode.Forbidden && head.TrimStart().StartsWith('<')
                ? " (the site's bot protection refused it; AqwWebApiOptions.UserAgent must look like a browser)"
                : string.Empty;
            throw new AqwLoginException(LoginFailureKind.Http, $"The {what} request returned HTTP {(int)status} with a non-JSON body{hint}: {head}");
        }
    }

    private static string Describe(Exception e)
    {
        List<string> parts = new();
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            string text = x is HttpRequestException { StatusCode: HttpStatusCode code } ? $"{x.Message} (HTTP {(int)code})" : x.Message;
            if (!parts.Contains(text))
                parts.Add(text);
        }
        return string.Join(" -> ", parts);
    }
}
