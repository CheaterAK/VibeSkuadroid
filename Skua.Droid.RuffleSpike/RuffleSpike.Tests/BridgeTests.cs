using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using RuffleSpike.Core;

namespace RuffleSpike.Tests;

public class ServerAllowlistTests
{
    // Shape of https://game.aq.com/game/api/data/servers (2026-11-19, shortened).
    private const string Live = """
        [{"sName":"Twilly","sIP":"sock7.aq.com","iCount":10,"iMax":1000,"bOnline":1,"iChat":2,"bUpg":0,"sLang":"xx","iPort":5593,"iLevel":0,"accessLevel":0},
         {"sName":"Twig","sIP":"sock8.aq.com","iPort":5589},
         {"sName":"Swordhaven (EU)","sIP":"euro.aqw.artix.com","iPort":5588},
         {"sName":"Yokai (SEA)","sIP":"Asia.Game.Artix.com","iPort":"5588"},
         {"sName":"Bad ip","sIP":"172.65.225.46","iPort":5588},
         {"sName":"Bad host","sIP":"evil.example.com","iPort":5588},
         {"sName":"Suffix trick","sIP":"sock7.aq.com.evil.net","iPort":5588},
         {"sName":"Lookalike","sIP":"evilaq.com","iPort":5588},
         {"sName":"Bad port","sIP":"sock7.aq.com","iPort":70000},
         {"sName":"No port","sIP":"sock7.aq.com"}]
        """;

    [Fact]
    public void Parses_the_live_list_and_skips_bad_entries()
    {
        ServerAllowlist a = ServerAllowlist.Parse(Live, out int skipped);
        Assert.Equal(6, skipped);
        Assert.Equal(4, a.Endpoints.Count);
        Assert.True(a.IsAllowed("sock7.aq.com", 5593));
        Assert.True(a.IsAllowed("SOCK7.AQ.COM", 5593));
        Assert.True(a.IsAllowed("asia.game.artix.com", 5588));
        Assert.True(a.IsAllowed("euro.aqw.artix.com", 5588));
    }

    [Theory]
    [InlineData("sock7.aq.com", 5594)]       // listed host, unlisted port
    [InlineData("sock8.aq.com", 5593)]
    [InlineData("sock7.aq.com", 843)]        // policy port: not needed under Ruffle
    [InlineData("game.aq.com", 443)]
    [InlineData("172.65.225.46", 5588)]
    [InlineData("evil.example.com", 5588)]
    [InlineData("sock7.aq.com.evil.net", 5588)]
    [InlineData("127.0.0.1", 5593)]
    [InlineData("localhost", 5593)]
    [InlineData("", 5593)]
    [InlineData("sock7.aq.com", 0)]
    [InlineData("sock7..aq.com", 5593)]
    [InlineData("sock7.aq.com.", 5593)]
    public void Rejects_anything_not_in_the_list(string host, int port)
    {
        ServerAllowlist a = ServerAllowlist.Parse(Live, out _);
        Assert.False(a.IsAllowed(host, port));
    }

    [Fact]
    public void Rejects_a_list_that_is_not_an_array() =>
        Assert.Throws<FormatException>(() => ServerAllowlist.Parse("{\"sIP\":\"sock7.aq.com\"}", out _));
}

public class BridgeSecretTests
{
    [Fact]
    public void Created_secrets_are_long_random_and_url_safe()
    {
        BridgeSecret a = BridgeSecret.Create(), b = BridgeSecret.Create();
        Assert.Equal(43, a.Value.Length);
        Assert.NotEqual(a.Value, b.Value);
        Assert.Matches("^[A-Za-z0-9_-]+$", a.Value);
        Assert.Equal("(secret)", a.ToString());
    }

    [Fact]
    public void Matches_only_the_exact_secret()
    {
        BridgeSecret s = BridgeSecret.Create();
        Assert.True(s.Matches(s.Value));
        Assert.False(s.Matches(s.Value[..^1]));
        Assert.False(s.Matches(s.Value + "x"));
        Assert.False(s.Matches(s.Value[..^1] + (s.Value[^1] == 'a' ? 'b' : 'a')));
        Assert.False(s.Matches(""));
        Assert.False(s.Matches(s.Value.ToUpperInvariant() == s.Value ? s.Value.ToLowerInvariant() : s.Value.ToUpperInvariant()));
    }

    [Fact]
    public void Short_secrets_are_refused() => Assert.Throws<ArgumentException>(() => new BridgeSecret("short"));
}

public class BridgeHandshakeTests
{
    private static readonly BridgeSecret Secret = new("abcdefghijklmnopqrstuvwxyz0123456789-_ABCDE");
    private static readonly ServerAllowlist List = new([new GameEndpoint("sock7.aq.com", 5593), new GameEndpoint("euro.aqw.artix.com", 5588)]);
    private static readonly string[] Origins = ["https://game.aq.com"];

    private static string Head(string path, string origin = "https://game.aq.com", string method = "GET", string key = "dGhlIHNhbXBsZSBub25jZQ==", string version = "13", bool upgrade = true) =>
        $"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:5000\r\n"
        + (upgrade ? "Upgrade: websocket\r\nConnection: keep-alive, Upgrade\r\n" : "")
        + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: {version}\r\n"
        + (origin.Length > 0 ? $"Origin: {origin}\r\n" : "") + "\r\n";

    [Fact]
    public void Accepts_the_right_secret_origin_and_endpoint()
    {
        BridgeRequestCheck c = SocketBridge.CheckRequest(Head($"/{Secret.Value}/sock7.aq.com/5593"), Secret, List, Origins);
        Assert.True(c.Ok, c.Reason);
        Assert.Equal(new GameEndpoint("sock7.aq.com", 5593), c.Target);
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", c.AcceptKey); // RFC 6455 example
    }

    [Theory]
    [InlineData("/WRONGghijklmnopqrstuvwxyz0123456789-_ABCDE/sock7.aq.com/5593", "wrong secret")]
    [InlineData("/sock7.aq.com/5593", "bad path")]
    [InlineData("/{s}/sock7.aq.com/5594", "endpoint not in server list")]
    [InlineData("/{s}/evil.example.com/5593", "endpoint not in server list")]
    [InlineData("/{s}/127.0.0.1/5593", "endpoint not in server list")]
    [InlineData("/{s}/sock7.aq.com/5593/x", "bad path")]
    [InlineData("/{s}/sock7.aq.com/5593?host=evil.example.com", "bad path")]
    [InlineData("/{s}/sock7.aq.com/+5593", "bad port")]
    [InlineData("/{s}/sock7%2Eaq.com/5593", "bad path")]
    [InlineData("/{s}/sock7.aq.com/005593", "bad port")]
    public void Rejects_bad_paths(string path, string reason)
    {
        BridgeRequestCheck c = SocketBridge.CheckRequest(Head(path.Replace("{s}", Secret.Value)), Secret, List, Origins);
        Assert.False(c.Ok);
        Assert.Equal(reason, c.Reason);
    }

    [Fact]
    public void Rejects_other_origins_and_non_websocket_requests()
    {
        string p = $"/{Secret.Value}/sock7.aq.com/5593";
        Assert.Equal("origin not allowed", SocketBridge.CheckRequest(Head(p, origin: ""), Secret, List, Origins).Reason);
        Assert.Equal("origin not allowed", SocketBridge.CheckRequest(Head(p, origin: "https://evil.example.com"), Secret, List, Origins).Reason);
        Assert.Equal("origin not allowed", SocketBridge.CheckRequest(Head(p, origin: "null"), Secret, List, Origins).Reason);
        Assert.Equal("not a GET request", SocketBridge.CheckRequest(Head(p, method: "POST"), Secret, List, Origins).Reason);
        Assert.Equal("not a websocket upgrade", SocketBridge.CheckRequest(Head(p, upgrade: false), Secret, List, Origins).Reason);
        Assert.Equal("unsupported websocket version", SocketBridge.CheckRequest(Head(p, version: "8"), Secret, List, Origins).Reason);
        Assert.Equal("bad websocket key", SocketBridge.CheckRequest(Head(p, key: "short"), Secret, List, Origins).Reason);
    }
}

public sealed class BridgeEndToEndTests : IAsyncLifetime
{
    private readonly TcpListener _game = new(IPAddress.Loopback, 0);
    private readonly List<string> _events = [];
    private SocketBridge _bridge = null!;
    private BridgeSecret _secret = null!;
    private int _gamePort;
    private readonly GameEndpoint _endpoint = new("sock7.aq.com", 5593);
    private readonly List<GameEndpoint> _connectedTo = [];

    public ValueTask InitializeAsync()
    {
        _game.Start();
        _gamePort = ((IPEndPoint)_game.LocalEndpoint).Port;
        _ = Task.Run(EchoServerAsync);
        _secret = BridgeSecret.Create();
        _bridge = new SocketBridge(_secret, new ServerAllowlist([_endpoint]), new SocketBridgeOptions
        {
            // Stands in for DNS + the real server: every allowed endpoint goes to the local echo server.
            Connector = async (e, ct) =>
            {
                lock (_connectedTo) _connectedTo.Add(e);
                TcpClient c = new();
                await c.ConnectAsync(IPAddress.Loopback, _gamePort, ct);
                return c.GetStream();
            },
        }, e => { lock (_events) _events.Add(e.Kind + " " + e.Detail); });
        _bridge.Start();
        return ValueTask.CompletedTask;
    }

    // Echoes bytes back; closes the connection when it receives "BYE".
    private async Task EchoServerAsync()
    {
        while (true)
        {
            TcpClient c;
            try { c = await _game.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    NetworkStream s = c.GetStream();
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = await s.ReadAsync(buf)) > 0)
                    {
                        if (Encoding.ASCII.GetString(buf, 0, n).Contains("BYE")) return;
                        await s.WriteAsync(buf.AsMemory(0, n));
                    }
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _bridge.DisposeAsync();
        _game.Stop();
    }

    private async Task<ClientWebSocket> ConnectAsync(string url, string origin = "https://game.aq.com")
    {
        ClientWebSocket ws = new();
        ws.Options.SetRequestHeader("Origin", origin);
        await ws.ConnectAsync(new Uri(url), TestContext.Current.CancellationToken);
        return ws;
    }

    private static async Task<byte[]> ReceiveAsync(ClientWebSocket ws, int count)
    {
        byte[] buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, got, count - got), TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketMessageType.Binary, r.MessageType);
            got += r.Count;
        }
        return buf;
    }

    [Fact]
    public async Task Relays_bytes_both_ways_without_changing_them()
    {
        using ClientWebSocket ws = await ConnectAsync(_bridge.ProxyUrl(_endpoint));
        byte[] small = Encoding.ASCII.GetBytes("%xt%zm%cmd%1%\0");
        await ws.SendAsync(small, WebSocketMessageType.Binary, true, TestContext.Current.CancellationToken);
        Assert.Equal(small, await ReceiveAsync(ws, small.Length));

        byte[] big = new byte[70000]; // 64-bit length frame
        new Random(1).NextBytes(big);
        for (int i = 0; i < big.Length; i++) if (big[i] == (byte)'B') big[i] = 0; // keep "BYE" out
        await ws.SendAsync(big, WebSocketMessageType.Binary, true, TestContext.Current.CancellationToken);
        Assert.Equal(big, await ReceiveAsync(ws, big.Length));

        Assert.Equal([_endpoint], _connectedTo);
        Assert.Equal(1, _bridge.ActiveConnections);
        Assert.True(_bridge.BytesToServer >= small.Length + big.Length);
    }

    [Fact]
    public async Task Server_close_reaches_the_page()
    {
        using ClientWebSocket ws = await ConnectAsync(_bridge.ProxyUrl(_endpoint));
        await ws.SendAsync(Encoding.ASCII.GetBytes("BYE"), WebSocketMessageType.Binary, true, TestContext.Current.CancellationToken);
        WebSocketReceiveResult r = await ws.ReceiveAsync(new byte[16], TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("wrong-origin")]
    [InlineData("unlisted-endpoint")]
    public async Task Refuses_bad_requests_before_any_connection_is_made(string kind)
    {
        string url = kind switch
        {
            "wrong-secret" => $"ws://127.0.0.1:{_bridge.Port}/{BridgeSecret.Create().Value}/sock7.aq.com/5593",
            "unlisted-endpoint" => $"ws://127.0.0.1:{_bridge.Port}/{_secret.Value}/sock8.aq.com/5593",
            _ => _bridge.ProxyUrl(_endpoint),
        };
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(url, kind == "wrong-origin" ? "https://evil.example.com" : "https://game.aq.com"));
        Assert.Empty(_connectedTo);
        Assert.Equal(1, _bridge.Rejections.Values.Sum());
    }

    [Fact]
    public void Listens_on_loopback_only_and_never_reports_the_secret()
    {
        Assert.StartsWith("ws://127.0.0.1:", _bridge.ProxyUrl(_endpoint));
        lock (_events) Assert.DoesNotContain(_events, e => e.Contains(_secret.Value));
    }
}
