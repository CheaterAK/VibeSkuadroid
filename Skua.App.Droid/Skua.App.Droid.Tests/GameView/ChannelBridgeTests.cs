using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Skua.App.Droid.Core.GameView;

namespace Skua.App.Droid.Tests.GameView;

public sealed class ChannelBridgeTests : IAsyncLifetime
{
    private readonly TcpListener _game = new(IPAddress.Loopback, 0);
    private readonly BlockingCollection<string> _toPage = new();
    private readonly ConcurrentQueue<GameEndpoint> _connectedTo = new();
    private readonly GameEndpoint _endpoint = new("sock7.aq.com", 5593);
    private ChannelBridge _bridge = null!;
    private TaskCompletionSource<TcpClient> _accepted = new();

    public ValueTask InitializeAsync()
    {
        _game.Start();
        int port = ((IPEndPoint)_game.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { _accepted.TrySetResult(await _game.AcceptTcpClientAsync()); } catch { return; }
            }
        });
        _bridge = new ChannelBridge(new ServerAllowlist([_endpoint]), _toPage.Add, async (e, ct) =>
        {
            _connectedTo.Enqueue(e);
            TcpClient c = new();
            await c.ConnectAsync(IPAddress.Loopback, port, ct);
            return c.GetStream();
        });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _bridge.DisposeAsync();
        _game.Stop();
    }

    private string Next()
    {
        Assert.True(_toPage.TryTake(out string? m, TimeSpan.FromSeconds(5)), "no message to the page");
        return m!;
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.ASCII.GetBytes(s));

    [Fact]
    public async Task Relays_in_order_including_data_sent_before_the_connection_is_up()
    {
        _bridge.Receive("o|1|sock7.aq.com|5593");
        _bridge.Receive("d|1|" + B64("first,")); // Ruffle sends at once; the bridge must queue it
        _bridge.Receive("d|1|" + B64("second"));
        Assert.Equal("O|1", Next());
        TcpClient server = await _accepted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        NetworkStream s = server.GetStream();
        byte[] buf = new byte[64];
        string got = "";
        while (got.Length < "first,second".Length)
        {
            int n = await s.ReadAsync(buf, TestContext.Current.CancellationToken);
            got += Encoding.ASCII.GetString(buf, 0, n);
        }
        Assert.Equal("first,second", got);

        await s.WriteAsync(Encoding.ASCII.GetBytes("reply\0"), TestContext.Current.CancellationToken);
        Assert.Equal("D|1|" + B64("reply\0"), Next());

        server.Dispose(); // server closes: the page is told
        Assert.Equal("C|1|server closed", Next());
        Assert.Equal(0, _bridge.ActiveConnections);
        Assert.Equal([_endpoint], _connectedTo);
    }

    [Theory]
    [InlineData("o|1|sock8.aq.com|5593")]
    [InlineData("o|1|sock7.aq.com|5594")]
    [InlineData("o|1|evil.example.com|5593")]
    [InlineData("o|1|127.0.0.1|5593")]
    [InlineData("o|1|sock7.aq.com|+5593")]
    public void Refuses_endpoints_not_in_the_server_list(string open)
    {
        _bridge.Receive(open);
        Assert.Equal("C|1|refused", Next());
        Assert.Empty(_connectedTo);
        Assert.Equal(1, _bridge.Rejections["endpoint not in server list"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("o")]
    [InlineData("x|1")]
    [InlineData("o|abc|sock7.aq.com|5593")]
    [InlineData("o|1|sock7.aq.com")]
    [InlineData("d|1|not base64!")]
    [InlineData("d|9|AAAA")]
    public void Drops_malformed_messages(string message)
    {
        _bridge.Receive(message);
        Assert.Equal(1, _bridge.Rejections.Values.Sum());
        Assert.Empty(_connectedTo);
    }

    [Fact]
    public async Task Page_close_closes_the_server_connection()
    {
        _bridge.Receive("o|7|sock7.aq.com|5593");
        Assert.Equal("O|7", Next());
        TcpClient server = await _accepted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _bridge.Receive("c|7");
        int n = await server.GetStream().ReadAsync(new byte[8], TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, n);
        Assert.False(_toPage.TryTake(out _, TimeSpan.FromMilliseconds(300))); // no "C" back for a page-initiated close
    }
}
