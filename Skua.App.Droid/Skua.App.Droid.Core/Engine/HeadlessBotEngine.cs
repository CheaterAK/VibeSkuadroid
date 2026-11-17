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
/// The engine owns exactly one <see cref="AqwSession"/> at a time. A logout disposes it (dropping the credentials it
/// holds for reconnects) and creates a fresh one, which is handed to the script host again via
/// <see cref="IScriptHost.Attach"/>.
/// </remarks>
public sealed class HeadlessBotEngine : IBotEngine
{
    private readonly HeadlessEngineOptions _options;
    private readonly IScriptHost _scripts;
    private readonly IMemoryProbe _memoryProbe;
    private readonly TimeProvider _time;
    private readonly SecretScrubber _scrubber = new();
    private readonly RingBuffer<LogEntry> _log;
    private readonly RingBuffer<PacketEntry> _packets;
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly object _snapshotLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _publisher;

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
    private CancellationTokenSource? _reconnectCts;
    private int _dirty = 1;
    private int _disposed;

    public HeadlessBotEngine(HeadlessEngineOptions? options = null, IScriptHost? scriptHost = null, IMemoryProbe? memoryProbe = null, TimeProvider? time = null)
    {
        _options = options ?? new HeadlessEngineOptions();
        _scripts = scriptHost ?? new UnavailableScriptHost();
        _memoryProbe = memoryProbe ?? new ProcMemoryProbe();
        _time = time ?? TimeProvider.System;
        _log = new RingBuffer<LogEntry>(_options.LogCapacity);
        _packets = new RingBuffer<PacketEntry>(_options.PacketCapacity);
        _scriptStatus = _scripts.Status;
        _scripts.StatusChanged += OnScriptStatus;
        _scripts.Log += OnScriptLog;

        _session = CreateSession();
        SampleMemory();
        PublishNow();
        _publisher = Task.Run(PublishLoopAsync);
    }

    public EngineSnapshot Snapshot => _snapshot;

    public event EventHandler<EngineSnapshot>? SnapshotChanged;

    public event EventHandler<LogEntry>? LogAdded;

    public event EventHandler<PacketEntry>? PacketLogged;

    public ScriptSupport ScriptSupport => _scripts.Support;

    public bool AutoReconnect
    {
        get => _options.AutoReconnect;
        set => _options.AutoReconnect = value;
    }

    public bool PacketCaptureEnabled
    {
        get => _packetCapture;
        set => _packetCapture = value;
    }

    /// <summary>The live session (for the host adapter and tests). Replaced after each logout.</summary>
    public AqwSession Session => _session;

    public IReadOnlyList<LogEntry> GetRecentLog() => _log.Snapshot();

    public void ClearLog() => _log.Clear();

    public IReadOnlyList<PacketEntry> GetRecentPackets() => _packets.Snapshot();

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

        CancelReconnect();
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

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        CancelReconnect();
        _wantOnline = false;
        if (_scripts.Status.IsBusy)
            await StopScriptAsync(cancellationToken).ConfigureAwait(false);

        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
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

    public async Task<bool> SendPacketAsync(string packet, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packet))
            throw new EngineException("The packet is empty.");
        GameCommands commands = RequireOnline();
        string text = packet.TrimEnd('\r', '\n');
        bool sent = await commands.SendRawAsync(text).WaitAsync(cancellationToken).ConfigureAwait(false);
        AddLog(sent ? EngineLogLevel.Info : EngineLogLevel.Warning, "packets", sent ? "Raw packet sent." : "Raw packet was dropped (queue full or disconnected).");
        return sent;
    }

    public async Task LoadBankAsync(CancellationToken cancellationToken = default)
    {
        GameCommands commands = RequireOnline();
        try
        {
            IReadOnlyList<ItemInfo> bank = await commands.LoadBankAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            AddLog(EngineLogLevel.Info, "engine", $"Bank loaded: {bank.Count} items.");
        }
        catch (TimeoutException e)
        {
            throw new EngineException("The bank did not load in time.", e);
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
            w.GetInventory().Select(ToRow).ToList(),
            w.GetTempInventory().Select(ToRow).ToList(),
            w.IsBankLoaded ? w.GetBank().Select(ToRow).ToList() : [],
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

    public async Task StartScriptAsync(string? scriptPath = null, CancellationToken cancellationToken = default)
    {
        if (!_scripts.Support.Available)
            throw new EngineException(_scripts.Support.Reason ?? "Scripts are not available.");
        if (_scripts.Status.IsBusy)
            throw new EngineException("A script is already running.");
        if (scriptPath is not null)
            _scripts.Load(scriptPath);
        if (_scripts.Status.ScriptPath is null)
            throw new EngineException("Choose a script first.");
        AddLog(EngineLogLevel.Info, "scripts", $"Starting {Path.GetFileName(_scripts.Status.ScriptPath)}.");
        await _scripts.StartAsync(cancellationToken).ConfigureAwait(false);
        MarkDirty();
    }

    public async Task StopScriptAsync(CancellationToken cancellationToken = default)
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
        MarkDirty();
    }

    // ------------------------------------------------------------------ session wiring

    private AqwSession CreateSession()
    {
        AqwSession session = _options.SessionFactory();
        session.StateChanged += OnSessionStateChanged;
        session.Disconnected += OnSessionDisconnected;
        session.Log += OnSessionLog;
        session.PacketReceived += OnPacketReceived;
        session.PacketSent += OnPacketSent;
        session.World.Changed += OnWorldChanged;
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

    private void OnSessionStateChanged(object? sender, ConnectionState state) => MarkDirty();

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
        if (_packetCapture)
            AddPacket(PacketDirection.Out, e.Text);
    }

    private void OnSessionDisconnected(object? sender, DisconnectInfo info)
    {
        MarkDirty();
        if (info.Intentional || _connecting || !ReferenceEquals(sender, _session))
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
            TimeSpan delay = _options.ReconnectDelays.Count == 0
                ? TimeSpan.FromSeconds(10)
                : _options.ReconnectDelays[Math.Min(attempt - 1, _options.ReconnectDelays.Count - 1)];
            SetPhase(ConnectionPhase.Reconnecting, $"Reconnecting in {delay.TotalSeconds:0}s (attempt {attempt})");
            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (!_wantOnline)
                    return;
                _connecting = true;
                SetPhase(ConnectionPhase.Connecting, $"Reconnecting (attempt {attempt})...");
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
            catch (Exception e) when (e is AqwLoginException || IsNetworkFailure(e))
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

        return new EngineSnapshot(
            _phase,
            _statusText,
            _account,
            _server,
            character,
            _scriptStatus,
            _memory,
            w.Version,
            _time.GetUtcNow());
    }

    // ------------------------------------------------------------------ helpers

    private static ServerChoice ToChoice(GameServer s) => new(s.Name, s.Players, s.MaxPlayers, s.Online, s.MembersOnly, s.MinLevel, s.Language);

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
                await StopScriptAsync().ConfigureAwait(false);
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
        await _scripts.DisposeAsync().ConfigureAwait(false);
        DetachSession(_session);
        await _session.DisposeAsync().ConfigureAwait(false);
        _scrubber.Clear();
        _lifetime.Dispose();
    }
}
