using Skua.Core.Headless.Commands;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.World;

namespace Skua.Core.Headless;

/// <summary>Settings for <see cref="AqwSession"/>.</summary>
public sealed class AqwSessionOptions
{
    public AqwWebApiOptions WebApi { get; set; } = new();

    public AqwConnectionOptions Connection { get; set; } = new();

    /// <summary>Forces a client version instead of discovering it.</summary>
    public string? ClientVersion { get; set; }

    /// <summary>Discover the version from the live game SWF before the first connect.</summary>
    public bool DiscoverClientVersion { get; set; } = true;

    /// <summary>How many times to retry a login the server refused with "requires AQW game version X".</summary>
    public int ClientVersionRetries { get; set; } = 2;

    /// <summary>
    /// The client version last seen working on the live servers. Default <see cref="Net.ClientVersion.Fallback"/>
    /// (5.32: another client got in with it on 2026-11-19, while 5.33, the vParam of Game4000r9.swf, was refused).
    /// </summary>
    public string KnownGoodClientVersion { get; set; } = Net.ClientVersion.Fallback;

    /// <summary>
    /// Fallback after the server refuses a socket login for a reason other than "requires version X" (live:
    /// "Character data could not be retrieved. Please Login and try again.") while the version came from the game
    /// file: the next socket login (the caller's own retry, with a fresh web login) sends the known-good version
    /// instead, once. If that works the session keeps it; if it is refused too the discovered version comes back.
    /// Adds no attempt of its own.
    /// </summary>
    public bool FallBackToKnownGoodVersion { get; set; } = true;
}

/// <summary>
/// One character's headless client: HTTP login, server choice, client version discovery, the socket connection,
/// the world model and the command API, with events that stay subscribed across reconnects.
/// </summary>
public sealed class AqwSession : IAsyncDisposable
{
    private readonly AqwWebApi _api;
    private readonly bool _ownsApi;
    private readonly TimeProvider _time;
    private readonly ClientVersionResolver _resolver;
    private readonly object _loginGate = new();
    private AqwCredentials? _credentials;
    private AqwConnection? _connection;
    private Task? _pendingLogin;
    private long _loginsStarted;
    private VersionFallback _fallback;
    private string? _refusedVersion;
    private ClientVersionResult? _workingVersion;
    private ClientVersionResult? _discovered;

    private enum VersionFallback
    {
        /// <summary>Nothing refused.</summary>
        None,

        /// <summary>A refusal with the discovered version: the next socket login uses the known-good one.</summary>
        Armed,

        /// <summary>The next socket login is the fallback attempt.</summary>
        Trying,

        /// <summary>The fallback was refused too: not tried again until a login works.</summary>
        Spent,
    }

    public AqwSession(AqwSessionOptions? options = null, AqwWebApi? api = null, TimeProvider? time = null)
    {
        Options = options ?? new AqwSessionOptions();
        _time = time ?? TimeProvider.System;
        _ownsApi = api is null;
        _api = api ?? new AqwWebApi(Options.WebApi);
        _resolver = new ClientVersionResolver(_api);
        World = new WorldModel(_time);
    }

    public event EventHandler<InboundPacket>? PacketReceived;

    public event EventHandler<ExtensionResponseEventArgs>? ExtensionResponse;

    public event EventHandler<PacketSentEventArgs>? PacketSent;

    public event EventHandler<ConnectionState>? StateChanged;

    public event EventHandler<DisconnectInfo>? Disconnected;

    public event EventHandler<HeadlessLogEventArgs>? Log;

    public AqwSessionOptions Options { get; }

    public AqwWebApi Api => _api;

    /// <summary>The model; the same instance across reconnects (reset on each connect).</summary>
    public WorldModel World { get; }

    /// <summary>The current connection, if any.</summary>
    public AqwConnection? Connection => _connection;

    /// <summary>Commands over the current connection.</summary>
    public GameCommands Commands => new(_connection ?? throw new InvalidOperationException("Not connected."));

    /// <summary>A web login has started and not finished.</summary>
    public bool WebLoginRunning
    {
        get
        {
            lock (_loginGate)
                return _pendingLogin is { IsCompleted: false };
        }
    }

    /// <summary>The last HTTP login.</summary>
    public LoginResult? Account { get; private set; }

    /// <summary>The version used for the last socket login and where it came from.</summary>
    public ClientVersionResult? ClientVersion { get; private set; }

    public GameServer? Server { get; private set; }

    /// <summary>
    /// HTTP login: gets the socket token and the server list. Each web login replaces the account's token on the
    /// server, so <see cref="ConnectAsync"/> waits for one still running and uses the newest.
    /// </summary>
    public async Task<LoginResult> LoginAsync(AqwCredentials credentials, CancellationToken cancellationToken = default)
    {
        _credentials = credentials;
        long generation;
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_loginGate)
        {
            generation = ++_loginsStarted;
            _pendingLogin = done.Task;
        }
        LoginResult result;
        try
        {
            result = await _api.LoginAsync(credentials.Username, credentials.Password, cancellationToken).ConfigureAwait(false);
            result.Generation = generation;
            result.ObtainedAt = _time.GetUtcNow();
            lock (_loginGate)
            {
                // Two logins in flight: the one started last is the one whose token the server keeps.
                if (Account is null || Account.Generation < generation)
                    Account = result;
            }
        }
        finally
        {
            done.TrySetResult();
        }
        RaiseLog(HeadlessLogLevel.Info, $"Web login #{generation} ok for {result.Username}: level {result.Level}, {result.Servers.Count} servers listed (its token replaces any earlier one).");
        return result;
    }

    /// <summary>A server this account may join (preferred name first, else the least full).</summary>
    public GameServer PickServer(string? preferred = null)
    {
        LoginResult account = Account ?? throw new InvalidOperationException("Log in first.");
        return ServerPicker.Pick(account.Servers, account.IsMember, account.Level, preferred)
            ?? throw new AqwLoginException(LoginFailureKind.NoServer, preferred is null ? "No usable server is online." : $"Server '{preferred}' is not usable and no other server is online.");
    }

    /// <summary>
    /// Connects to <paramref name="server"/> and loads the character. The client version is the configured one,
    /// else the live game file's, else the fallback; if the server refuses it naming the version it requires, the
    /// login is retried with that version.
    /// </summary>
    /// <remarks>
    /// The token is the newest web login's: a web login still running is waited for (it replaces the token on the
    /// server), and the connection reads the token again when the server answers verChk, as the game does.
    /// </remarks>
    public async Task ConnectAsync(GameServer server, CancellationToken cancellationToken = default)
    {
        await WaitForWebLoginAsync(cancellationToken).ConfigureAwait(false);
        _ = Account ?? throw new InvalidOperationException("Log in first.");
        ClientVersionResult version = await ResolveVersionAsync(cancellationToken).ConfigureAwait(false);
        version = ApplyVersionFallback(version);
        for (int attempt = 0; ; attempt++)
        {
            await CloseCurrentAsync().ConfigureAwait(false);
            await WaitForWebLoginAsync(cancellationToken).ConfigureAwait(false);
            LoginResult account = Account ?? throw new InvalidOperationException("Log in first.");
            AqwConnection connection = new(Options.Connection, World, _time);
            connection.AddRedaction(_credentials?.Password, "<password>");
            Attach(connection);
            _connection = connection;
            Server = server;
            ClientVersion = version;
            RaiseLog(HeadlessLogLevel.Info, $"Socket login to {server.Name} ({server.Host}:{server.Port}) with the token of web login #{account.Generation} "
                + $"(finished {Age(account)} ago{(account.UsedBySocket ? ", already used by an earlier socket login" : string.Empty)}), "
                + $"client version {version.Version} ({version.Source}), verChk API version {Options.Connection.ApiVersion}.");
            try
            {
                await connection.ConnectAsync(server, () => CurrentSocketLogin(account), version.Version, cancellationToken).ConfigureAwait(false);
                NoteSuccess(version);
                return;
            }
            catch (AqwLoginException e) when (e.Kind == LoginFailureKind.ClientVersionRejected
                && e.RequiredClientVersion is string required
                && required != version.Version
                && attempt < Options.ClientVersionRetries)
            {
                RaiseLog(HeadlessLogLevel.Warning, $"Client version {version.Version} refused; retrying with {required}.");
                version = new ClientVersionResult(required, ClientVersionSource.ServerRequired, e.Message);
                // The token may be single use: get a fresh one before trying again.
                if (_credentials is not null)
                    await LoginAsync(_credentials, cancellationToken).ConfigureAwait(false);
            }
            catch (AqwLoginException e) when (e.Kind == LoginFailureKind.SocketRejected && NoteRefusal(e, version))
            {
                throw; // not reached: NoteRefusal returns false
            }
        }
    }

    // The game reads loginInfo when the socket is ready; a newer web login than the one connect started with wins.
    private SocketLogin CurrentSocketLogin(LoginResult started)
    {
        LoginResult account = Account is LoginResult now && now.Generation >= started.Generation ? now : started;
        account.UsedBySocket = true;
        return new SocketLogin(account.Username, account.Token, $"web login #{account.Generation}, finished {Age(account)} before");
    }

    private string Age(LoginResult account)
    {
        TimeSpan age = _time.GetUtcNow() - account.ObtainedAt;
        return age < TimeSpan.Zero ? "0.0 s" : $"{age.TotalSeconds:0.0} s";
    }

    // A web login started (by this session's owner or by Skua's relogin) but not finished: its token will replace the
    // current one on the server, so a socket login now would use a token about to be superseded.
    private async Task WaitForWebLoginAsync(CancellationToken cancellationToken)
    {
        for (int i = 0; i < 4; i++)
        {
            Task? pending;
            lock (_loginGate)
                pending = _pendingLogin;
            if (pending is null || pending.IsCompleted)
                return;
            RaiseLog(HeadlessLogLevel.Info, "A web login is still running: waiting for it, since its token replaces the current one.");
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // The fallback (AqwSessionOptions.FallBackToKnownGoodVersion): one socket login with the known-good version after
    // the discovered one was refused. It replaces the version of the next attempt; it never adds an attempt.
    private ClientVersionResult ApplyVersionFallback(ClientVersionResult version)
    {
        if (_fallback != VersionFallback.Armed)
            return version;
        _fallback = VersionFallback.Trying;
        string good = Options.KnownGoodClientVersion;
        RaiseLog(HeadlessLogLevel.Warning, $"Fallback: the last socket login with client version {_refusedVersion} was refused without the server naming a version; "
            + $"this one sends {good}, the version last known to work, instead of {version.Version} ({version.Source}).");
        return new ClientVersionResult(good, ClientVersionSource.KnownGood, $"{_refusedVersion} was refused");
    }

    private bool NoteRefusal(AqwLoginException e, ClientVersionResult version)
    {
        if (_fallback == VersionFallback.Trying)
        {
            _fallback = VersionFallback.Spent;
            RaiseLog(HeadlessLogLevel.Warning, $"Fallback result: client version {version.Version} was refused the same way, so the version is not the cause; "
                + $"the next socket login goes back to {_refusedVersion}.");
            return false;
        }
        if (_fallback == VersionFallback.None && Options.FallBackToKnownGoodVersion
            && version.Source is ClientVersionSource.GameFile or ClientVersionSource.ServerRequired
            && !string.Equals(Packets.As3Number(version.Version), Packets.As3Number(Options.KnownGoodClientVersion), StringComparison.Ordinal))
        {
            _fallback = VersionFallback.Armed;
            _refusedVersion = version.Version;
            RaiseLog(HeadlessLogLevel.Warning, $"Socket login with client version {version.Version} ({version.Source}) refused. Fallback armed: the next socket login "
                + $"(after a fresh web login) sends client version {Options.KnownGoodClientVersion} once.");
        }
        return false;
    }

    private void NoteSuccess(ClientVersionResult version)
    {
        if (_fallback == VersionFallback.Trying)
        {
            RaiseLog(HeadlessLogLevel.Warning, $"Fallback result: client version {version.Version} got in where {_refusedVersion} was refused. This session keeps {version.Version}.");
            _workingVersion = version;
        }
        else if (_fallback == VersionFallback.Spent)
        {
            RaiseLog(HeadlessLogLevel.Info, $"Client version {version.Version} got in after the fallback to {Options.KnownGoodClientVersion} was refused too.");
        }
        _fallback = VersionFallback.None;
    }

    /// <summary>
    /// Logs in over HTTP again (fresh token) and reconnects to the same server when it is still listed, else to
    /// another usable one.
    /// </summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        AqwCredentials credentials = _credentials ?? throw new InvalidOperationException("Log in first.");
        await CloseCurrentAsync().ConfigureAwait(false);
        await LoginAsync(credentials, cancellationToken).ConfigureAwait(false);
        GameServer server = PickServer(Server?.Name);
        await ConnectAsync(server, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the credentials kept for <see cref="ReconnectAsync"/> and the last HTTP login (its token and server
    /// list). The session stays usable: the next <see cref="LoginAsync"/> starts over.
    /// </summary>
    public void ForgetCredentials()
    {
        _credentials = null;
        Account = null;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
            await _connection.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ClientVersionResult> ResolveVersionAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(Options.ClientVersion))
            return new ClientVersionResult(Options.ClientVersion, ClientVersionSource.Configured, "set in options");
        if (_workingVersion is not null)
            return _workingVersion;
        if (ClientVersion is { Source: ClientVersionSource.ServerRequired } required)
            return required;
        if (_discovered is not null)
            return _discovered;
        if (!Options.DiscoverClientVersion)
            return new ClientVersionResult(Net.ClientVersion.Fallback, ClientVersionSource.Fallback, "discovery disabled");
        ClientVersionResult result = await _resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        RaiseLog(result.Source == ClientVersionSource.Fallback ? HeadlessLogLevel.Warning : HeadlessLogLevel.Info,
            $"Client version {result.Version} ({result.Source}: {result.Detail}).");
        if (result.Source == ClientVersionSource.GameFile)
            _discovered = result;
        return result;
    }

    private void Attach(AqwConnection connection)
    {
        connection.PacketReceived += OnPacketReceived;
        connection.PacketSent += (_, e) => PacketSent?.Invoke(this, e);
        connection.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
        connection.Disconnected += (_, e) => Disconnected?.Invoke(this, e);
        connection.Log += (_, e) => Log?.Invoke(this, e);
    }

    // pext JSON is only serialised when someone listens to the session's ExtensionResponse.
    private void OnPacketReceived(object? sender, InboundPacket packet)
    {
        PacketReceived?.Invoke(this, packet);
        EventHandler<ExtensionResponseEventArgs>? ext = ExtensionResponse;
        if (ext is not null && packet.ToExtensionResponseJson() is string json)
            ext(this, new ExtensionResponseEventArgs(packet, json));
    }

    private async Task CloseCurrentAsync()
    {
        AqwConnection? old = _connection;
        _connection = null;
        if (old is null)
            return;
        if (old.State != ConnectionState.Closed)
            await old.DisconnectAsync().ConfigureAwait(false);
        await old.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Raises <see cref="Log"/>: for layers built on the session whose messages belong with its own.</summary>
    public void WriteLog(HeadlessLogLevel level, string message)
    {
        RaiseLog(level, message);
    }

    private void RaiseLog(HeadlessLogLevel level, string message)
    {
        Log?.Invoke(this, new HeadlessLogEventArgs(level, message));
    }

    public async ValueTask DisposeAsync()
    {
        await CloseCurrentAsync().ConfigureAwait(false);
        if (_ownsApi)
            _api.Dispose();
    }
}
