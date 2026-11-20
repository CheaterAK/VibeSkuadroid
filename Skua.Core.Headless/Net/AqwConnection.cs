using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.World;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Skua.Core.Headless.Net;

/// <summary>
/// One socket session with an AQW game server: SmartFox handshake, login, character load, framed and paced
/// sending, a single reader loop feeding the <see cref="WorldModel"/>, liveness probing and disconnect signalling.
/// </summary>
/// <remarks>
/// <para>Login sequence (the decompiled Game4000r9.swf: SmartFoxClient.handleSocketConnection, Game.onConnectionHandler;
/// also Seraph and aqx, both MIT):</para>
/// <list type="number">
/// <item>verChk (<c>v='157'</c>) -> <c>apiOK</c></item>
/// <item>login (<c>SPIDER#0001~user lowercased~clientVersion</c>, the newest web login's token, zone_master) ->
/// <c>%xt%loginResponse%-1%true%uid%name%...</c></item>
/// <item><c>firstJoin</c> -> <c>moveToArea</c> -> <c>moveToCell</c> + <c>retrieveUserData</c></item>
/// <item><c>initUserData</c> -> <c>retrieveInventory</c> -> <c>loadInventoryBig</c>: ready.</item>
/// </list>
/// <para>
/// An instance is single use: after it closes, create a new one (sharing the same <see cref="WorldModel"/>, which
/// is reset on connect). Events are raised on the reader loop's thread, after the model has applied the packet;
/// a throwing subscriber is logged and counted, never allowed to stop the loop.
/// </para>
/// </remarks>
public sealed partial class AqwConnection : IAsyncDisposable
{
    private static readonly HashSet<string> s_expectReply = new(StringComparer.Ordinal)
    {
        "getQuests", "acceptQuest", "loadShop", "loadBank", "retrieveUserData", "tryQuestComplete",
        "loadHairShop", "buyItem", "sellItem", "bankFromInv", "bankToInv",
    };

    private static readonly HashSet<string> s_afkActions = new(StringComparer.Ordinal)
    {
        "gar", "moveToCell", "restRequest", "resPlayerTimed", "aggroMon", "mv",
    };

    // Requests the server always answers with a packet of a known command: the time between writing one and reading
    // its answer is the round trip (server work included) shown as latency.
    private static readonly Dictionary<string, string> s_replyOf = new(StringComparer.Ordinal)
    {
        ["retrieveUserData"] = "initUserData", ["getQuests"] = "getQuests", ["loadShop"] = "loadShop",
        ["acceptQuest"] = "acceptQuest", ["tryQuestComplete"] = "ccqr", ["loadBank"] = "loadBank",
        ["buyItem"] = "buyItem", ["sellItem"] = "sellItem", ["loadHairShop"] = "loadHairShop",
    };

    private readonly TimeProvider _time;
    private readonly SendPacer _pacer;
    private readonly PacketFramer _framer;
    private readonly SemaphoreSlim _sendSignal = new(0);
    private readonly object _waitGate = new();
    private readonly List<Waiter> _waiters = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DisconnectInfo> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LivenessMonitor _liveness;
    private Stream? _stream;
    private Task? _readTask;
    private Task? _writeTask;
    private Task? _livenessTask;
    private int _state = (int)ConnectionState.Created;
    private int _closing;
    private string _username = string.Empty;
    private string _token = string.Empty;
    private Func<SocketLogin> _login = null!;
    private string? _loginOrigin;
    private readonly object _redactGate = new();
    private readonly List<(string Secret, string Placeholder)> _redactions = new();
    private int _tracing;
    private long _socketOpenedAt;
    private int _actionId;
    private bool _firstAreaEntered;
    private bool _inventoryRequested;
    private DisconnectReason? _pendingReason;
    private string? _pendingMessage;
    private DateTimeOffset _lastAfkClear = DateTimeOffset.MinValue;
    private (string Map, string Cell, string Pad)? _requestedSpot;

    private long _packetsIn;
    private long _packetsOut;
    private long _bytesIn;
    private long _bytesOut;
    private long _compressedIn;
    private long _undecodableIn;
    private long _slowDowns;
    private long _afkClears;
    private long _probes;
    private long _subscriberErrors;
    private readonly object _rttGate = new();
    private readonly Dictionary<string, long> _awaitingReply = new(StringComparer.Ordinal);
    private long _lastRoundTripTicks = -1;
    private bool _interceptArmed;
    private long _interceptChanged;
    private long _interceptBlocked;
    private double _averageRoundTripMs = -1;

    public AqwConnection(AqwConnectionOptions? options = null, WorldModel? world = null, TimeProvider? time = null)
    {
        Options = options ?? new AqwConnectionOptions();
        _time = time ?? TimeProvider.System;
        World = world ?? new WorldModel(_time);
        _pacer = new SendPacer(Options.SendGap, Options.SlowSendGap, Options.SlowDownHold, Options.MaxQueuedPackets);
        _framer = new PacketFramer(Options.MaxFrameBytes);
        _liveness = new LivenessMonitor(Options.AwaitReplyTimeout, Options.ProbeTimeout, Options.IdleProbeAfter, _time.GetUtcNow());
    }

    /// <summary>Every decoded server message, after the model applied it. <see cref="InboundPacket.Text"/> is the raw text.</summary>
    public event EventHandler<InboundPacket>? PacketReceived;

    /// <summary>Every JSON/xt server message in skua.swf's <c>pext</c> shape. Only serialised when subscribed.</summary>
    public event EventHandler<ExtensionResponseEventArgs>? ExtensionResponse;

    /// <summary>Every message written to the socket.</summary>
    public event EventHandler<PacketSentEventArgs>? PacketSent;

    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Raised exactly once, when the connection ends for any reason.</summary>
    public event EventHandler<DisconnectInfo>? Disconnected;

    public event EventHandler<HeadlessLogEventArgs>? Log;

    public AqwConnectionOptions Options { get; }

    public WorldModel World { get; }

    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    public bool IsReady => State == ConnectionState.Ready;

    public GameServer? Server { get; private set; }

    public string? ClientVersion { get; private set; }

    /// <summary>Set once the connection has closed.</summary>
    public DisconnectInfo? DisconnectInfo { get; private set; }

    /// <summary>Completes when the connection closes.</summary>
    public Task<DisconnectInfo> Closed => _closed.Task;

    public ConnectionStatistics GetStatistics()
    {
        return new ConnectionStatistics(
            Interlocked.Read(ref _packetsIn),
            Interlocked.Read(ref _packetsOut),
            Interlocked.Read(ref _bytesIn),
            Interlocked.Read(ref _bytesOut),
            Interlocked.Read(ref _compressedIn),
            Interlocked.Read(ref _undecodableIn),
            _pacer.Dropped,
            _pacer.Superseded,
            _pacer.Count,
            Interlocked.Read(ref _slowDowns),
            Interlocked.Read(ref _afkClears),
            Interlocked.Read(ref _probes),
            Interlocked.Read(ref _subscriberErrors),
            State == ConnectionState.Created ? null : _liveness.LastInbound)
        {
            LastRoundTrip = RoundTrip(out TimeSpan? average),
            AverageRoundTrip = average,
            InterceptedChanged = Interlocked.Read(ref _interceptChanged),
            InterceptedBlocked = Interlocked.Read(ref _interceptBlocked),
        };
    }

    private TimeSpan? RoundTrip(out TimeSpan? average)
    {
        lock (_rttGate)
        {
            average = _averageRoundTripMs < 0 ? null : TimeSpan.FromMilliseconds(_averageRoundTripMs);
            return _lastRoundTripTicks < 0 ? null : TimeSpan.FromTicks(_lastRoundTripTicks);
        }
    }

    private void NoteRequestWritten(string? command)
    {
        if (command is null || !s_replyOf.TryGetValue(command, out string? reply))
            return;
        lock (_rttGate)
            _awaitingReply.TryAdd(reply, Stopwatch.GetTimestamp());
    }

    private void NoteReply(string? command)
    {
        if (command is null)
            return;
        lock (_rttGate)
        {
            if (_awaitingReply.Count == 0 || !_awaitingReply.Remove(command, out long sent))
                return;
            TimeSpan rtt = Stopwatch.GetElapsedTime(sent);
            _lastRoundTripTicks = rtt.Ticks;
            // A moving average over about the last eight answers.
            _averageRoundTripMs = _averageRoundTripMs < 0 ? rtt.TotalMilliseconds : _averageRoundTripMs * 0.875 + rtt.TotalMilliseconds * 0.125;
        }
    }

    // ---------------------------------------------------------------- connect / login

    /// <summary>
    /// Opens the socket and logs in. Completes when the character's inventory has loaded.
    /// </summary>
    /// <exception cref="AqwLoginException">The server refused the login, the character did not load in time, or the socket failed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the connection is closed.</exception>
    public Task ConnectAsync(GameServer server, string username, string token, string clientVersion, CancellationToken cancellationToken = default)
    {
        SocketLogin login = new(username, token);
        return ConnectAsync(server, () => login, clientVersion, cancellationToken);
    }

    /// <summary>
    /// Opens the socket and logs in with the name and token <paramref name="login"/> gives when the server accepts
    /// the API version (apiOK). The game reads <c>loginInfo.strToken</c> at that moment too
    /// (<c>Game.onConnectionHandler</c>), so a web login that finishes while the socket opens is the one used.
    /// Completes when the character's inventory has loaded.
    /// </summary>
    public async Task ConnectAsync(GameServer server, Func<SocketLogin> login, string clientVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentException.ThrowIfNullOrEmpty(clientVersion);
        SocketLogin first = login();
        if (Interlocked.CompareExchange(ref _state, (int)ConnectionState.Connecting, (int)ConnectionState.Created) != (int)ConnectionState.Created)
            throw new InvalidOperationException("An AqwConnection can only connect once; create a new one to reconnect.");
        Raise(StateChanged, ConnectionState.Connecting);

        Server = server;
        ClientVersion = clientVersion;
        _login = login;
        UseLogin(first);
        Volatile.Write(ref _tracing, Options.TraceHandshake ? 1 : 0);
        World.Reset();

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(Options.LoginTimeout);
        try
        {
            using (CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                connectTimeout.CancelAfter(Options.ConnectTimeout);
                Func<string, int, CancellationToken, ValueTask<Stream>> connector = Options.Connector ?? ConnectTcpAsync;
                try
                {
                    _stream = await connector(server.Host, server.Port, connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AqwLoginException(LoginFailureKind.ConnectionLost, $"Could not connect to {server.Name} ({server.Host}:{server.Port}) within {Options.ConnectTimeout.TotalSeconds:0} s.");
                }
                catch (Exception e) when (e is SocketException or IOException)
                {
                    throw new AqwLoginException(LoginFailureKind.ConnectionLost, $"Could not connect to {server.Name} ({server.Host}:{server.Port}): {e.Message}", inner: e);
                }
            }

            World.SetConnected(true);
            _liveness = new LivenessMonitor(Options.AwaitReplyTimeout, Options.ProbeTimeout, Options.IdleProbeAfter, _time.GetUtcNow());
            CancellationToken life = _lifetime.Token;
            _readTask = Task.Run(() => ReadLoopAsync(_stream, life), CancellationToken.None);
            _writeTask = Task.Run(() => WriteLoopAsync(_stream, life), CancellationToken.None);
            _livenessTask = Task.Run(() => LivenessLoopAsync(life), CancellationToken.None);

            SetState(ConnectionState.Handshaking);
            _socketOpenedAt = Stopwatch.GetTimestamp();
            RaiseLog(HeadlessLogLevel.Info, $"Connected to {server.Name} ({server.Host}:{server.Port}); client version {clientVersion}, verChk API version {Options.ApiVersion}.");
            Enqueue(Packets.VersionCheck(Options.ApiVersion), SendPriority.Immediate, wantCompletion: false);

            await _ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Close(DisconnectReason.Cancelled, "Login cancelled.");
            throw;
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            Close(DisconnectReason.LoginTimeout, $"The character did not finish loading within {Options.LoginTimeout.TotalSeconds:0} s (state {State}).");
            throw new AqwLoginException(LoginFailureKind.Timeout, DisconnectInfo!.Message);
        }
        catch (OperationCanceledException)
        {
            // The connection closed while logging in; _ready carries the reason.
            await _ready.Task.ConfigureAwait(false);
        }
        catch (AqwLoginException e)
        {
            Close(e.Kind == LoginFailureKind.ConnectionLost ? DisconnectReason.TransportError : DisconnectReason.LoginFailed, e.Message, e.RequiredClientVersion);
            throw;
        }
    }

    private static async ValueTask<Stream> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        TcpClient client = new() { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            return new OwnedNetworkStream(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- sending

    /// <summary>
    /// Queues a packet (paced unless <see cref="SendPriority.Immediate"/>). The task completes with true when it is
    /// written, false when it was dropped, superseded or the connection closed first.
    /// </summary>
    public Task<bool> SendAsync(string packet, SendPriority priority = SendPriority.Normal)
    {
        ArgumentException.ThrowIfNullOrEmpty(packet);
        if (State is ConnectionState.Created or ConnectionState.Closed)
            return Task.FromResult(false);
        BeforeAction(packet);
        return Enqueue(packet, priority, wantCompletion: true);
    }

    /// <summary>Next id for a <c>gar</c> action request.</summary>
    public int NextActionId()
    {
        return Interlocked.Increment(ref _actionId);
    }

    private Task<bool> Enqueue(string packet, SendPriority priority, bool wantCompletion)
    {
        if (packet.Contains('\0'))
            throw new ArgumentException("A packet must not contain NUL (the message terminator).", nameof(packet));
        OutboundItem item = new(packet, priority, wantCompletion);
        IReadOnlyList<OutboundItem> lost = _pacer.Enqueue(item);
        if (lost.Count > 0)
        {
            RaiseLog(HeadlessLogLevel.Warning, $"Send queue over {Options.MaxQueuedPackets}: dropped {lost.Count} packet(s) ({string.Join(", ", lost.Select(x => x.Command ?? "?").Distinct())}), {_pacer.Dropped} since connect.");
        }
        _sendSignal.Release();
        return item.Completion;
    }

    // World.afkPostpone: the game clears the AFK flag before its own actions.
    private void BeforeAction(string packet)
    {
        if (!Options.ClearAfk || State != ConnectionState.Ready)
            return;
        string? cmd = Packets.CommandOf(packet);
        bool transfer = cmd == "cmd" && packet.Contains("%tfer%", StringComparison.Ordinal);
        if (cmd is null || !(s_afkActions.Contains(cmd) || transfer))
            return;
        if (World.IsMeAfk())
            ClearAfk("before " + cmd);
    }

    private void ClearAfk(string why)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (now - _lastAfkClear < Options.AfkClearThrottle)
            return;
        _lastAfkClear = now;
        Interlocked.Increment(ref _afkClears);
        RaiseLog(HeadlessLogLevel.Debug, $"Clearing AFK ({why}).");
        Enqueue(Packets.Afk(false), SendPriority.High, wantCompletion: false);
    }

    private async Task WriteLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_pacer.TryDequeue(_time.GetUtcNow(), out OutboundItem? item, out TimeSpan wait))
                {
                    await WriteAsync(stream, item!, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (wait > TimeSpan.Zero)
                {
                    // A newer packet may jump the queue (Immediate), so wake on a signal too.
                    await _sendSignal.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _sendSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Close(DisconnectReason.TransportError, $"Write failed: {e.Message}");
        }
        finally
        {
            _pacer.Clear();
        }
    }

    private async Task WriteAsync(Stream stream, OutboundItem item, CancellationToken cancellationToken)
    {
        string text = item.Text;
        string? command = item.Command;
        if (item.Paced && Volatile.Read(ref _interceptArmed) && Options.Interceptor is { IsActive: true } interceptor)
        {
            string? changed = InterceptOutbound(interceptor, text, command);
            if (changed is null)
            {
                // Dropped by the interceptor: never written, so the pacer's clock does not move.
                item.Complete(false);
                return;
            }
            if (!ReferenceEquals(changed, text))
            {
                text = changed;
                command = Packets.CommandOf(text);
            }
        }

        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        string shown = Redact(text);
        // Traced before the bytes leave: once they do, the reader may handle the reply (the first moveToArea ends the
        // trace) before this thread goes on, and the frame that asked for it would be missing from the trace.
        if (Volatile.Read(ref _tracing) == 1)
            TraceFrame(">>", shown, verbatim: command is null);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        if (item.Paced)
            _pacer.MarkWritten(now);
        Interlocked.Increment(ref _packetsOut);
        Interlocked.Add(ref _bytesOut, bytes.Length);
        bool transfer = command == "cmd" && text.Contains("%tfer%", StringComparison.Ordinal);
        if (command is not null && (s_expectReply.Contains(command) || transfer))
            _liveness.OnRequestExpectingReply(now);
        NoteRequestWritten(command);
        if (transfer)
        {
            // [tfer, user, map, cell, pad]: remembered so the cell move after moveToArea goes where we asked.
            string[] args = Packets.ArgsOf(text);
            if (args.Length >= 3)
                _requestedSpot = (args[2].Split('-')[0], args.Length > 3 && args[3].Length > 0 ? args[3] : "Enter", args.Length > 4 && args[4].Length > 0 ? args[4] : "Spawn");
        }
        item.Complete(true);

        World.ApplyOutbound(text);
        bool redacted = !string.Equals(shown, text, StringComparison.Ordinal);
        Raise(PacketSent, new PacketSentEventArgs(shown, redacted, now));
    }

    // ---------------------------------------------------------------- interception (see IPacketInterceptor)

    /// <summary>The text to write (the same instance when unchanged), or null to drop the packet.</summary>
    private string? InterceptOutbound(IPacketInterceptor interceptor, string text, string? command)
    {
        if (command is null || InterceptionRules.ProtectedOutbound.Contains(command) || !string.Equals(Redact(text), text, StringComparison.Ordinal))
            return text;
        InterceptedFrame frame = new(outbound: true, text, command);
        if (!RunInterceptor(interceptor, frame))
            return text;
        if (frame.Blocked)
        {
            Interlocked.Increment(ref _interceptBlocked);
            return null;
        }
        string changed = frame.Text ?? string.Empty;
        if (string.Equals(changed, text, StringComparison.Ordinal))
            return text;
        if (!changed.StartsWith("%xt%", StringComparison.Ordinal) || changed.Contains('\0') || Packets.CommandOf(changed) is not string newCommand
            || InterceptionRules.ProtectedOutbound.Contains(newCommand))
        {
            RaiseLog(HeadlessLogLevel.Warning, $"Packet interceptor: the changed {command} is not one game packet it may send; the original was sent.");
            return text;
        }
        Interlocked.Increment(ref _interceptChanged);
        return changed;
    }

    /// <summary>The packet to apply (the same instance when unchanged), or null to drop it.</summary>
    private InboundPacket? InterceptInbound(IPacketInterceptor interceptor, InboundPacket packet)
    {
        if (packet.Kind is not (PacketKind.Xt or PacketKind.Json) || packet.Command is not string command
            || InterceptionRules.ProtectedInbound.Contains(command) || !string.Equals(Redact(packet.Text), packet.Text, StringComparison.Ordinal))
            return packet;
        InterceptedFrame frame = new(outbound: false, packet.Text, command);
        if (!RunInterceptor(interceptor, frame))
            return packet;
        if (frame.Blocked)
        {
            Interlocked.Increment(ref _interceptBlocked);
            return null;
        }
        string changed = frame.Text ?? string.Empty;
        if (string.Equals(changed, packet.Text, StringComparison.Ordinal))
            return packet;
        InboundPacket parsed = PacketCodec.Parse(changed, packet.WasCompressed);
        if (parsed.Kind is not (PacketKind.Xt or PacketKind.Json) || parsed.Command is null || InterceptionRules.ProtectedInbound.Contains(parsed.Command))
        {
            RaiseLog(HeadlessLogLevel.Warning, $"Packet interceptor: the changed {command} does not parse as a game message it may change; the original was applied.");
            return packet;
        }
        Interlocked.Increment(ref _interceptChanged);
        return parsed;
    }

    private bool RunInterceptor(IPacketInterceptor interceptor, InterceptedFrame frame)
    {
        try
        {
            interceptor.Intercept(frame);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _subscriberErrors);
            RaiseLog(HeadlessLogLevel.Error, $"Packet interceptor failed on {frame.Command}: {e.GetType().Name}: {e.Message}; the original was used.");
            return false;
        }
    }

    // ---------------------------------------------------------------- receiving

    private async Task ReadLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    Close(_pendingReason ?? DisconnectReason.RemoteClosed, _pendingMessage ?? "The server closed the connection.");
                    return;
                }
                Interlocked.Add(ref _bytesIn, read);
                foreach (byte[] frame in _framer.Push(buffer.AsSpan(0, read)))
                    HandleFrame(frame);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (FrameTooLargeException e)
        {
            Close(DisconnectReason.ProtocolError, e.Message);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Close(_pendingReason ?? DisconnectReason.TransportError, _pendingMessage ?? $"Read failed: {e.Message}");
        }
        catch (Exception e)
        {
            // Anything else (a packet shape no layer expected) must not leave a dead reader behind an open socket.
            RaiseLog(HeadlessLogLevel.Error, $"Reader stopped by {e.GetType().Name}: {e.Message}");
            Close(DisconnectReason.ProtocolError, $"Reader failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Hands a packet made by the client itself (not received) to everything a received packet goes to: the world
    /// model, <see cref="PacketReceived"/>, <see cref="ExtensionResponse"/> and the reply waiters. It does not count
    /// as server traffic (liveness, packet counters). Used to tell Skua's layers that the bank arrived over HTTP,
    /// as the socket's <c>loadBank</c> reply would.
    /// </summary>
    public void DispatchLocal(string text)
    {
        InboundPacket packet = PacketCodec.Parse(text);
        World.Apply(packet);
        Raise(PacketReceived, packet);
        EventHandler<ExtensionResponseEventArgs>? ext = ExtensionResponse;
        if (ext is not null && packet.ToExtensionResponseJson() is string json)
            Raise(ext, new ExtensionResponseEventArgs(packet, json));
        CompleteWaiters(packet);
    }

    private void HandleFrame(byte[] frame)
    {
        _liveness.OnInbound(_time.GetUtcNow());
        Interlocked.Increment(ref _packetsIn);
        InboundPacket packet = PacketCodec.Decode(frame);
        NoteReply(packet.Command);
        if (packet.WasCompressed)
            Interlocked.Increment(ref _compressedIn);
        if (packet.Kind == PacketKind.Unknown)
        {
            Interlocked.Increment(ref _undecodableIn);
            RaiseLog(HeadlessLogLevel.Warning, $"Undecodable message ({packet.DecodeError}): {Shorten(packet.Text)}");
        }
        if (Volatile.Read(ref _tracing) == 1)
        {
            TraceFrame("<<", packet.Text, verbatim: packet.Kind is PacketKind.Xml || packet.Command == "loginResponse");
            if (packet.Command == "moveToArea")
            {
                Volatile.Write(ref _tracing, 0);
                RaiseLog(HeadlessLogLevel.Info, $"[handshake] First moveToArea after {ElapsedSinceSocketOpen()}; trace ends.");
            }
        }

        if (Volatile.Read(ref _interceptArmed) && Options.Interceptor is { IsActive: true } interceptor)
        {
            InboundPacket? kept = InterceptInbound(interceptor, packet);
            if (kept is null)
                return; // dropped: not applied, not reported, no waiter completed
            packet = kept;
        }

        World.Apply(packet);
        try
        {
            HandleProtocol(packet);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        {
            RaiseLog(HeadlessLogLevel.Error, $"Protocol handling of {packet.Command} failed: {e.Message}");
        }

        Raise(PacketReceived, packet);
        EventHandler<ExtensionResponseEventArgs>? ext = ExtensionResponse;
        if (ext is not null && packet.ToExtensionResponseJson() is string json)
            Raise(ext, new ExtensionResponseEventArgs(packet, json));
        CompleteWaiters(packet);
        // The login ends when the character has loaded (after the first moveToArea, its character data and its
        // inventory): only from the next message on may an interceptor see anything.
        if (!Volatile.Read(ref _interceptArmed) && State == ConnectionState.Ready)
            Volatile.Write(ref _interceptArmed, true);
    }

    private void HandleProtocol(InboundPacket p)
    {
        switch (p.Kind)
        {
            case PacketKind.Xml:
                HandleXml(p);
                break;

            case PacketKind.Xt:
                HandleXt(p);
                break;

            case PacketKind.Json:
                HandleJson(p);
                break;
        }
    }

    private void HandleXml(InboundPacket p)
    {
        switch (p.Command)
        {
            case "apiOK" when State == ConnectionState.Handshaking:
                SetState(ConnectionState.LoggingIn);
                // Game.onConnectionHandler reads loginInfo now, so a web login that finished meanwhile wins.
                string before = _token;
                try
                {
                    UseLogin(_login());
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    RaiseLog(HeadlessLogLevel.Warning, $"Could not read the current login ({e.Message}); using the one given at connect.");
                }
                if (!string.Equals(before, _token, StringComparison.Ordinal))
                    RaiseLog(HeadlessLogLevel.Info, "A newer web login finished while the socket opened: its token is used, as the game reads it only now.");
                RaiseLog(HeadlessLogLevel.Info, $"Socket login {ElapsedSinceSocketOpen()} after the socket opened: nick {Packets.ClientToken}~{Packets.LoginName(_username)}~{Packets.As3Number(ClientVersion!)}, "
                    + $"password = token ({_loginOrigin ?? "origin not given"}), zone {Options.Zone}.");
                Enqueue(Packets.Login(_username, ClientVersion!, _token, Options.Zone), SendPriority.Immediate, wantCompletion: false);
                break;

            case "apiKO":
                FailLogin(new AqwLoginException(LoginFailureKind.ApiVersionRejected, $"The server rejected SmartFox API version {Options.ApiVersion} (apiKO)."));
                break;

            case "logKO":
                FailLogin(new AqwLoginException(LoginFailureKind.SocketRejected, $"The server refused the socket login (logKO): {Shorten(p.Text)}"));
                break;

            case "logout":
                Close(_pendingReason ?? DisconnectReason.RemoteClosed, _pendingMessage ?? "The server logged this session out.");
                break;
        }
    }

    private void HandleXt(InboundPacket p)
    {
        IReadOnlyList<string> a = p.Args;
        switch (p.Command)
        {
            case "loginResponse" when State == ConnectionState.LoggingIn:
                if (a.Count >= 2 && a[0] == "true" && int.TryParse(a[1], out int uid))
                {
                    RaiseLog(HeadlessLogLevel.Info, $"Logged in as {(a.Count > 2 ? a[2] : _username)} (uid {uid}); loading the character.");
                    SetState(ConnectionState.Loading);
                    Enqueue(Packets.FirstJoin(), SendPriority.High, wantCompletion: false);
                }
                else
                {
                    string message = a.LastOrDefault(x => x.Length > 0) ?? "login refused";
                    string? required = Net.ClientVersion.RequiredFromMessage(message);
                    FailLogin(required is not null
                        ? new AqwLoginException(LoginFailureKind.ClientVersionRejected, $"The server requires client version {required} (sent {ClientVersion}): {message}", required)
                        : new AqwLoginException(LoginFailureKind.SocketRejected, $"Login refused: {message}"));
                }
                break;

            case "server":
            case "warning":
                string text = string.Join(' ', a);
                if (SlowDownRegex().IsMatch(text))
                {
                    Interlocked.Increment(ref _slowDowns);
                    _pacer.SlowDown(_time.GetUtcNow());
                    RaiseLog(HeadlessLogLevel.Warning, $"Server asked to slow down: widening the send gap to {Options.SlowSendGap.TotalMilliseconds:0} ms for {Options.SlowDownHold.TotalSeconds:0} s.");
                }
                if (text.Contains("invalid session", StringComparison.OrdinalIgnoreCase))
                    SetPending(DisconnectReason.InvalidSession, text);
                break;

            case "multiLoginWarning":
                SetPending(DisconnectReason.LoggedInElsewhere, "This account was logged in from somewhere else.");
                break;

            case "logoutWarning":
                SetPending(DisconnectReason.Kicked, "The server announced a logout: " + string.Join(' ', a));
                break;

            case "uotls" when a.Count >= 2:
                if (Options.ClearAfk && State == ConnectionState.Ready && string.Equals(a[0], World.MyUsername, StringComparison.OrdinalIgnoreCase)
                    && a[1].Contains("afk:true", StringComparison.OrdinalIgnoreCase))
                    ClearAfk("server set AFK");
                break;
        }
    }

    private void HandleJson(InboundPacket p)
    {
        switch (p.Command)
        {
            case "moveToArea":
                OnAreaEntered();
                break;

            case "initUserData" when State == ConnectionState.Loading && !_inventoryRequested:
                int uid = World.MyUserId;
                int? target = JsonUtil.AsIntOrNull(p.Data?["uid"]);
                if (target is null || target == uid)
                {
                    _inventoryRequested = true;
                    Enqueue(Packets.RetrieveInventory(World.RoomId, uid), SendPriority.High, wantCompletion: false);
                }
                break;

            case "loadInventoryBig" when State == ConnectionState.Loading:
                SetState(ConnectionState.Ready);
                RaiseLog(HeadlessLogLevel.Info, "Character loaded.");
                _ready.TrySetResult();
                break;

            case "uotls":
                if (Options.ClearAfk && State == ConnectionState.Ready
                    && string.Equals(JsonUtil.AsString(p.Data?["unm"]), World.MyUsername, StringComparison.OrdinalIgnoreCase)
                    && JsonUtil.AsBool((p.Data?["o"] as System.Text.Json.Nodes.JsonObject)?["afk"]))
                    ClearAfk("server set AFK");
                break;

            case "multiLoginWarning":
                SetPending(DisconnectReason.LoggedInElsewhere, "This account was logged in from somewhere else.");
                break;

            case "logoutWarning":
                SetPending(DisconnectReason.Kicked, "The server announced a logout.");
                break;
        }
    }

    // The map is only entered for the server once the client picks a cell; the first join also asks for this
    // character's data (the server sends none until asked).
    private void OnAreaEntered()
    {
        int room = World.RoomId;
        if (Options.EnterCellAfterJoin)
        {
            AreaInfo area = World.GetArea();
            string cell = area.Cell;
            string pad = area.Pad;
            if (_requestedSpot is { } spot && spot.Map.Equals(area.MapName, StringComparison.OrdinalIgnoreCase))
            {
                cell = spot.Cell;
                pad = spot.Pad;
            }
            _requestedSpot = null;
            Enqueue(Packets.MoveToCell(room, cell, pad), SendPriority.High, wantCompletion: false);
        }
        if (!_firstAreaEntered && State == ConnectionState.Loading)
        {
            _firstAreaEntered = true;
            Enqueue(Packets.RetrieveUserData(room, World.MyUserId), SendPriority.High, wantCompletion: false);
        }
    }

    private void SetPending(DisconnectReason reason, string message)
    {
        _pendingReason = reason;
        _pendingMessage = message;
        RaiseLog(HeadlessLogLevel.Warning, message);
    }

    private void FailLogin(AqwLoginException e)
    {
        RaiseLog(HeadlessLogLevel.Error, e.Message);
        Close(DisconnectReason.LoginFailed, e.Message, e.RequiredClientVersion, e);
    }

    // ---------------------------------------------------------------- waiting for replies

    /// <summary>
    /// Waits for the next packet matching <paramref name="predicate"/>. Register before sending the request that
    /// triggers it (see <see cref="SendAndWaitAsync"/>). Returns null on timeout.
    /// </summary>
    /// <exception cref="ConnectionClosedException">The connection closed while waiting.</exception>
    public Task<InboundPacket?> WaitForAsync(Func<InboundPacket, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Waiter waiter = AddWaiter(predicate);
        return AwaitWaiterAsync(waiter, timeout, cancellationToken);
    }

    /// <summary>Sends a packet and waits for the reply matching <paramref name="reply"/> (registered before sending).</summary>
    public async Task<InboundPacket?> SendAndWaitAsync(string packet, Func<InboundPacket, bool> reply, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Waiter waiter = AddWaiter(reply);
        bool written = await SendAsync(packet).ConfigureAwait(false);
        if (!written)
        {
            RemoveWaiter(waiter);
            if (DisconnectInfo is DisconnectInfo info)
                throw new ConnectionClosedException(info);
            return null;
        }
        return await AwaitWaiterAsync(waiter, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the server for this character's data (always answered) and reports whether anything arrived in time.
    /// </summary>
    public async Task<bool> ProbeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (State is ConnectionState.Created or ConnectionState.Closed)
            return false;
        Waiter waiter = AddWaiter(_ => true);
        Interlocked.Increment(ref _probes);
        _ = Enqueue(Packets.RetrieveUserData(World.RoomId, World.MyUserId), SendPriority.High, wantCompletion: false);
        try
        {
            return await AwaitWaiterAsync(waiter, timeout ?? Options.ProbeTimeout, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (ConnectionClosedException)
        {
            return false;
        }
    }

    private Waiter AddWaiter(Func<InboundPacket, bool> predicate)
    {
        Waiter waiter = new(predicate);
        lock (_waitGate)
        {
            if (DisconnectInfo is DisconnectInfo info)
            {
                waiter.Completion.TrySetException(new ConnectionClosedException(info));
                return waiter;
            }
            _waiters.Add(waiter);
        }
        return waiter;
    }

    private void RemoveWaiter(Waiter waiter)
    {
        lock (_waitGate) _waiters.Remove(waiter);
    }

    private async Task<InboundPacket?> AwaitWaiterAsync(Waiter waiter, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await waiter.Completion.Task.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            RemoveWaiter(waiter);
        }
    }

    private void CompleteWaiters(InboundPacket packet)
    {
        Waiter[] current;
        lock (_waitGate)
        {
            if (_waiters.Count == 0)
                return;
            current = _waiters.ToArray();
        }
        foreach (Waiter waiter in current)
        {
            bool match;
            try
            {
                match = waiter.Predicate(packet);
            }
            catch (Exception e)
            {
                waiter.Completion.TrySetException(e);
                RemoveWaiter(waiter);
                continue;
            }
            if (match && waiter.Completion.TrySetResult(packet))
                RemoveWaiter(waiter);
        }
    }

    private sealed class Waiter
    {
        public Waiter(Func<InboundPacket, bool> predicate)
        {
            Predicate = predicate;
        }

        public Func<InboundPacket, bool> Predicate { get; }

        public TaskCompletionSource<InboundPacket?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ---------------------------------------------------------------- liveness

    private async Task LivenessLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(Options.LivenessCheckInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (State != ConnectionState.Ready)
                    continue;
                DateTimeOffset now = _time.GetUtcNow();
                switch (_liveness.Check(now))
                {
                    case LivenessVerdict.Probe:
                        Interlocked.Increment(ref _probes);
                        _liveness.OnProbeSent(now);
                        RaiseLog(HeadlessLogLevel.Info, $"No reply for {(now - _liveness.LastInbound).TotalSeconds:0} s; probing the connection.");
                        Enqueue(Packets.RetrieveUserData(World.RoomId, World.MyUserId), SendPriority.High, wantCompletion: false);
                        break;

                    case LivenessVerdict.Dead:
                        Close(DisconnectReason.LivenessTimeout, $"Connection dead: nothing from the server for {(now - _liveness.LastInbound).TotalSeconds:0} s, probe unanswered.");
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    // ---------------------------------------------------------------- closing

    /// <summary>Logs out (when logged in) and closes the socket. Raises <see cref="Disconnected"/> with <see cref="DisconnectReason.Requested"/>.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is ConnectionState.Closed)
        {
            await WhenClosedAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (State is ConnectionState.Loading or ConnectionState.Ready)
        {
            // The server may close the socket on logout before this method does: either way it is our request.
            _pendingReason = DisconnectReason.Requested;
            _pendingMessage = "Disconnected by the client.";
            Task<bool> logout = Enqueue(Packets.Logout(), SendPriority.High, wantCompletion: true);
            try
            {
                await logout.WaitAsync(TimeSpan.FromSeconds(2), _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        Close(DisconnectReason.Requested, "Disconnected by the client.");
        await WhenClosedAsync(cancellationToken).ConfigureAwait(false);
    }

    // A Close on another thread (the reader seeing the server close the socket after logout) may be the one that
    // runs: return only once it has finished (DisconnectInfo set, Disconnected raised).
    private Task WhenClosedAsync(CancellationToken cancellationToken)
    {
        return Volatile.Read(ref _closing) == 1 ? _closed.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    /// <summary>
    /// Closes once; a second call (any thread) returns at once. The login (<see cref="_ready"/>) fails only at the
    /// end, after <see cref="DisconnectInfo"/>, the state and <see cref="Disconnected"/>: ConnectAsync resumes on
    /// another thread as soon as it fails, and its caller must find the connection fully closed.
    /// </summary>
    private void Close(DisconnectReason reason, string message, string? requiredClientVersion = null, AqwLoginException? loginFailure = null)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1)
            return;
        ConnectionState before = State;
        DisconnectInfo info = new(reason, message, before, _time.GetUtcNow(), requiredClientVersion);
        DisconnectInfo = info;
        Volatile.Write(ref _state, (int)ConnectionState.Closed);

        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        try { _stream?.Dispose(); } catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        _pacer.Clear();
        World.SetConnected(false);

        Waiter[] waiters;
        lock (_waitGate)
        {
            waiters = _waiters.ToArray();
            _waiters.Clear();
        }
        ConnectionClosedException closed = new(info);
        foreach (Waiter waiter in waiters)
            waiter.Completion.TrySetException(closed);

        RaiseLog(reason is DisconnectReason.Requested ? HeadlessLogLevel.Info : HeadlessLogLevel.Warning, $"Disconnected ({reason}): {message}");
        Raise(StateChanged, ConnectionState.Closed);
        Raise(Disconnected, info);

        _ready.TrySetException(reason == DisconnectReason.Cancelled
            ? new OperationCanceledException(message)
            : loginFailure ?? new AqwLoginException(reason == DisconnectReason.LoginFailed && requiredClientVersion is not null ? LoginFailureKind.ClientVersionRejected : reason == DisconnectReason.LoginTimeout ? LoginFailureKind.Timeout : LoginFailureKind.ConnectionLost, message, requiredClientVersion));
        _ = _ready.Task.Exception; // observed: a caller that never awaited ConnectAsync must not see an unobserved exception
        _closed.TrySetResult(info);
    }

    public async ValueTask DisposeAsync()
    {
        if (State != ConnectionState.Closed && State != ConnectionState.Created)
            Close(DisconnectReason.Requested, "Disposed.");
        else if (State == ConnectionState.Created)
            Volatile.Write(ref _state, (int)ConnectionState.Closed);
        foreach (Task? task in new[] { _readTask, _writeTask, _livenessTask })
        {
            if (task is null)
                continue;
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
            }
        }
        _lifetime.Dispose();
        _sendSignal.Dispose();
    }

    // ---------------------------------------------------------------- helpers

    private void SetState(ConnectionState state)
    {
        ConnectionState old = (ConnectionState)Interlocked.Exchange(ref _state, (int)state);
        if (old == ConnectionState.Closed)
        {
            Volatile.Write(ref _state, (int)ConnectionState.Closed);
            return;
        }
        if (old != state)
            Raise(StateChanged, state);
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
            return;
        foreach (EventHandler<T> single in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                single(this, args);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _subscriberErrors);
                if (!ReferenceEquals(handler, Log))
                    RaiseLog(HeadlessLogLevel.Error, $"An event subscriber threw: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private void RaiseLog(HeadlessLogLevel level, string message)
    {
        EventHandler<HeadlessLogEventArgs>? log = Log;
        if (log is null)
            return;
        HeadlessLogEventArgs args = new(level, Redact(message));
        foreach (EventHandler<HeadlessLogEventArgs> single in log.GetInvocationList().Cast<EventHandler<HeadlessLogEventArgs>>())
        {
            try
            {
                single(this, args);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _subscriberErrors);
            }
        }
    }

    private static string Shorten(string text)
    {
        return text.Length > 300 ? text[..300] + "..." : text;
    }

    /// <summary>
    /// Replaces <paramref name="secret"/> by <paramref name="placeholder"/> in everything this connection logs or
    /// reports (sent packets, the handshake trace). The login token is always replaced by <c>&lt;token&gt;</c>.
    /// </summary>
    public void AddRedaction(string? secret, string placeholder)
    {
        ArgumentException.ThrowIfNullOrEmpty(placeholder);
        // A very short value would blank out ordinary text everywhere (it is never sent on the socket anyway).
        if (string.IsNullOrEmpty(secret) || secret.Length < 4)
            return;
        lock (_redactGate)
        {
            if (!_redactions.Exists(r => r.Secret == secret))
                _redactions.Add((secret, placeholder));
            // Longest first, so a secret containing another is replaced whole.
            _redactions.Sort((a, b) => b.Secret.Length.CompareTo(a.Secret.Length));
        }
    }

    private void UseLogin(SocketLogin login)
    {
        AddRedaction(login.Token, "<token>");
        _username = login.Username;
        _token = login.Token;
        _loginOrigin = login.Origin;
    }

    private string Redact(string text)
    {
        lock (_redactGate)
        {
            foreach ((string secret, string placeholder) in _redactions)
            {
                if (text.Contains(secret, StringComparison.Ordinal))
                    text = text.Replace(secret, placeholder, StringComparison.Ordinal);
            }
        }
        return text;
    }

    // One handshake frame at Info level: secrets and e-mail addresses replaced; long game data shortened, the
    // system messages and the loginResponse kept whole.
    private void TraceFrame(string direction, string text, bool verbatim)
    {
        string clean = EmailRegex().Replace(Redact(text), "<email>");
        if (!verbatim && clean.Length > 600)
            clean = string.Concat(clean.AsSpan(0, 600), $"... ({clean.Length} chars)");
        RaiseLog(HeadlessLogLevel.Info, $"[handshake] +{ElapsedSinceSocketOpen()} {direction} {clean}");
    }

    private string ElapsedSinceSocketOpen()
    {
        return _socketOpenedAt == 0 ? "0 ms" : $"{Stopwatch.GetElapsedTime(_socketOpenedAt).TotalMilliseconds:0} ms";
    }

    [GeneratedRegex("slow down|too soon|spamming", RegexOptions.IgnoreCase)]
    private static partial Regex SlowDownRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    /// <summary>A NetworkStream that also disposes its TcpClient.</summary>
    private sealed class OwnedNetworkStream : Stream
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _inner;

        public OwnedNetworkStream(TcpClient client)
        {
            _client = client;
            _inner = client.GetStream();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
