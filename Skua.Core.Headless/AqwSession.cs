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
    private AqwCredentials? _credentials;
    private AqwConnection? _connection;

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

    /// <summary>The last HTTP login.</summary>
    public LoginResult? Account { get; private set; }

    /// <summary>The version used for the last socket login and where it came from.</summary>
    public ClientVersionResult? ClientVersion { get; private set; }

    public GameServer? Server { get; private set; }

    /// <summary>HTTP login: gets the socket token and the server list.</summary>
    public async Task<LoginResult> LoginAsync(AqwCredentials credentials, CancellationToken cancellationToken = default)
    {
        _credentials = credentials;
        Account = await _api.LoginAsync(credentials.Username, credentials.Password, cancellationToken).ConfigureAwait(false);
        RaiseLog(HeadlessLogLevel.Info, $"Web login ok for {Account.Username}: level {Account.Level}, {Account.Servers.Count} servers listed.");
        return Account;
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
    public async Task ConnectAsync(GameServer server, CancellationToken cancellationToken = default)
    {
        LoginResult account = Account ?? throw new InvalidOperationException("Log in first.");
        ClientVersionResult version = await ResolveVersionAsync(cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; ; attempt++)
        {
            await CloseCurrentAsync().ConfigureAwait(false);
            AqwConnection connection = new(Options.Connection, World, _time);
            Attach(connection);
            _connection = connection;
            Server = server;
            ClientVersion = version;
            try
            {
                await connection.ConnectAsync(server, account.Username, account.Token, version.Version, cancellationToken).ConfigureAwait(false);
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
                    account = await LoginAsync(_credentials, cancellationToken).ConfigureAwait(false);
            }
        }
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

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
            await _connection.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ClientVersionResult> ResolveVersionAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(Options.ClientVersion))
            return new ClientVersionResult(Options.ClientVersion, ClientVersionSource.Configured, "set in options");
        if (ClientVersion is { Source: ClientVersionSource.ServerRequired or ClientVersionSource.GameFile } known)
            return known;
        if (!Options.DiscoverClientVersion)
            return new ClientVersionResult(Net.ClientVersion.Fallback, ClientVersionSource.Fallback, "discovery disabled");
        ClientVersionResult result = await _resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        RaiseLog(result.Source == ClientVersionSource.Fallback ? HeadlessLogLevel.Warning : HeadlessLogLevel.Info,
            $"Client version {result.Version} ({result.Source}: {result.Detail}).");
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
