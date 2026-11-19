using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RuffleSpike.Core;

public sealed class SocketBridgeOptions
{
    /// <summary>Origins whose pages may use the bridge (the WebSocket handshake's Origin header must be one of them).</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = ["https://" + RequestPolicy.GameHost];
    public int MaxConnections { get; init; } = 4;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxFramePayload { get; init; } = 1 << 20;
    /// <summary>Opens the TCP connection to a game server (tests replace it; the default resolves and connects).</summary>
    public Func<GameEndpoint, CancellationToken, Task<Stream>>? Connector { get; init; }
}

/// <summary>What the bridge reports (never payload bytes, never the secret).</summary>
public readonly record struct BridgeEvent(string Kind, string Detail);

/// <summary>Result of checking a WebSocket upgrade request.</summary>
public readonly record struct BridgeRequestCheck(bool Ok, GameEndpoint Target, string? Reason, string? AcceptKey);

/// <summary>
/// A WebSocket-to-TCP bridge for Ruffle's <c>socketProxy</c>, listening on 127.0.0.1 only (random port).
/// Each WebSocket connection maps to one TCP connection to the game server named in its path,
/// <c>ws://127.0.0.1:{port}/{secret}/{host}/{port}</c>, and is accepted only when the secret matches (constant
/// time), the Origin is the game's page origin, and host:port is in the live server list (<see cref="ServerAllowlist"/>).
/// Binary frames carry the raw byte stream both ways. Nothing logs payloads; only counts and states are reported.
/// </summary>
public sealed class SocketBridge : IAsyncDisposable
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private readonly BridgeSecret _secret;
    private readonly ServerAllowlist _allowlist;
    private readonly SocketBridgeOptions _options;
    private readonly Action<BridgeEvent>? _onEvent;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _nextId;
    private int _active;

    public SocketBridge(BridgeSecret secret, ServerAllowlist allowlist, SocketBridgeOptions? options = null, Action<BridgeEvent>? onEvent = null)
    {
        _secret = secret;
        _allowlist = allowlist;
        _options = options ?? new SocketBridgeOptions();
        _onEvent = onEvent;
    }

    public int Port { get; private set; }
    public int ActiveConnections => Volatile.Read(ref _active);
    public long TotalConnections;
    public long BytesToServer;
    public long BytesFromServer;
    public ConcurrentDictionary<string, int> Rejections { get; } = new();

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        Report("listening", $"127.0.0.1:{Port}");
    }

    /// <summary>The proxy URL the page gives Ruffle for one endpoint (contains the secret: never log it).</summary>
    public string ProxyUrl(GameEndpoint e) => $"ws://127.0.0.1:{Port}/{_secret.Value}/{e.Host}/{e.Port}";

    private void Report(string kind, string detail)
    {
        try { _onEvent?.Invoke(new BridgeEvent(kind, detail)); } catch { /* reporting must not break the bridge */ }
    }

    private void Reject(string reason)
    {
        Rejections.AddOrUpdate(reason, 1, (_, n) => n + 1);
        Report("rejected", reason);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (_cts.IsCancellationRequested) break; continue; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        int id = Interlocked.Increment(ref _nextId);
        _clients[id] = client;
        Stream? upstream = null;
        bool counted = false;
        try
        {
            client.NoDelay = true;
            NetworkStream ws = client.GetStream();
            string? head;
            using (CancellationTokenSource hs = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                hs.CancelAfter(_options.HandshakeTimeout);
                head = await ReadHeadAsync(ws, hs.Token).ConfigureAwait(false);
            }
            if (head is null) { Reject("bad or slow handshake"); return; }

            BridgeRequestCheck check = CheckRequest(head, _secret, _allowlist, _options.AllowedOrigins);
            if (!check.Ok) { Reject(check.Reason!); await WriteStatusAsync(ws, "403 Forbidden").ConfigureAwait(false); return; }

            if (Interlocked.Increment(ref _active) > _options.MaxConnections)
            {
                Interlocked.Decrement(ref _active);
                Reject("too many connections");
                await WriteStatusAsync(ws, "503 Service Unavailable").ConfigureAwait(false);
                return;
            }
            counted = true;
            Interlocked.Increment(ref TotalConnections);

            try
            {
                using CancellationTokenSource ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                ct.CancelAfter(_options.ConnectTimeout);
                upstream = await (_options.Connector ?? DefaultConnectAsync)(check.Target, ct.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report("connect_failed", $"{check.Target}: {ex.GetType().Name}: {Short(ex.Message)}");
                await WriteStatusAsync(ws, "502 Bad Gateway").ConfigureAwait(false);
                return;
            }

            string response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                + $"Sec-WebSocket-Accept: {check.AcceptKey}\r\n\r\n";
            await ws.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token).ConfigureAwait(false);
            Report("connected", check.Target.ToString());

            long up0 = Interlocked.Read(ref BytesToServer), down0 = Interlocked.Read(ref BytesFromServer);
            string reason = await PumpAsync(ws, upstream).ConfigureAwait(false);
            Report("closed", $"{check.Target} ({reason}; {Interlocked.Read(ref BytesToServer) - up0} B up, {Interlocked.Read(ref BytesFromServer) - down0} B down)");
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Connection dropped during the handshake.
        }
        finally
        {
            if (counted) Interlocked.Decrement(ref _active);
            if (upstream is not null) await upstream.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            _clients.TryRemove(id, out _);
        }
    }

    private static string Short(string s) => s.Length > 120 ? s[..120] : s;

    internal static async Task<Stream> DefaultConnectAsync(GameEndpoint e, CancellationToken ct)
    {
        TcpClient tcp = new() { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(e.Host, e.Port, ct).ConfigureAwait(false);
            return new OwnedStream(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>A NetworkStream that also disposes its TcpClient.</summary>
    private sealed class OwnedStream(TcpClient tcp) : Stream
    {
        private readonly NetworkStream _s = tcp.GetStream();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _s.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _s.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _s.ReadAsync(buffer, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _s.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _s.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { if (disposing) { _s.Dispose(); tcp.Dispose(); } base.Dispose(disposing); }
    }

    private static async Task WriteStatusAsync(Stream s, string status)
    {
        try { await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).ConfigureAwait(false); }
        catch { /* client went away */ }
    }

    /// <summary>Reads the HTTP request head (up to the blank line), at most 8 KiB; null if it is longer or ends early.</summary>
    private static async Task<string?> ReadHeadAsync(Stream s, CancellationToken ct)
    {
        byte[] buf = new byte[8192];
        int len = 0;
        while (len < buf.Length)
        {
            // One byte at a time: the head is small and nothing after it may be consumed.
            int n = await s.ReadAsync(buf.AsMemory(len, 1), ct).ConfigureAwait(false);
            if (n == 0) return null;
            len++;
            if (len >= 4 && buf[len - 4] == '\r' && buf[len - 3] == '\n' && buf[len - 2] == '\r' && buf[len - 1] == '\n')
                return Encoding.Latin1.GetString(buf, 0, len);
        }
        return null;
    }

    /// <summary>
    /// Validates a WebSocket upgrade request head: GET, upgrade headers, version 13, a 16-byte key, an allowed
    /// Origin, and a path of exactly /{secret}/{host}/{port} with the right secret and an allowlisted endpoint.
    /// </summary>
    public static BridgeRequestCheck CheckRequest(string head, BridgeSecret secret, ServerAllowlist allowlist, IReadOnlyList<string> allowedOrigins)
    {
        static BridgeRequestCheck Fail(string reason) => new(false, default, reason, null);

        string[] lines = head.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || requestLine[0] != "GET" || !requestLine[2].StartsWith("HTTP/1.1", StringComparison.Ordinal))
            return Fail("not a GET request");

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) return Fail("malformed header");
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out string? prev) ? prev + ", " + value : value;
        }

        if (!headers.TryGetValue("Upgrade", out string? upgrade) || !upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
            return Fail("not a websocket upgrade");
        if (!headers.TryGetValue("Connection", out string? connection) || !connection.Contains("upgrade", StringComparison.OrdinalIgnoreCase))
            return Fail("not a websocket upgrade");
        if (!headers.TryGetValue("Sec-WebSocket-Version", out string? version) || version != "13")
            return Fail("unsupported websocket version");
        if (!headers.TryGetValue("Sec-WebSocket-Key", out string? key) || !IsValidKey(key))
            return Fail("bad websocket key");
        if (!headers.TryGetValue("Origin", out string? origin) || !allowedOrigins.Contains(origin, StringComparer.Ordinal))
            return Fail("origin not allowed");

        string target = requestLine[1];
        if (target.Contains('?') || target.Contains('#') || target.Contains('%'))
            return Fail("bad path");
        string[] parts = target.Split('/');
        // "/{secret}/{host}/{port}" splits into "", secret, host, port.
        if (parts.Length != 4 || parts[0].Length != 0)
            return Fail("bad path");
        if (!secret.Matches(parts[1]))
            return Fail("wrong secret");
        if (!int.TryParse(parts[3], System.Globalization.NumberStyles.None, null, out int port) || parts[3].Length > 5)
            return Fail("bad port");
        if (!allowlist.IsAllowed(parts[2], port))
            return Fail("endpoint not in server list");
        ServerAllowlist.TryNormalize(parts[2], port, out GameEndpoint endpoint);

        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WsGuid)));
        return new BridgeRequestCheck(true, endpoint, null, accept);
    }

    private static bool IsValidKey(string key)
    {
        Span<byte> bytes = stackalloc byte[24];
        return Convert.TryFromBase64String(key, bytes, out int n) && n == 16;
    }

    /// <summary>Copies both directions until one side closes; returns why it ended.</summary>
    private async Task<string> PumpAsync(Stream ws, Stream tcp)
    {
        using CancellationTokenSource done = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        SemaphoreSlim writeLock = new(1, 1);
        Task<string> up = WsToTcpAsync(ws, tcp, writeLock, done.Token);
        Task<string> down = TcpToWsAsync(tcp, ws, writeLock, done.Token);
        Task<string> first = await Task.WhenAny(up, down).ConfigureAwait(false);
        string reason;
        try { reason = await first.ConfigureAwait(false); }
        catch (Exception ex) { reason = ex.GetType().Name; }
        done.Cancel();
        if (first == down)
        {
            // Server closed: tell the page with a close frame.
            try { await SendFrameAsync(ws, 0x8, ReadOnlyMemory<byte>.Empty, writeLock, CancellationToken.None).ConfigureAwait(false); }
            catch { /* page already gone */ }
        }
        try { await Task.WhenAll(up, down).ConfigureAwait(false); } catch { /* ended by cancellation */ }
        return reason;
    }

    private async Task<string> TcpToWsAsync(Stream tcp, Stream ws, SemaphoreSlim writeLock, CancellationToken ct)
    {
        byte[] buf = new byte[16384];
        while (true)
        {
            int n = await tcp.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) return "server closed";
            Interlocked.Add(ref BytesFromServer, n);
            await SendFrameAsync(ws, 0x2, buf.AsMemory(0, n), writeLock, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> WsToTcpAsync(Stream ws, Stream tcp, SemaphoreSlim writeLock, CancellationToken ct)
    {
        byte[] header = new byte[14];
        while (true)
        {
            if (!await ReadExactAsync(ws, header.AsMemory(0, 2), ct).ConfigureAwait(false)) return "page closed";
            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            long len = header[1] & 0x7F;
            if (!masked) return "unmasked client frame";
            if (len == 126)
            {
                if (!await ReadExactAsync(ws, header.AsMemory(2, 2), ct).ConfigureAwait(false)) return "page closed";
                len = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
            }
            else if (len == 127)
            {
                if (!await ReadExactAsync(ws, header.AsMemory(2, 8), ct).ConfigureAwait(false)) return "page closed";
                len = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(2, 8));
            }
            if (len > _options.MaxFramePayload) return "frame too large";
            byte[] mask = new byte[4];
            if (!await ReadExactAsync(ws, mask, ct).ConfigureAwait(false)) return "page closed";
            byte[] payload = new byte[len];
            if (!await ReadExactAsync(ws, payload, ct).ConfigureAwait(false)) return "page closed";
            for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

            switch (opcode)
            {
                case 0x0: // continuation
                case 0x1: // text (Ruffle sends binary; text is forwarded as bytes too)
                case 0x2: // binary
                    if (payload.Length > 0)
                    {
                        await tcp.WriteAsync(payload, ct).ConfigureAwait(false);
                        Interlocked.Add(ref BytesToServer, payload.Length);
                    }
                    break;
                case 0x8: // close: echo it
                    try { await SendFrameAsync(ws, 0x8, payload.AsMemory(0, Math.Min(payload.Length, 2)), writeLock, ct).ConfigureAwait(false); }
                    catch { /* ignore */ }
                    return "page closed";
                case 0x9: // ping
                    await SendFrameAsync(ws, 0xA, payload, writeLock, ct).ConfigureAwait(false);
                    break;
                case 0xA: // pong
                    break;
                default:
                    return "unknown opcode";
            }
        }
    }

    private static async Task<bool> ReadExactAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await s.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static async Task SendFrameAsync(Stream ws, int opcode, ReadOnlyMemory<byte> payload, SemaphoreSlim writeLock, CancellationToken ct)
    {
        byte[] header = new byte[10];
        int hl;
        header[0] = (byte)(0x80 | opcode);
        if (payload.Length < 126) { header[1] = (byte)payload.Length; hl = 2; }
        else if (payload.Length <= ushort.MaxValue) { header[1] = 126; BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)payload.Length); hl = 4; }
        else { header[1] = 127; BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(2), (ulong)payload.Length); hl = 10; }
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.WriteAsync(header.AsMemory(0, hl), ct).ConfigureAwait(false);
            if (payload.Length > 0) await ws.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        foreach (TcpClient c in _clients.Values) { try { c.Dispose(); } catch { /* ignore */ } }
        if (_acceptLoop is not null) { try { await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ } }
        Report("stopped", "");
    }
}
