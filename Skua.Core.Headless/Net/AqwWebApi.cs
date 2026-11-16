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
    /// Sent with every request. The default mimics a browser-hosted client; which headers the endpoints
    /// actually require has not been confirmed from this environment (the host was unreachable).
    /// </summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    /// <summary>Referer header; the Flash client's requests come from the gamefiles folder.</summary>
    public string? Referer { get; set; } = "https://game.aq.com/game/gamefiles/";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Upper bound for a downloaded game SWF (client version discovery).</summary>
    public long MaxGameFileBytes { get; set; } = 64L * 1024 * 1024;
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
/// <c>api/data/gameversion</c> and <c>gamefiles/</c>.
/// </summary>
public sealed class AqwWebApi : IDisposable
{
    private readonly HttpClient _http;

    public AqwWebApi(AqwWebApiOptions? options = null, HttpMessageHandler? handler = null)
    {
        Options = options ?? new AqwWebApiOptions();
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
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
        string path = "gamefiles/" + relativePath.TrimStart('/');
        using HttpRequestMessage request = NewRequest(HttpMethod.Get, path);
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AqwLoginException(LoginFailureKind.Http, $"Game file request failed: {Describe(e)}", inner: e);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string relative)
    {
        HttpRequestMessage request = new(method, new Uri(Options.BaseUri, relative));
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
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
            throw new AqwLoginException(LoginFailureKind.Http, $"The {what} request returned HTTP {(int)status} with a non-JSON body: {head}");
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
