using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;

namespace Skua.Core.Headless;

/// <summary>Same signature as Skua.Core.Interfaces' <c>FlashCallHandler</c>, without depending on it.</summary>
public delegate void HeadlessFlashCallHandler(string function, params object[] args);

/// <summary>
/// Turns session events into the calls skua.swf makes into Skua (<c>IFlashUtil.FlashCall</c>), so a
/// <c>HeadlessFlashUtil</c> can simply forward <see cref="FlashCall"/>:
/// <list type="bullet">
/// <item><c>packetFromServer</c>(text): every received message, decompressed (Main.as packetReceived).</item>
/// <item><c>pext</c>(json): every JSON/xt extension response as <c>JSON.stringify(SFSEvent)</c> (Main.as onExtensionResponse).</item>
/// <item><c>packet</c>(text): every sent <c>%xt%zm%</c> message (Main.as packetReceived, "[Sending - STR]").</item>
/// <item><c>connectionLost</c>(): the socket closed (Main.as onConnectionLost).</item>
/// </list>
/// Order per received message is packetFromServer then pext, as in skua.swf. Unlike skua.swf, outgoing SmartFox
/// XML (which carries the login token) is never reported.
/// </summary>
public sealed class HeadlessFlashEvents : IDisposable
{
    private readonly AqwSession _session;

    public HeadlessFlashEvents(AqwSession session)
    {
        _session = session;
        _session.PacketReceived += OnPacketReceived;
        _session.PacketSent += OnPacketSent;
        _session.Disconnected += OnDisconnected;
    }

    public event HeadlessFlashCallHandler? FlashCall;

    private void OnPacketReceived(object? sender, InboundPacket packet)
    {
        HeadlessFlashCallHandler? handler = FlashCall;
        if (handler is null)
            return;
        handler("packetFromServer", packet.Text);
        if (packet.ToExtensionResponseJson() is string json)
            handler("pext", json);
    }

    private void OnPacketSent(object? sender, PacketSentEventArgs e)
    {
        if (e.Text.StartsWith("%xt%zm%", StringComparison.Ordinal))
            FlashCall?.Invoke("packet", e.Text.Trim());
    }

    private void OnDisconnected(object? sender, DisconnectInfo info)
    {
        FlashCall?.Invoke("connectionLost");
    }

    public void Dispose()
    {
        _session.PacketReceived -= OnPacketReceived;
        _session.PacketSent -= OnPacketSent;
        _session.Disconnected -= OnDisconnected;
    }
}
