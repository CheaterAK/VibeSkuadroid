using Skua.Core.Headless.Client;
using Skua.Core.Headless.Maps;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.World;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Flash;

/// <summary>Settings for <see cref="HeadlessGame"/>.</summary>
public sealed class HeadlessGameOptions
{
    public AqwSessionOptions Session { get; set; } = new();

    public GameClientOptions Client { get; set; } = new();

    /// <summary>Where map cells are cached between runs; null keeps them in memory only.</summary>
    public string? MapCacheDirectory { get; set; }

    /// <summary>Download map files to learn their cells (otherwise <c>world.map.currentScene.labels</c> stays empty).</summary>
    public bool DownloadMapCells { get; set; } = true;

    /// <summary>
    /// A <see cref="HeadlessGame.Login"/> for the account just logged in, whose token no socket login has used yet and
    /// is younger than this, shows that login's server list again instead of logging in over HTTP a second time
    /// (each web login replaces the token). This is the engine's login followed at once by Skua's relogin, which
    /// logs in again before it joins. Zero turns it off.
    /// </summary>
    public TimeSpan ReuseUnusedWebLoginFor { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>A change of the login lifecycle.</summary>
public enum HeadlessGameState
{
    /// <summary>Nothing started.</summary>
    LoggedOut,

    /// <summary>HTTP login running.</summary>
    LoggingIn,

    /// <summary>HTTP login done, server list shown.</summary>
    ServerList,

    /// <summary>Connecting to a game server and loading the character.</summary>
    Connecting,

    /// <summary>In game.</summary>
    InGame,
}

/// <summary>
/// One headless AQW client as skua.swf plus the game file would be: the session (HTTP login, socket, world model), the
/// client rules (<see cref="GameClient"/>), and the login screen's state (server list, modal messages, kick warning).
/// The login screen is driven the way skua drives the Flash one: <see cref="Login"/>, <see cref="ConnectTo"/>,
/// <see cref="Logout"/>; every step is asynchronous and observable through the state.
/// </summary>
public sealed class HeadlessGame : IPacketSender, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private CancellationTokenSource _lifecycle = new();
    private HeadlessGameState _state = HeadlessGameState.LoggedOut;
    private string? _modal;
    private string? _connDetail;
    private bool _kicked;
    private bool _loginVisible;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private IReadOnlyList<GameServer> _servers = Array.Empty<GameServer>();
    private string _serverIp = string.Empty;
    private bool _catching;
    private int _loginRequests;

    public HeadlessGame(HeadlessGameOptions? options = null, AqwWebApi? api = null, TimeProvider? time = null)
    {
        Options = options ?? new HeadlessGameOptions();
        Time = time ?? TimeProvider.System;
        Session = new AqwSession(Options.Session, api, Time);
        Maps = Options.DownloadMapCells
            ? new MapCellCache((file, ct) => Session.Api.DownloadGameFileAsync("maps/" + file.TrimStart('/'), ct), Options.MapCacheDirectory)
            : null;
        Client = new GameClient(Session.World, this, Time, Maps, Options.Client);
        Session.PacketReceived += OnPacketReceived;
        Session.PacketSent += OnPacketSent;
        Session.Disconnected += OnDisconnected;
        Session.StateChanged += OnSessionState;
        Session.Log += (_, e) => Log?.Invoke(this, e.ToString());
        Client.Log += (_, e) => Log?.Invoke(this, e.Message);
    }

    public HeadlessGameOptions Options { get; }

    public TimeProvider Time { get; }

    public AqwSession Session { get; }

    public GameClient Client { get; }

    public MapCellCache? Maps { get; }

    public WorldModel World => Session.World;

    /// <summary>Lines worth logging (login progress, refused actions, failures).</summary>
    public event EventHandler<string>? Log;

    /// <summary>A received packet after the model and the client rules applied it (in arrival order, on the reader thread).</summary>
    public event EventHandler<InboundPacket>? PacketProcessed;

    /// <summary>A packet written to the socket, after the client rules saw it.</summary>
    public event EventHandler<PacketSentEventArgs>? PacketWritten;

    /// <summary>The socket closed (any reason).</summary>
    public event EventHandler<DisconnectInfo>? ConnectionLost;

    public event EventHandler<HeadlessGameState>? StateChanged;

    public HeadlessGameState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>The account name given to the last login.</summary>
    public string Username
    {
        get { lock (_gate) return _username; }
    }

    /// <summary>The password given to the last login (skua reads loginInfo.strPassword to log in again).</summary>
    public string Password
    {
        get { lock (_gate) return _password; }
    }

    /// <summary>The server list of the last HTTP login.</summary>
    public IReadOnlyList<GameServer> Servers
    {
        get { lock (_gate) return _servers; }
    }

    /// <summary>The IP of the server joined last (skua's serverIP static).</summary>
    public string ServerIp
    {
        get { lock (_gate) return _serverIp; }
    }

    /// <summary>The text of the login screen's modal dialog, if one is shown.</summary>
    public string? ModalMessage
    {
        get { lock (_gate) return _modal; }
    }

    /// <summary>mcConnDetail: shown while connecting, and with the reason after a failed connect.</summary>
    public string? ConnectionDetail
    {
        get { lock (_gate) return _connDetail; }
    }

    /// <summary>mcLogin.warning: the server kicked the character.</summary>
    public bool Kicked
    {
        get { lock (_gate) return _kicked; }
    }

    /// <summary>mcLogin.visible.</summary>
    public bool LoginScreenVisible
    {
        get { lock (_gate) return _loginVisible; }
    }

    /// <summary>skua.swf catchPackets: outgoing packets are reported (the headless bridge reports them always).</summary>
    public bool CatchingPackets
    {
        get { lock (_gate) return _catching; }
        set { lock (_gate) _catching = value; }
    }

    /// <summary>The socket is open (sfc.isConnected).</summary>
    public bool IsConnected => Session.Connection?.State is ConnectionState.Handshaking or ConnectionState.LoggingIn or ConnectionState.Loading or ConnectionState.Ready;

    /// <summary>The character is loaded and playable (the Flash world exists).</summary>
    public bool IsInGame => Session.Connection?.State == ConnectionState.Ready;

    private void SetState(HeadlessGameState state)
    {
        lock (_gate)
        {
            if (_state == state)
                return;
            _state = state;
        }
        StateChanged?.Invoke(this, state);
    }

    // ---------------------------------------------------------------- IPacketSender

    bool IPacketSender.Send(string packet)
    {
        AqwConnection? c = Session.Connection;
        if (c is null || c.State == ConnectionState.Closed)
            return false;
        _ = c.SendAsync(packet);
        return true;
    }

    /// <summary>sfc.sendString / sendXtMessage: any packet, paced like the client's own.</summary>
    public bool SendRaw(string packet)
    {
        return ((IPacketSender)this).Send(packet);
    }

    /// <summary>
    /// skua.swf sendClientPacket: hands a packet to the game as if the server had sent it (the model and the client
    /// rules apply it and the usual events are raised). Nothing goes to the server.
    /// </summary>
    public void InjectInbound(string text)
    {
        InboundPacket packet = PacketCodec.Parse(text);
        World.Apply(packet);
        OnPacketReceived(this, packet);
    }

    // ---------------------------------------------------------------- login screen

    /// <summary>
    /// The game's <c>login(user, pass)</c>: an HTTP login in the background. On success the server list is shown
    /// (<see cref="Servers"/>, mcLogin.sl.iList); on failure the reason is the modal message.
    /// </summary>
    public Task Login(string username, string password)
    {
        int request;
        CancellationToken token;
        LoginResult? reuse;
        lock (_gate)
        {
            reuse = UnusedLoginFor(username, password);
            _username = username;
            _password = password;
            _modal = null;
            _kicked = false;
            _loginVisible = true;
            // The list (and the token behind it) belongs to the previous web login, which this one supersedes: without
            // clearing it Skua's relogin sees "server list ready" at once and connects with the old token while the
            // new login is still running (the likely cause of the live "Character data could not be retrieved" refusals).
            _servers = reuse?.Servers ?? Array.Empty<GameServer>();
            request = ++_loginRequests;
            token = _lifecycle.Token;
        }
        if (reuse is not null)
        {
            Session.WriteLog(HeadlessLogLevel.Info, $"Login: web login #{reuse.Generation} for this account finished {(Time.GetUtcNow() - reuse.ObtainedAt).TotalSeconds:0.0} s ago "
                + "and no socket login has used its token, so its server list is shown again instead of a second web login (which would replace the token).");
            SetState(HeadlessGameState.ServerList);
            return Task.CompletedTask;
        }
        SetState(HeadlessGameState.LoggingIn);
        return Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                    throw new AqwLoginException(LoginFailureKind.Rejected, "Login refused: missing username or password.");
                LoginResult account = await Session.LoginAsync(new AqwCredentials(username, password), token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (request != _loginRequests)
                        return;
                    _servers = account.Servers;
                }
                SetState(HeadlessGameState.ServerList);
            }
            catch (Exception e) when (e is AqwLoginException or HttpRequestException or OperationCanceledException or ArgumentException)
            {
                lock (_gate)
                {
                    if (request != _loginRequests)
                        return;
                    _modal = e is AqwLoginException ? e.Message : "Login failed: " + e.Message;
                }
                Log?.Invoke(this, "Login failed: " + e.Message);
                SetState(HeadlessGameState.LoggedOut);
            }
        }, CancellationToken.None);
    }

    // Under _gate. See HeadlessGameOptions.ReuseUnusedWebLoginFor.
    private LoginResult? UnusedLoginFor(string username, string password)
    {
        LoginResult? account = Session.Account;
        if (account is null || account.UsedBySocket || Session.WebLoginRunning || IsConnected || Options.ReuseUnusedWebLoginFor <= TimeSpan.Zero)
            return null;
        if (!string.Equals(username, _username, StringComparison.OrdinalIgnoreCase) || !string.Equals(password, _password, StringComparison.Ordinal))
            return null;
        TimeSpan age = Time.GetUtcNow() - account.ObtainedAt;
        return age >= TimeSpan.Zero && age < Options.ReuseUnusedWebLoginFor ? account : null;
    }

    /// <summary>The account object skua reads as the static objLogin, or null before a login.</summary>
    public JsonObject? LoginObject()
    {
        LoginResult? account = Session.Account;
        if (account is null)
            return null;
        JsonObject o = (JsonObject)account.Raw.DeepClone();
        o["unm"] = account.Username;
        o["iUpgDays"] = account.UpgradeDays;
        o["iLvl"] = account.Level;
        o["userid"] = account.UserId;
        o["bSuccess"] = 1;
        o["servers"] = ServerListJson();
        return o;
    }

    /// <summary>serialCmd.servers: the server objects as the login reply sent them.</summary>
    public JsonArray ServerListJson()
    {
        JsonArray list = new();
        foreach (GameServer s in Servers)
        {
            list.Add(s.Raw?.DeepClone() ?? new JsonObject
            {
                ["sName"] = s.Name, ["sIP"] = s.Host, ["iPort"] = s.Port, ["iCount"] = s.Players, ["iMax"] = s.MaxPlayers,
                ["bOnline"] = s.Online ? 1 : 0, ["bUpg"] = s.MembersOnly ? 1 : 0, ["iLevel"] = s.MinLevel, ["sLang"] = s.Language,
            });
        }
        return list;
    }

    /// <summary>The server whose name contains <paramref name="name"/> (skua.swf clickServer).</summary>
    public GameServer? FindServer(string name)
    {
        return Servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? Servers.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The server at <paramref name="ip"/> (and port, when given).</summary>
    public GameServer? FindServerByIp(string ip, int? port = null)
    {
        return Servers.FirstOrDefault(s => s.Host.Equals(ip, StringComparison.OrdinalIgnoreCase) && (port is null || s.Port == port))
            ?? Servers.FirstOrDefault(s => s.Host.Equals(ip, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Joins a server in the background (Game.connectTo). Applies the login screen's own refusals first (offline,
    /// full, member-only). Returns false when no login is done or the server is refused at once.
    /// </summary>
    public bool ConnectTo(GameServer server)
    {
        LoginResult? account = Session.Account;
        if (account is null)
        {
            SetModal("Log in before choosing a server.");
            return false;
        }
        if (!server.Online)
        {
            SetModal("Server currently offline!");
            return false;
        }
        if (server.IsFull)
        {
            SetModal("Server is Full!");
            return false;
        }
        if (server.MembersOnly && !account.IsMember)
        {
            SetModal("Member Server! Do you want to upgrade your account to access this premium server now?");
            return false;
        }
        CancellationToken token;
        lock (_gate)
        {
            _connDetail = "Connecting to server...";
            _modal = null;
            _serverIp = server.Host;
            token = _lifecycle.Token;
        }
        SetState(HeadlessGameState.Connecting);
        _ = Task.Run(async () =>
        {
            await _connectGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Client.Reset();
                await Session.ConnectAsync(server, token).ConfigureAwait(false);
                lock (_gate)
                {
                    _connDetail = null;
                    _loginVisible = false;
                }
                SetState(HeadlessGameState.InGame);
            }
            catch (Exception e) when (e is AqwLoginException or OperationCanceledException or IOException or InvalidOperationException)
            {
                lock (_gate)
                    _connDetail = $"Connection closed: {e.Message}";
                Log?.Invoke(this, $"Connect to {server.Name} failed: {e.Message}");
                SetState(Session.Account is null ? HeadlessGameState.LoggedOut : HeadlessGameState.ServerList);
            }
            finally
            {
                _connectGate.Release();
            }
        }, CancellationToken.None);
        return true;
    }

    /// <summary>The game's logout(): tells the server, closes the socket, shows the login screen.</summary>
    public void Logout()
    {
        SendRaw(Packets.Logout());
        Disconnect();
        lock (_gate)
            _loginVisible = true;
    }

    /// <summary>
    /// Forgets the last login: the account name and password skua reads back (loginInfo), the server list, and the
    /// session's credentials and token (<see cref="AqwSession.ForgetCredentials"/>). Call after a logout; the next
    /// <see cref="Login"/> starts over.
    /// </summary>
    public void ForgetLogin()
    {
        lock (_gate)
        {
            _username = string.Empty;
            _password = string.Empty;
            _servers = Array.Empty<GameServer>();
            _loginRequests++;
        }
        Session.ForgetCredentials();
        if (!IsConnected)
            SetState(HeadlessGameState.LoggedOut);
    }

    /// <summary>sfc.disconnect.</summary>
    public void Disconnect()
    {
        AqwConnection? c = Session.Connection;
        if (c is not null && c.State != ConnectionState.Closed)
            _ = c.DisconnectAsync();
    }

    /// <summary>skua.swf resetConnectionState: drops pending work and the socket, clears the login dialogs.</summary>
    public void ResetConnectionState()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _lifecycle;
            _lifecycle = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
        KillLoginModals();
        Disconnect();
        lock (_gate)
            _connDetail = null;
    }

    /// <summary>skua.swf killLoginModals.</summary>
    public void KillLoginModals()
    {
        lock (_gate)
        {
            _modal = null;
            _kicked = false;
            if (_connDetail is not null && _state != HeadlessGameState.Connecting)
                _connDetail = null;
        }
    }

    /// <summary>gotoAndPlay("Login").</summary>
    public void ShowLoginScreen()
    {
        lock (_gate)
            _loginVisible = true;
        if (!IsConnected && State == HeadlessGameState.InGame)
            SetState(Session.Account is null ? HeadlessGameState.LoggedOut : HeadlessGameState.ServerList);
    }

    private void SetModal(string text)
    {
        lock (_gate)
            _modal = text;
        Log?.Invoke(this, text);
    }

    // ---------------------------------------------------------------- session events

    private void OnPacketReceived(object? sender, InboundPacket packet)
    {
        Client.OnInbound(packet);
        if (packet.Kind == PacketKind.Xt && packet.Command is "logoutWarning" or "multiLoginWarning")
        {
            lock (_gate)
                _kicked = true;
        }
        PacketProcessed?.Invoke(this, packet);
    }

    private void OnPacketSent(object? sender, PacketSentEventArgs e)
    {
        Client.OnOutbound(e.Text);
        PacketWritten?.Invoke(this, e);
    }

    private void OnSessionState(object? sender, ConnectionState state)
    {
        if (state == ConnectionState.Ready)
            SetState(HeadlessGameState.InGame);
    }

    private void OnDisconnected(object? sender, DisconnectInfo info)
    {
        Client.CancelAutoAttack();
        bool wasInGame;
        lock (_gate)
        {
            wasInGame = _state == HeadlessGameState.InGame;
            _loginVisible = true;
            if (info.Reason is DisconnectReason.Kicked or DisconnectReason.LoggedInElsewhere or DisconnectReason.InvalidSession)
            {
                _kicked = true;
                _modal = info.Message;
            }
        }
        if (wasInGame || State == HeadlessGameState.Connecting)
            SetState(Session.Account is null ? HeadlessGameState.LoggedOut : HeadlessGameState.ServerList);
        ConnectionLost?.Invoke(this, info);
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifecycle.Cancel();
        Client.Dispose();
        await Session.DisposeAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
        _connectGate.Dispose();
    }
}
