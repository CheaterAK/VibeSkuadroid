using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RuffleSpike.Core;

// Stands in for the app's socket side in the Chromium check. Both transports use the real bridge classes; the
// "game server" behind them is a local echo server that reports (on stderr) how many bytes arrived and their first
// printable characters, so the check can see the game's own SmartFox client reach it.
//   BridgeProbe loopback : prints {"transport":"loopback","socketProxy":[...]} and serves until stdin closes.
//   BridgeProbe channel  : prints {"transport":"channel","socketProxy":[...]}, then reads page-to-app channel
//                          messages from stdin (one per line) and writes app-to-page messages to stdout.
string mode = args.Length > 0 ? args[0] : "loopback";
TcpListener echo = new(IPAddress.Loopback, 0);
echo.Start();
int echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
_ = Task.Run(async () =>
{
    while (true)
    {
        TcpClient c = await echo.AcceptTcpClientAsync();
        _ = Task.Run(async () =>
        {
            using (c)
            {
                byte[] buf = new byte[65536];
                NetworkStream s = c.GetStream();
                int n;
                while ((n = await s.ReadAsync(buf)) > 0)
                {
                    string text = new(Encoding.ASCII.GetString(buf, 0, Math.Min(n, 80)).Select(ch => ch is >= ' ' and < (char)127 ? ch : '.').ToArray());
                    Console.Error.WriteLine($"probe server: received {n} bytes: {text}");
                    await s.WriteAsync(buf.AsMemory(0, n));
                }
            }
        });
    }
});

GameEndpoint endpoint = new("sock7.aq.com", 5593);
ServerAllowlist allowlist = new([endpoint]);
async Task<Stream> ConnectToEcho(GameEndpoint e, CancellationToken ct)
{
    TcpClient c = new();
    await c.ConnectAsync(IPAddress.Loopback, echoPort, ct);
    return c.GetStream();
}
void Log(BridgeEvent e) => Console.Error.WriteLine($"bridge: {e.Kind} {e.Detail}");

if (mode == "channel")
{
    object outLock = new();
    await using ChannelBridge bridge = new(allowlist, msg => { lock (outLock) { Console.Out.WriteLine(msg); Console.Out.Flush(); } }, ConnectToEcho, Log);
    lock (outLock) Console.Out.WriteLine(JsonSerializer.Serialize(new { transport = "channel", socketProxy = new[] { new { host = endpoint.Host, port = endpoint.Port, proxyUrl = ChannelBridge.ProxyUrl(endpoint) } } }));
    Console.Out.Flush();
    string? line;
    while ((line = await Console.In.ReadLineAsync()) is not null) bridge.Receive(line);
    Console.Error.WriteLine($"bridge: total {bridge.TotalConnections} up {bridge.BytesToServer} B down {bridge.BytesFromServer} B rejected {bridge.Rejections.Values.Sum()}");
}
else
{
    await using SocketBridge bridge = new(BridgeSecret.Create(), allowlist, new SocketBridgeOptions { Connector = ConnectToEcho }, Log);
    bridge.Start();
    Console.WriteLine(JsonSerializer.Serialize(new { transport = "loopback", socketProxy = new[] { new { host = endpoint.Host, port = endpoint.Port, proxyUrl = bridge.ProxyUrl(endpoint) } } }));
    Console.Out.Flush();
    await Console.In.ReadToEndAsync();
    Console.Error.WriteLine($"bridge: total {bridge.TotalConnections} up {bridge.BytesToServer} B down {bridge.BytesFromServer} B rejected {bridge.Rejections.Values.Sum()}");
}
