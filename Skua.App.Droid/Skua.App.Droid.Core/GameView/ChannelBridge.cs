using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Globalization;
using System.Threading.Channels;

namespace Skua.App.Droid.Core.GameView;

/// <summary>Something the socket bridge did, for the game view's report (never payloads).</summary>
public readonly record struct BridgeEvent(string Kind, string Detail);

/// <summary>
/// The default socket transport: no listening socket at all. The page replaces <c>window.WebSocket</c> for URLs
/// under <see cref="UrlPrefix"/> with a shim (GameView/web/gameview.js in the Android head) that talks to the app over a private
/// WebMessagePort (WebView.postWebMessage, delivered only to the page's origin, https://game.aq.com). This class
/// is the app side of that channel: one TCP connection per page socket, only to endpoints in the live server list.
///
/// Ported from the game view spike (Skua.Droid.RuffleSpike), which also had a loopback WebSocket transport; only
/// the channel is shipped: there is no listening socket at all (any app on the phone could reach a loopback port, and
/// Chromium refuses ws://127.0.0.1 from a public https origin).
///
/// Protocol (strings; payloads base64): page to app "o|id|host|port", "d|id|b64", "c|id";
/// app to page "O|id" (connected), "D|id|b64", "C|id|reason" (closed).
/// </summary>
public sealed class ChannelBridge : IAsyncDisposable
{
    public const string UrlPrefix = "wss://vibeskua-bridge.invalid/";
    private const int MaxMessageChars = 2 << 20;

    private readonly ServerAllowlist _allowlist;
    private readonly Action<string> _send;
    private readonly Func<GameEndpoint, CancellationToken, Task<Stream>> _connector;
    private readonly Action<BridgeEvent>? _onEvent;
    private readonly int _maxConnections;
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly CancellationTokenSource _cts = new();

    public ChannelBridge(ServerAllowlist allowlist, Action<string> send, Func<GameEndpoint, CancellationToken, Task<Stream>>? connector = null,
        Action<BridgeEvent>? onEvent = null, int maxConnections = 4)
    {
        _allowlist = allowlist;
        _send = send;
        _connector = connector ?? DefaultConnectAsync;
        _onEvent = onEvent;
        _maxConnections = maxConnections;
    }

    public static string ProxyUrl(GameEndpoint e) => $"{UrlPrefix}{e.Host}/{e.Port}";

    public int ActiveConnections => _connections.Count;
    public long TotalConnections;
    public long BytesToServer;
    public long BytesFromServer;
    public ConcurrentDictionary<string, int> Rejections { get; } = new();

    private void Report(string kind, string detail)
    {
        try { _onEvent?.Invoke(new BridgeEvent(kind, detail)); } catch { /* reporting must not break the bridge */ }
    }

    private void Reject(string reason)
    {
        Rejections.AddOrUpdate(reason, 1, (_, n) => n + 1);
        Report("rejected", reason);
    }

    private void Send(string message)
    {
        try { _send(message); } catch (Exception ex) { Report("send_failed", ex.GetType().Name); }
    }

    private static bool IsId(string s) => s.Length is > 0 and <= 9 && s.All(char.IsAsciiDigit);

    /// <summary>Handles one message from the page. Never throws; bad messages are counted and dropped.</summary>
    public void Receive(string message)
    {
        if (_cts.IsCancellationRequested) return;
        if (message.Length > MaxMessageChars) { Reject("message too large"); return; }
        string[] p = message.Split('|');
        if (p.Length < 2 || !IsId(p[1])) { Reject("malformed message"); return; }
        string id = p[1];
        switch (p[0])
        {
            case "o" when p.Length == 4:
                Open(id, p[2], p[3]);
                break;
            case "d" when p.Length == 3:
                byte[] data;
                try { data = Convert.FromBase64String(p[2]); }
                catch (FormatException) { Reject("bad base64"); return; }
                if (_connections.TryGetValue(id, out Connection? c)) c.Outgoing.Writer.TryWrite(data);
                else Reject("data for unknown socket");
                break;
            case "c" when p.Length == 2:
                if (_connections.TryRemove(id, out Connection? closing)) closing.Dispose("page closed");
                break;
            default:
                Reject("malformed message");
                break;
        }
    }

    private void Open(string id, string host, string portText)
    {
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || portText.Length > 5
            || !_allowlist.IsAllowed(host, port))
        {
            Reject("endpoint not in server list");
            Send($"C|{id}|refused");
            return;
        }
        ServerAllowlist.TryNormalize(host, port, out GameEndpoint target);
        if (_connections.Count >= _maxConnections) { Reject("too many connections"); Send($"C|{id}|refused"); return; }
        Connection conn = new(target, CancellationTokenSource.CreateLinkedTokenSource(_cts.Token));
        if (!_connections.TryAdd(id, conn)) { Reject("duplicate socket id"); conn.Dispose("duplicate"); return; }
        Interlocked.Increment(ref TotalConnections);
        _ = Task.Run(() => RunAsync(id, conn));
    }

    private async Task RunAsync(string id, Connection conn)
    {
        string reason = "closed";
        try
        {
            using (CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(conn.Cts.Token))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                try { conn.Stream = await _connector(conn.Target, connectCts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException || !conn.Cts.IsCancellationRequested)
                {
                    reason = "connect failed";
                    Report("connect_failed", $"{conn.Target}: {ex.GetType().Name}: {(ex.Message.Length > 120 ? ex.Message[..120] : ex.Message)}");
                    return;
                }
            }
            Send($"O|{id}");
            Report("connected", conn.Target.ToString());
            Task writer = WriteLoopAsync(conn);
            byte[] buf = new byte[16384];
            while (true)
            {
                int n = await conn.Stream.ReadAsync(buf, conn.Cts.Token).ConfigureAwait(false);
                if (n == 0) { reason = "server closed"; break; }
                Interlocked.Add(ref BytesFromServer, n);
                conn.BytesDown += n;
                Send($"D|{id}|{Convert.ToBase64String(buf, 0, n)}");
            }
            conn.Outgoing.Writer.TryComplete();
            try { await writer.ConfigureAwait(false); } catch { /* closing */ }
        }
        catch (OperationCanceledException) { reason = conn.CloseReason ?? "cancelled"; }
        catch (Exception ex) { reason = ex.GetType().Name; }
        finally
        {
            bool pageStillOpen = _connections.TryRemove(id, out _);
            if (pageStillOpen) Send($"C|{id}|{reason}");
            Report("closed", $"{conn.Target} ({reason}; {conn.BytesUp} B up, {conn.BytesDown} B down)");
            conn.Dispose(reason);
            try { conn.Stream?.Dispose(); } catch { /* connected after the page closed */ }
        }
    }

    private async Task WriteLoopAsync(Connection conn)
    {
        await foreach (byte[] data in conn.Outgoing.Reader.ReadAllAsync(conn.Cts.Token).ConfigureAwait(false))
        {
            await conn.Stream!.WriteAsync(data, conn.Cts.Token).ConfigureAwait(false);
            Interlocked.Add(ref BytesToServer, data.Length);
            conn.BytesUp += data.Length;
        }
    }

    private sealed class Connection(GameEndpoint target, CancellationTokenSource cts)
    {
        public GameEndpoint Target { get; } = target;
        public CancellationTokenSource Cts { get; } = cts;
        // Data the page sends before the TCP connection is up waits here (Ruffle reports "connected" at once).
        public Channel<byte[]> Outgoing { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        public Stream? Stream;
        public long BytesUp, BytesDown;
        public string? CloseReason;
        private int _disposed;

        public void Dispose(string reason)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            CloseReason ??= reason;
            Outgoing.Writer.TryComplete();
            try { Cts.Cancel(); } catch { /* ignore */ }
            try { Stream?.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>All connections closed (the game logged out, or the bridge stopped).</summary>
    public bool Idle => _connections.IsEmpty;

    /// <summary>Plain TCP to the game server (the endpoint was checked against the server list before).</summary>
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

    public ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return ValueTask.CompletedTask;
        _cts.Cancel();
        foreach (var kv in _connections) kv.Value.Dispose("app closed");
        _connections.Clear();
        Report("stopped", "");
        return ValueTask.CompletedTask;
    }
}
