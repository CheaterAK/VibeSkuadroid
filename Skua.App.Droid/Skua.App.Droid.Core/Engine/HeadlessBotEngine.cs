using Skua.App.Droid.Core.Memory;
using Skua.App.Droid.Core.Security;
using Skua.Core.Headless;
using Skua.Core.Headless.Commands;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.World;
using System.Net.Sockets;

namespace Skua.App.Droid.Core.Engine;

/// <summary>
/// <see cref="IBotEngine"/> directly on Skua.Core.Headless: web login, server list, socket session with automatic
/// reconnect, live world state, map joins, raw packets. Scripts are delegated to an <see cref="IScriptHost"/>.
/// </summary>
/// <remarks>
/// <para>
/// With an <see cref="ISessionScriptHost"/> (the Skua.Core host, the app's normal case) the host creates the one
/// <see cref="AqwSession"/> of the engine's lifetime: login and reconnect run through Skua's own login code, and a
/// logout keeps the session object but makes the host forget every credential and token in it. The engine, Skua's
/// services and the Flash bridge therefore never point at different sessions. Reconnects: while a script is busy or
/// Skua's relogin runs, Skua reconnects (its AutoRelogin option, which the app's auto-reconnect setting turns on at
/// every script start and scripts may change) and the engine's loop waits; otherwise the engine's loop reconnects
/// through the host.
/// </para>
/// <para>
/// Without one, the engine owns exactly one <see cref="AqwSession"/> at a time from its session factory. A logout
/// disposes it (dropping the credentials it holds for reconnects) and creates a fresh one, which is handed to the
/// script host again via <see cref="IScriptHost.Attach"/>.
/// </para>
/// </remarks>
public sealed class HeadlessBotEngine : IBotEngine
{
    private readonly HeadlessEngineOptions _options;
    private readonly IScriptHost _scripts;
    private ISessionScriptHost? _sessionHost;
    private readonly IMemoryProbe _memoryProbe;
    private readonly TimeProvider _time;
    private readonly SecretScrubber _scrubber;
    private readonly RingBuffer<LogEntry> _log;
    private readonly RingBuffer<PacketEntry> _packets;
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly object _snapshotLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _publisher;
    private readonly Packets.PacketInterception _interception;

    private AqwSession _session;
    private volatile ConnectionPhase _phase = ConnectionPhase.Offline;
    private volatile string? _statusText;
    private volatile string? _account;
    private volatile string? _server;
    private volatile ScriptStatus _scriptStatus;
    private volatile MemoryStatus _memory = MemoryStatus.Unknown;
    private volatile EngineSnapshot _snapshot = EngineSnapshot.Initial;
    private volatile bool _packetCapture;
    private volatile bool _wantOnline;
    private volatile bool _connecting;
    // The script start in progress (completes, never faults, when StartScriptAsync returns): a pause asked for meanwhile waits for it.
    private Task? _starting;
    private volatile bool _loggingOut;
    // The game view has the account: no reconnect of any kind until EndGameViewHoldAsync.
    private volatile bool _gameViewHold;
    private bool _holdWasOnline;
    private readonly List<string> _work = new();
    private string? _lastToken;
    private CancellationTokenSource? _reconnectCts;
    private int _dirty = 1;
    private int _disposed;

    public HeadlessBotEngine(HeadlessEngineOptions? options = null, IScriptHost? scriptHost = null, IMemoryProbe? memoryProbe = null, TimeProvider? time = null)
    {
        _options = options ?? new HeadlessEngineOptions();
        _scrubber = _options.Scrubber ?? new SecretScrubber();
        _scripts = scriptHost ?? new UnavailableScriptHost();
        _memoryProbe = memoryProbe ?? new ProcMemoryProbe();
        _time = time ?? TimeProvider.System;
        _log = new RingBuffer<LogEntry>(_options.LogCapacity);
        _packets = new RingBuffer<PacketEntry>(_options.PacketCapacity);
        _scriptStatus = _scripts.Status;
        _scripts.StatusChanged += OnScriptStatus;
        _scripts.Log += OnScriptLog;
        _sessionHost = _scripts as ISessionScriptHost;
        _interception = new Packets.PacketInterception(Scrub, _time);
        _interception.StateChanged += OnInterceptionChanged;

        _session = CreateSession();
        SampleMemory();
        PublishNow();
        _publisher = Task.Run(PublishLoopAsync);
    }

    public EngineSnapshot Snapshot => _snapshot;

    public event EventHandler<EngineSnapshot>? SnapshotChanged;

    public event EventHandler<LogEntry>? LogAdded;

    public event EventHandler<PacketEntry>? PacketLogged;

    public event EventHandler<EngineStopRequest>? StopRequested;

    public ScriptSupport ScriptSupport => _scripts.Support;

    /// <summary>The built-in plugins' game-side halves, from the script host (null without a Skua host).</summary>
    public Plugins.IPluginBackends? Plugins => (_scripts as Plugins.IPluginBackendSource)?.PluginBackends;

    public IServiceProvider? SkuaServices => (_scripts as ISkuaServicesSource)?.SkuaServices;

    /// <summary>Game events and drop actions from the script host (null without a Skua host).</summary>
    public Notifications.IGameEvents? GameEvents => (_scripts as Notifications.IGameEventSource)?.GameEvents;

    public bool AutoReconnect
    {
        get => _options.AutoReconnect;
        set
        {
            _options.AutoReconnect = value;
            if (_sessionHost is ISessionScriptHost host)
                host.AutoRelogin = value;
        }
    }

    public bool PacketCaptureEnabled
    {
        get => _packetCapture;
        set => _packetCapture = value;
    }

    /// <summary>The live session (for the host adapter and tests). Replaced after each logout unless a session host owns it.</summary>
    public AqwSession Session => _session;

    public IReadOnlyList<LogEntry> GetRecentLog() => _log.Snapshot();

    public void ClearLog() => _log.Clear();

    public void WriteLog(EngineLogLevel level, string source, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        AddLog(level, source, message ?? string.Empty);
    }

    public IReadOnlyList<PacketEntry> GetRecentPackets() => _packets.Snapshot();

    public Packets.PacketInterception Interception => _interception;

    private bool _interceptionWasActive;

    private void OnInterceptionChanged(object? sender, EventArgs e)
    {
        bool active = _interception.IsActive;
        if (active == _interceptionWasActive)
            return;
        _interceptionWasActive = active;
        AddLog(EngineLogLevel.Warning, "interceptor", active
            ? $"Packet interceptor on: {_interception.Rules.Count(r => r.Enabled)} rule(s) change or drop game messages from the next one on."
            : "Packet interceptor off: game messages pass unchanged.");
    }

    public ConnectionDetails? GetConnectionDetails()
    {
        AqwConnection? c = _session.Connection;
        if (c is null)
            return null;
        ConnectionStatistics st = c.GetStatistics();
        return new ConnectionDetails(
            c.Server?.Name,
            c.Server is { } server ? $"{server.Host}:{server.Port}" : null,
            c.State.ToString(),
            c.ClientVersion,
            st.PacketsIn,
            st.PacketsOut,
            st.BytesIn,
            st.BytesOut,
            st.OutboundDropped,
            st.OutboundQueued,
            st.SlowDownWarnings,
            st.Probes,
            st.LastInbound,
            st.LastRoundTrip,
            st.AverageRoundTrip,
            c.Options.SendGap,
            c.Options.SlowSendGap)
        {
            Identity = c,
        };
    }

    public IReadOnlyList<FlashGapRow>? GetFlashGaps() => (_scripts as IFlashGapSource)?.GetFlashGaps();

    public void ClearPackets() => _packets.Clear();

    public async Task<IReadOnlyList<ServerChoice>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<GameServer> servers = await _session.Api.GetServersAsync(cancellationToken).ConfigureAwait(false);
            return servers.Select(ToChoice).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (AqwLoginException e)
        {
            throw new EngineException($"Could not load the server list: {Scrub(e.Message)}", e);
        }
    }

    public async Task LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
            throw new EngineException("Enter the account name and password.");
        if (_scripts.Status.IsBusy)
            throw new EngineException("Stop the running script before logging in again.");
        if (_gameViewHold)
            throw new EngineException(GameViewHasTheAccount);

        CancelReconnect();
        if (_sessionHost is ISessionScriptHost host)
        {
            await LoginThroughHostAsync(host, request, cancellationToken).ConfigureAwait(false);
            return;
        }
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_session.Connection is { State: not ConnectionState.Closed })
                await DisconnectQuietlyAsync().ConfigureAwait(false);

            _scrubber.Add(request.Password);
            _wantOnline = false;
            _connecting = true;
            _account = request.Username.Trim();
            _server = null;
            SetPhase(ConnectionPhase.LoggingIn, "Logging in...");
            AddLog(EngineLogLevel.Info, "engine", $"Logging in as {_account}.");

            _session.Options.ClientVersion = string.IsNullOrWhiteSpace(request.ClientVersion) ? null : request.ClientVersion.Trim();
            AqwCredentials credentials = new(request.Username, request.Password);
            LoginResult account = await _session.LoginAsync(credentials, cancellationToken).ConfigureAwait(false);
            _scrubber.Add(account.Token);
            GameServer server = _session.PickServer(string.IsNullOrWhiteSpace(request.Server) ? null : request.Server.Trim());
            _server = server.Name;
            SetPhase(ConnectionPhase.Connecting, $"Connecting to {server.Name}...");

            await _session.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
            _scrubber.Add(_session.Account?.Token);
            _wantOnline = true;
            SetPhase(ConnectionPhase.Online, $"Online on {server.Name}");
            AddLog(EngineLogLevel.Info, "engine", $"Online on {server.Name} (client {_session.ClientVersion?.Version}).");
        }
        catch (AqwLoginException e)
        {
            Fail($"Login failed: {Scrub(e.Message)}");
            await DisconnectQuietlyAsync().ConfigureAwait(false);
            throw new EngineException($"Login failed: {Scrub(e.Message)}", e);
        }
        catch (OperationCanceledException)
        {
            SetPhase(ConnectionPhase.Offline, "Login cancelled");
            await DisconnectQuietlyAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (IsNetworkFailure(e))
        {
            Fail($"Connection failed: {Scrub(e.Message)}");
            await DisconnectQuietlyAsync().ConfigureAwait(false);
            throw new EngineException($"Connection failed: {Scrub(e.Message)}", e);
        }
        finally
        {
            _connecting = false;
            _opLock.Release();
            PublishNow();
        }
    }

    // Skua's login code (through the host) instead of the session's: same phases, messages and secret handling.
    private async Task LoginThroughHostAsync(ISessionScriptHost host, LoginRequest request, CancellationToken cancellationToken)
    {
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _scrubber.Add(request.Password);
            _connecting = true;
            if (_session.Connection is { State: not ConnectionState.Closed })
                await LogoutThroughHostQuietlyAsync(host).ConfigureAwait(false);

            _wantOnline = false;
            _account = request.Username.Trim();
            _server = null;
            SetPhase(ConnectionPhase.LoggingIn, "Logging in...");
            AddLog(EngineLogLevel.Info, "engine", $"Logging in as {_account}.");

            string server = await host.LoginAsync(request, name =>
            {
                _scrubber.Add(_session.Account?.Token);
                _server = name;
                SetPhase(ConnectionPhase.Connecting, $"Connecting to {name}...");
            }, cancellationToken).ConfigureAwait(false);
            _scrubber.Add(_session.Account?.Token);
            _server = server;
            _wantOnline = true;
            SetPhase(ConnectionPhase.Online, $"Online on {server}");
            AddLog(EngineLogLevel.Info, "engine", $"Online on {server} (client {_session.ClientVersion?.Version}).");
        }
        catch (EngineException e)
        {
            Fail(Scrub(e.Message));
            await LogoutThroughHostQuietlyAsync(host).ConfigureAwait(false);
            throw new EngineException(Scrub(e.Message), e);
        }
        catch (OperationCanceledException)
        {
            SetPhase(ConnectionPhase.Offline, "Login cancelled");
            await LogoutThroughHostQuietlyAsync(host).ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (e is AqwLoginException || IsNetworkFailure(e))
        {
            string message = e is AqwLoginException ? $"Login failed: {Scrub(e.Message)}" : $"Connection failed: {Scrub(e.Message)}";
            Fail(message);
            await LogoutThroughHostQuietlyAsync(host).ConfigureAwait(false);
            throw new EngineException(message, e);
        }
        finally
        {
            _connecting = false;
            _opLock.Release();
            PublishNow();
        }
    }

    private async Task LogoutThroughHostQuietlyAsync(ISessionScriptHost host)
    {
        try
        {
            await host.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is EngineException or InvalidOperationException || IsNetworkFailure(e))
        {
            AddLog(EngineLogLevel.Debug, "engine", $"Logout: {Scrub(e.Message)}");
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        RaiseStopRequested(EngineStopRequest.Logout);
        CancelReconnect();
        _wantOnline = false;
        if (_scripts.Status.IsBusy)
            await StopScriptCoreAsync(cancellationToken).ConfigureAwait(false);

        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (_sessionHost is ISessionScriptHost host)
            {
                // The host keeps the session and drops the credentials, token and server list in it.
                _loggingOut = true;
                try
                {
                    await LogoutThroughHostQuietlyAsync(host).ConfigureAwait(false);
                }
                finally
                {
                    _loggingOut = false;
                }
                _scrubber.Clear();
                _lastToken = null;
                _account = null;
                _server = null;
                SetPhase(ConnectionPhase.Offline, "Logged out");
                AddLog(EngineLogLevel.Info, "engine", "Logged out.");
                return;
            }
            AqwSession old = _session;
            DetachSession(old);
            try
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (IsNetworkFailure(e) || e is InvalidOperationException)
            {
                AddLog(EngineLogLevel.Debug, "engine", $"Closing the old session: {Scrub(e.Message)}");
            }

            // A fresh session drops the credentials the old one kept for reconnects.
            _session = CreateSession();
            _scrubber.Clear();
            _account = null;
            _server = null;
            SetPhase(ConnectionPhase.Offline, "Logged out");
            AddLog(EngineLogLevel.Info, "engine", "Logged out.");
        }
        finally
        {
            _opLock.Release();
            PublishNow();
        }
    }

    internal const string GameViewHasTheAccount = "The game view has the account: close it first.";

    public bool GameViewHold => _gameViewHold;

    public async Task HoldForGameViewAsync(CancellationToken cancellationToken = default)
    {
        if (_scripts.Status.IsBusy)
            throw new EngineException("Stop the script before opening the game view.");
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_gameViewHold)
                return;
            if (_scripts.Status.IsBusy)
                throw new EngineException("Stop the script before opening the game view.");
            // First, before anything can drop: from here on no reconnect starts (OnSessionDisconnected, the loop).
            _gameViewHold = true;
            CancelReconnect();
            _holdWasOnline = _account is not null && (_wantOnline || _phase is ConnectionPhase.Online or ConnectionPhase.Reconnecting or ConnectionPhase.Connecting);
            _wantOnline = false;
            if (_sessionHost is ISessionScriptHost host)
                host.AutoRelogin = false;
            _loggingOut = true;
            try
            {
                if (_sessionHost is ISessionScriptHost h)
                    await h.ReleaseSessionAsync(cancellationToken).ConfigureAwait(false);
                else
                    await DisconnectQuietlyAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is EngineException or InvalidOperationException or TimeoutException || IsNetworkFailure(e))
            {
                AddLog(EngineLogLevel.Debug, "engine", $"Logout for the game view: {Scrub(e.Message)}");
            }
            finally
            {
                _loggingOut = false;
            }
            if (_session.Connection is { State: not ConnectionState.Closed })
                await DisconnectQuietlyAsync().ConfigureAwait(false);
            SetPhase(ConnectionPhase.GameView, "The game view has the account.");
            AddLog(EngineLogLevel.Info, "engine", _holdWasOnline
                ? "Logged out for the game view; reconnects wait until it closes."
                : "The game view has the account; nothing to reconnect afterwards (not logged in).");
        }
        catch
        {
            if (_gameViewHold && _phase != ConnectionPhase.GameView)
            {
                // Cancelled or failed before the hand-over completed: the engine owns the account again.
                _gameViewHold = false;
                if (_sessionHost is ISessionScriptHost host)
                    host.AutoRelogin = _options.AutoReconnect;
                _wantOnline = _holdWasOnline;
            }
            throw;
        }
        finally
        {
            _opLock.Release();
            PublishNow();
        }
    }

    public async Task EndGameViewHoldAsync(bool reconnect, CancellationToken cancellationToken = default)
    {
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool startLoop = false;
        try
        {
            if (!_gameViewHold || Volatile.Read(ref _disposed) != 0)
                return;
            _gameViewHold = false;
            if (_sessionHost is ISessionScriptHost host)
                host.AutoRelogin = _options.AutoReconnect;
            if (!reconnect || !_holdWasOnline || _account is null)
            {
                _wantOnline = false;
                SetPhase(ConnectionPhase.Offline, "Logged out (the game view had the account).");
                AddLog(EngineLogLevel.Info, "engine", "The game view closed; the engine has the account again (not logged in).");
                return;
            }
            _wantOnline = true;
            _connecting = true;
            SetPhase(ConnectionPhase.Connecting, "Logging back in after the game view...");
            AddLog(EngineLogLevel.Info, "engine", "The game view closed; logging back in.");
            try
            {
                if (_sessionHost is ISessionScriptHost h)
                    await h.ReconnectAsync(_server, cancellationToken).ConfigureAwait(false);
                else
                    await _session.ReconnectAsync(cancellationToken).ConfigureAwait(false);
                _scrubber.Add(_session.Account?.Token);
                _server = _session.Server?.Name ?? _server;
                SetPhase(ConnectionPhase.Online, $"Online on {_server}");
                AddLog(EngineLogLevel.Info, "engine", $"Back online on {_server} after the game view.");
            }
            catch (AqwLoginException e) when (e.Kind == LoginFailureKind.Rejected)
            {
                _wantOnline = false;
                Fail($"Logging back in after the game view was refused: {Scrub(e.Message)}");
            }
            catch (Exception e) when (e is AqwLoginException or EngineException or InvalidOperationException || IsNetworkFailure(e))
            {
                AddLog(EngineLogLevel.Warning, "engine", $"Logging back in after the game view failed: {Scrub(e.Message)}");
                if (_options.AutoReconnect)
                {
                    SetPhase(ConnectionPhase.Reconnecting, "Reconnecting after the game view...");
                    startLoop = true;
                }
                else
                {
                    _wantOnline = false;
                    Fail($"Logging back in after the game view failed: {Scrub(e.Message)}");
                }
            }
            finally
            {
                _connecting = false;
            }
        }
        finally
        {
            _opLock.Release();
            PublishNow();
        }
        if (startLoop)
            StartReconnectLoop();
    }

    public async Task JoinMapAsync(string map, string cell = "Enter", string pad = "Spawn", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(map))
            throw new EngineException("Enter a map name.");
        GameCommands commands = RequireOnline();
        try
        {
            AreaInfo area = await commands.JoinMapAsync(map.Trim(), string.IsNullOrWhiteSpace(cell) ? "Enter" : cell.Trim(), string.IsNullOrWhiteSpace(pad) ? "Spawn" : pad.Trim(), cancellationToken: cancellationToken).ConfigureAwait(false);
            AddLog(EngineLogLevel.Info, "engine", $"Joined {area.AreaName}.");
        }
        catch (JoinFailedException e)
        {
            throw new EngineException(Scrub(e.Message), e);
        }
        catch (Exception e) when (e is TimeoutException or ConnectionClosedException)
        {
            throw new EngineException($"Join failed: {Scrub(e.Message)}", e);
        }
        finally
        {
            MarkDirty();
        }
    }

    public Task<bool> SendPacketAsync(string packet, CancellationToken cancellationToken = default) => SendPacketAsync(packet, log: true, cancellationToken);

    public async Task<bool> SendPacketAsync(string packet, bool log, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packet))
            throw new EngineException("The packet is empty.");
        GameCommands commands = RequireOnline();
        string text = packet.TrimEnd('\r', '\n');
        bool sent = await commands.SendRawAsync(text).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (log || !sent)
            AddLog(sent ? EngineLogLevel.Info : EngineLogLevel.Warning, "packets", sent ? "Raw packet sent." : "Raw packet was dropped (queue full or disconnected).");
        return sent;
    }

    public async Task LoadBankAsync(CancellationToken cancellationToken = default)
    {
        GameCommands commands = RequireOnline();
        try
        {
            int count = await commands.EnsureBankLoadedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            AddLog(EngineLogLevel.Info, "engine", $"Bank loaded: {count} items.");
        }
        catch (TimeoutException e)
        {
            throw new EngineException(Scrub(e.Message), e);
        }
        finally
        {
            MarkDirty();
        }
    }

    public WorldLists GetWorldLists()
    {
        WorldModel w = _session.World;
        if (w.MyUsername is null)
            return WorldLists.Empty;
        return new WorldLists(
            w.GetInventory(raw: false).Select(ToRow).ToList(),
            w.GetTempInventory(raw: false).Select(ToRow).ToList(),
            w.IsBankLoaded ? BankRows(w) : [],
            w.IsBankLoaded,
            w.GetQuests()
                .Where(q => q.Loaded || q.Accepted)
                .Select(q => new QuestRow(q.QuestId, string.IsNullOrEmpty(q.Name) ? $"Quest {q.QuestId}" : q.Name, q.Accepted, q.Status == "c", q.Level, q.Member))
                .OrderByDescending(q => q.Accepted)
                .ThenBy(q => q.QuestId)
                .ToList(),
            w.GetDrops().Select(d => new DropRow(d.ItemId, d.Name, d.Quantity, d.TimesOffered)).ToList(),
            w.GetMonsters().Select(m => new MonsterRow(m.MonMapId, m.Name, m.Level, m.Cell, m.Hp, m.HpMax, m.IsAlive)).ToList(),
            w.Version);
    }

    public void LoadScript(string scriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        _scripts.Load(scriptPath);
        MarkDirty();
    }

    public Task StartScriptAsync(string? scriptPath = null, CancellationToken cancellationToken = default) =>
        StartScriptCoreAsync(scriptPath, ScriptStartOptions.Default, cancellationToken);

    public Task StartScriptAsync(string scriptPath, ScriptStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentNullException.ThrowIfNull(options);
        return StartScriptCoreAsync(scriptPath, options, cancellationToken);
    }

    private async Task StartScriptCoreAsync(string? scriptPath, ScriptStartOptions options, CancellationToken cancellationToken)
    {
        if (!_scripts.Support.Available)
            throw new EngineException(_scripts.Support.Reason ?? "Scripts are not available.");
        if (_scripts.Status.IsBusy)
            throw new EngineException("A script is already running.");
        if (_gameViewHold)
            throw new EngineException(GameViewHasTheAccount);
        if (_connecting)
            throw new EngineException("Wait for the login to finish.");
        if (scriptPath is not null)
            _scripts.Load(scriptPath);
        if (_scripts.Status.ScriptPath is null)
            throw new EngineException("Choose a script first.");
        // Registered before the first await: whoever sees this call made (the queue marks its entry running) and then
        // pauses finds the start, and the pause waits for it instead of failing with "No script is running".
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _starting, started.Task);
        try
        {
            AddLog(EngineLogLevel.Info, "scripts", $"Starting {Path.GetFileName(_scripts.Status.ScriptPath)}{(options.OptionStorage is string storage ? $" (options {storage})" : string.Empty)}.");
            if (ReferenceEquals(options, ScriptStartOptions.Default))
                await _scripts.StartAsync(cancellationToken).ConfigureAwait(false);
            else
                await _scripts.StartAsync(options, cancellationToken).ConfigureAwait(false);
            PublishNow();
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _starting, null, started.Task); // returns the previous value, not an operation
            started.TrySetResult();
        }
    }

    public Task StopScriptAsync(CancellationToken cancellationToken = default)
    {
        RaiseStopRequested(EngineStopRequest.Script);
        return StopScriptCoreAsync(cancellationToken);
    }

    private void RaiseStopRequested(EngineStopRequest request)
    {
        try
        {
            StopRequested?.Invoke(this, request);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or EngineException)
        {
            AddLog(EngineLogLevel.Warning, "engine", $"Stop request handler failed: {e.Message}");
        }
    }

    public bool CanPauseScript => _scripts.CanPause;

    public Task PauseScriptAsync(CancellationToken cancellationToken = default) => SetPausedAsync(true, cancellationToken);

    public Task ResumeScriptAsync(CancellationToken cancellationToken = default) => SetPausedAsync(false, cancellationToken);

    private async Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        if (!_scripts.CanPause)
            throw new EngineException("Pausing scripts is not available in this engine.");
        // Pause pressed right after Start (or by the queue just after it started an entry): the start is still in
        // progress (compiling); wait for it, then pause the script it started. A failed start is reported to its caller.
        if (Volatile.Read(ref _starting) is Task starting && _scripts.Status.State != ScriptRunState.Running)
            await starting.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_scripts.Status.State == ScriptRunState.Compiling)
            throw new EngineException("The script is still starting.");
        if (_scripts.Status.State != ScriptRunState.Running)
            throw new EngineException("No script is running.");
        if (_scripts.Status.Paused == paused)
            return;
        bool applied = await _scripts.SetPausedAsync(paused, cancellationToken).ConfigureAwait(false);
        PublishNow();
        if (!applied)
            throw new EngineException(paused ? "The script could not be paused (it is stopping)." : "The script could not be resumed (it stopped or was paused again).");
    }

    private async Task StopScriptCoreAsync(CancellationToken cancellationToken)
    {
        if (!_scripts.Status.IsBusy)
            return;
        AddLog(EngineLogLevel.Info, "scripts", "Stopping the script.");
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(_options.ScriptStopTimeout);
        try
        {
            await _scripts.StopAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AddLog(EngineLogLevel.Warning, "scripts", "The script did not stop in time.");
        }
        PublishNow();
    }

    public Task<ScriptOptionSet> GetScriptOptionsAsync(string scriptPath, CancellationToken cancellationToken = default) =>
        GetScriptOptionsAsync(scriptPath, null, cancellationToken);

    public async Task<ScriptOptionSet> GetScriptOptionsAsync(string scriptPath, string? optionStorage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ISessionScriptHost host = _sessionHost ?? throw new EngineException(_scripts.Support.Reason ?? "Script options are not available.");
        if (_scripts.Status.IsBusy)
            throw new EngineException("Stop the script before editing options.");
        return await host.GetOptionsAsync(scriptPath, optionStorage, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveScriptOptionsAsync(ScriptOptionSet options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ISessionScriptHost host = _sessionHost ?? throw new EngineException(_scripts.Support.Reason ?? "Script options are not available.");
        await host.SaveOptionsAsync(options, cancellationToken).ConfigureAwait(false);
        AddLog(EngineLogLevel.Info, "scripts", $"Saved the options of {Path.GetFileName(options.ScriptPath)} ({options.Storage}).");
    }

    public IDisposable BeginBackgroundWork(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        lock (_work)
            _work.Add(description);
        PublishNow();
        return new WorkRegistration(this, description);
    }

    private void EndBackgroundWork(string description)
    {
        lock (_work)
            _work.Remove(description);
        PublishNow();
    }

    private sealed class WorkRegistration : IDisposable
    {
        private HeadlessBotEngine? _engine;
        private readonly string _description;

        public WorkRegistration(HeadlessBotEngine engine, string description)
        {
            _engine = engine;
            _description = description;
        }

        public void Dispose() => Interlocked.Exchange(ref _engine, null)?.EndBackgroundWork(_description);
    }

    // ------------------------------------------------------------------ session wiring

    private AqwSession CreateSession()
    {
        AqwSession? owned = null;
        if (_sessionHost is ISessionScriptHost host)
        {
            owned = host.OpenSession();
            if (owned is null)
            {
                AddLog(EngineLogLevel.Error, "engine", "The script host did not start; scripts are unavailable.");
                _sessionHost = null;
            }
            else
            {
                host.AutoRelogin = _options.AutoReconnect;
            }
        }
        AqwSession session = owned ?? _options.SessionFactory();
        // Every connection of this session offers its game messages to the interceptor (which passes them on
        // untouched while it is off); AqwConnection keeps the handshake and the token out of its reach.
        session.Options.Connection.Interceptor = _interception;
        session.StateChanged += OnSessionStateChanged;
        session.Disconnected += OnSessionDisconnected;
        session.Log += OnSessionLog;
        session.PacketReceived += OnPacketReceived;
        session.PacketSent += OnPacketSent;
        session.World.Changed += OnWorldChanged;
        if (owned is null)
            _scripts.Attach(session);
        return session;
    }

    private void DetachSession(AqwSession session)
    {
        session.StateChanged -= OnSessionStateChanged;
        session.Disconnected -= OnSessionDisconnected;
        session.Log -= OnSessionLog;
        session.PacketReceived -= OnPacketReceived;
        session.PacketSent -= OnPacketSent;
        session.World.Changed -= OnWorldChanged;
    }

    private void OnSessionStateChanged(object? sender, ConnectionState state)
    {
        MarkDirty();
        // Skua (a script's relogin or server change) brought the session back without the engine.
        if (state == ConnectionState.Ready && _sessionHost is not null && ReferenceEquals(sender, _session)
            && !_connecting && !_loggingOut && !_gameViewHold && _account is not null && _phase != ConnectionPhase.Online)
        {
            _wantOnline = true;
            _server = _session.Server?.Name;
            _scrubber.Add(_session.Account?.Token);
            SetPhase(ConnectionPhase.Online, $"Online on {_server}");
            AddLog(EngineLogLevel.Info, "engine", $"Online on {_server}.");
        }
    }

    private void OnWorldChanged(object? sender, WorldChangedEventArgs e) => MarkDirty();

    private void OnSessionLog(object? sender, HeadlessLogEventArgs e)
    {
        EngineLogLevel level = e.Level switch
        {
            HeadlessLogLevel.Debug => EngineLogLevel.Debug,
            HeadlessLogLevel.Info => EngineLogLevel.Info,
            HeadlessLogLevel.Warning => EngineLogLevel.Warning,
            _ => EngineLogLevel.Error,
        };
        AddLog(level, "session", e.Message);
    }

    private void OnPacketReceived(object? sender, InboundPacket packet)
    {
        if (_packetCapture)
            AddPacket(PacketDirection.In, packet.Text);
    }

    private void OnPacketSent(object? sender, PacketSentEventArgs e)
    {
        // The host's login code logs in over HTTP again on its own: register each new socket token before it is shown.
        string? token = _session.Account?.Token;
        if (token is not null && !ReferenceEquals(token, _lastToken))
        {
            _lastToken = token;
            _scrubber.Add(token);
        }
        if (_packetCapture)
            AddPacket(PacketDirection.Out, e.Text);
    }

    private void OnSessionDisconnected(object? sender, DisconnectInfo info)
    {
        MarkDirty();
        if (_connecting || _loggingOut || _gameViewHold || !ReferenceEquals(sender, _session))
            return;

        if (_sessionHost is ISessionScriptHost host && _wantOnline)
        {
            if (host.OwnsReconnect)
            {
                // A script is busy or Skua's relogin runs: Skua reconnects. The engine's loop (if on) only waits as a fallback.
                AddLog(EngineLogLevel.Warning, "engine", $"Disconnected: {Describe(info)} Skua's relogin takes over while the script runs.");
                SetPhase(ConnectionPhase.Reconnecting, "Reconnecting (script relogin)...");
                if (_options.AutoReconnect)
                    StartReconnectLoop();
                return;
            }
            if (info.Intentional)
            {
                // Skua logged out on purpose (for example a stuck map load with relogin off).
                _wantOnline = false;
                SetPhase(ConnectionPhase.Offline, "Disconnected");
                AddLog(EngineLogLevel.Warning, "engine", "Disconnected by Skua.");
                return;
            }
        }
        if (info.Intentional)
            return;

        AddLog(EngineLogLevel.Warning, "engine", $"Disconnected: {Describe(info)}");
        if (_wantOnline && _options.AutoReconnect && info.ShouldReconnect)
        {
            StartReconnectLoop();
            return;
        }
        _wantOnline = false;
        Fail(Describe(info));
    }

    private void StartReconnectLoop()
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _reconnectCts, cts);
        previous?.Cancel();
        _ = Task.Run(() => ReconnectLoopAsync(cts.Token));
    }

    private void CancelReconnect()
    {
        Interlocked.Exchange(ref _reconnectCts, null)?.Cancel();
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= _options.MaxReconnectAttempts; attempt++)
        {
            if (_gameViewHold || cancellationToken.IsCancellationRequested)
                return; // the game view took the account (or a login or logout cancelled the loop)
            TimeSpan delay = _options.ReconnectDelays.Count == 0
                ? TimeSpan.FromSeconds(10)
                : _options.ReconnectDelays[Math.Min(attempt - 1, _options.ReconnectDelays.Count - 1)];
            SetPhase(ConnectionPhase.Reconnecting, $"Reconnecting in {delay.TotalSeconds:0}s (attempt {attempt})");
            IAsyncDisposable? gate = null;
            try
            {
            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                if (_options.LoginGate is { } loginGate)
                    gate = await loginGate(cancellationToken).ConfigureAwait(false);
                // Never fight Skua: while a script is busy or Skua's relogin runs, it reconnects and this loop waits.
                while (_sessionHost is { OwnsReconnect: true } && _session.Connection is not { IsReady: true })
                {
                    SetPhase(ConnectionPhase.Reconnecting, "Reconnecting (script relogin)...");
                    await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
                }
                if (_session.Connection is { IsReady: true })
                {
                    if (_phase != ConnectionPhase.Online)
                    {
                        _server = _session.Server?.Name;
                        SetPhase(ConnectionPhase.Online, $"Online on {_server}");
                    }
                    return;
                }
                await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (!_wantOnline || _gameViewHold)
                    return;
                _connecting = true;
                SetPhase(ConnectionPhase.Connecting, $"Reconnecting (attempt {attempt})...");
                if (_sessionHost is ISessionScriptHost host)
                    await host.ReconnectAsync(_server, cancellationToken).ConfigureAwait(false);
                else
                    await _session.ReconnectAsync(cancellationToken).ConfigureAwait(false);
                _scrubber.Add(_session.Account?.Token);
                _server = _session.Server?.Name;
                SetPhase(ConnectionPhase.Online, $"Online on {_server}");
                AddLog(EngineLogLevel.Info, "engine", $"Reconnected to {_server}.");
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (AqwLoginException e) when (e.Kind == LoginFailureKind.Rejected)
            {
                _wantOnline = false;
                Fail($"Reconnect refused: {Scrub(e.Message)}");
                return;
            }
            catch (Exception e) when (e is AqwLoginException or EngineException || IsNetworkFailure(e))
            {
                AddLog(EngineLogLevel.Warning, "engine", $"Reconnect attempt {attempt} failed: {Scrub(e.Message)}");
            }
            finally
            {
                _connecting = false;
                _opLock.Release();
                PublishNow();
            }
            }
            finally
            {
                // The army's login turn ends with this attempt, whatever its outcome.
                if (gate is not null)
                    await gate.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (_gameViewHold || cancellationToken.IsCancellationRequested)
            return;
        _wantOnline = false;
        Fail($"Gave up reconnecting after {_options.MaxReconnectAttempts} attempts.");
    }

    private async Task DisconnectQuietlyAsync()
    {
        try
        {
            await _session.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (IsNetworkFailure(e) || e is InvalidOperationException)
        {
            AddLog(EngineLogLevel.Debug, "engine", $"Disconnect: {Scrub(e.Message)}");
        }
    }

    private GameCommands RequireOnline()
    {
        if (_session.Connection is not { IsReady: true })
            throw new EngineException("Not connected.");
        return _session.Commands;
    }

    // ------------------------------------------------------------------ scripts

    private void OnScriptStatus(object? sender, ScriptStatus status)
    {
        _scriptStatus = status;
        if (status.State == ScriptRunState.Faulted && status.Message is string message)
            AddLog(EngineLogLevel.Error, "scripts", message);
        // Script state changes are rare (a few per run): publish at once, so the snapshot never lags a start, a stop
        // or a pause (the script queue and the notification read it).
        if (Volatile.Read(ref _disposed) == 0 && _session is not null)
            PublishNow();
        else
            MarkDirty();
    }

    private void OnScriptLog(object? sender, LogEntry entry)
    {
        AddLog(entry.Level, entry.Source, entry.Message);
    }

    // ------------------------------------------------------------------ snapshots, log, packets

    private void SetPhase(ConnectionPhase phase, string? status)
    {
        _phase = phase;
        _statusText = status;
        MarkDirty();
    }

    private void Fail(string message)
    {
        AddLog(EngineLogLevel.Error, "engine", message);
        SetPhase(ConnectionPhase.Failed, message);
    }

    private void MarkDirty() => Volatile.Write(ref _dirty, 1);

    private void AddLog(EngineLogLevel level, string source, string message)
    {
        LogEntry entry = new(_time.GetUtcNow(), level, source, Scrub(message));
        _log.Add(entry);
        LogAdded?.Invoke(this, entry);
    }

    private void AddPacket(PacketDirection direction, string text)
    {
        string clean = Scrub(text.TrimEnd('\0'));
        if (clean.Length > _options.MaxPacketChars)
            clean = string.Concat(clean.AsSpan(0, _options.MaxPacketChars), $"... ({clean.Length} chars)");
        PacketEntry entry = new(_time.GetUtcNow(), direction, clean);
        _packets.Add(entry);
        PacketLogged?.Invoke(this, entry);
    }

    private string Scrub(string text) => _scrubber.Scrub(text);

    private async Task PublishLoopAsync()
    {
        DateTimeOffset nextMemory = _time.GetUtcNow() + _options.MemorySampleInterval;
        using PeriodicTimer timer = new(_options.PublishInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (_time.GetUtcNow() >= nextMemory)
                {
                    SampleMemory();
                    nextMemory = _time.GetUtcNow() + _options.MemorySampleInterval;
                }
                if (Interlocked.Exchange(ref _dirty, 0) == 1)
                    PublishNow();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SampleMemory()
    {
        try
        {
            MemorySample sample = _memoryProbe.Read();
            _memory = new MemoryStatus(sample.RssBytes, sample.PeakRssBytes, sample.ManagedBytes, _scripts.LoadedAssemblyCount, _time.GetUtcNow());
            MarkDirty();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddLog(EngineLogLevel.Debug, "engine", $"Memory sample failed: {e.Message}");
        }
    }

    /// <summary>Builds and publishes a snapshot now (also used by the coalescing loop).</summary>
    internal void PublishNow()
    {
        EngineSnapshot snapshot;
        lock (_snapshotLock)
        {
            snapshot = BuildSnapshot();
            _snapshot = snapshot;
        }
        try
        {
            SnapshotChanged?.Invoke(this, snapshot);
        }
        catch (Exception e)
        {
            // A broken subscriber must not stop the publisher loop.
            _log.Add(new LogEntry(_time.GetUtcNow(), EngineLogLevel.Error, "engine", $"Snapshot subscriber failed: {e.GetType().Name}: {Scrub(e.Message)}"));
        }
    }

    private EngineSnapshot BuildSnapshot()
    {
        AqwSession session = _session;
        WorldModel w = session.World;
        CharacterState? character = null;
        if (session.Connection is { State: not ConnectionState.Closed } && w.GetPlayer() is PlayerInfo me)
        {
            AreaInfo area = w.GetArea();
            character = new CharacterState(
                me.Username,
                me.Level,
                me.ClassName,
                me.Hp,
                me.HpMax,
                me.Mp,
                me.MpMax,
                me.Gold,
                string.IsNullOrEmpty(area.MapName) ? area.AreaName : area.MapName,
                area.Cell,
                area.Pad,
                area.UserCount,
                me.IsAlive,
                me.InCombat,
                me.Afk);
        }

        string? work;
        lock (_work)
            work = _work.Count == 0 ? null : string.Join("; ", _work);

        return new EngineSnapshot(
            _phase,
            _statusText,
            _account,
            _server,
            character,
            _scriptStatus,
            _memory,
            w.Version,
            _time.GetUtcNow())
        {
            ScriptCounters = ReadCounters(),
            BackgroundWork = work,
        };
    }

    private ScriptCounters? ReadCounters()
    {
        try
        {
            return _sessionHost?.Counters;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or NullReferenceException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static ServerChoice ToChoice(GameServer s) => new(s.Name, s.Players, s.MaxPlayers, s.Online, s.MembersOnly, s.MinLevel, s.Language);

    private sealed record BankRowCache(WorldModel World, long Version, IReadOnlyList<InventoryRow> Rows);

    private volatile BankRowCache? _bankRows;

    // The bank's rows, rebuilt only when the bank changed (WorldModel.BankVersion): a 3000-item bank copied 12 MB per
    // GetWorldLists, and the World page asks after every world change. The same list instance is returned while the
    // bank is unchanged, so the page can skip filtering and sorting it again.
    private IReadOnlyList<InventoryRow> BankRows(WorldModel w)
    {
        BankRowCache? cached = _bankRows;
        long version = w.BankVersion;
        if (cached is not null && ReferenceEquals(cached.World, w) && cached.Version == version)
            return cached.Rows;
        IReadOnlyList<InventoryRow> rows = w.GetBank(raw: false).Select(ToRow).ToList();
        _bankRows = new BankRowCache(w, version, rows);
        return rows;
    }

    /// <summary>Drops the cached bank rows (memory pressure); the next read rebuilds them.</summary>
    public void TrimCaches() => _bankRows = null;

    private static InventoryRow ToRow(ItemInfo i) => new(i.ItemId, i.Name, i.Quantity, i.MaxStack, i.Type, i.EquipSlot, i.Equipped, i.Temp, i.Member);

    private static bool IsNetworkFailure(Exception e) =>
        e is IOException or SocketException or TimeoutException or HttpRequestException or ConnectionClosedException;

    private string Describe(DisconnectInfo info) => info.Reason switch
    {
        DisconnectReason.LoggedInElsewhere => "This account logged in somewhere else.",
        DisconnectReason.Kicked => "The server logged this character out.",
        DisconnectReason.LivenessTimeout => "The server stopped answering.",
        DisconnectReason.RemoteClosed => "The server closed the connection.",
        DisconnectReason.TransportError => $"Network error: {Scrub(info.Message)}",
        _ => Scrub(info.Message),
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CancelReconnect();
        _wantOnline = false;
        try
        {
            if (_scripts.Status.IsBusy)
                await StopScriptCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is EngineException or InvalidOperationException)
        {
        }
        _lifetime.Cancel();
        try
        {
            await _publisher.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _scripts.StatusChanged -= OnScriptStatus;
        _scripts.Log -= OnScriptLog;
        DetachSession(_session);
        await _scripts.DisposeAsync().ConfigureAwait(false);
        if (_sessionHost is null)
            await _session.DisposeAsync().ConfigureAwait(false); // a session host disposes the session it created
        _scrubber.Clear();
        _lifetime.Dispose();
    }
}
