namespace Skua.Core.Headless.Net;

/// <summary>
/// A game message offered to an <see cref="IPacketInterceptor"/>: the headless counterpart of Skua's
/// <c>MessageInfo</c> (the desktop's capture proxy). Change <see cref="Text"/> to change what is sent or applied; set
/// <see cref="Blocked"/> to drop it.
/// </summary>
public sealed class InterceptedFrame
{
    public InterceptedFrame(bool outbound, string text, string? command)
    {
        Outbound = outbound;
        Original = text;
        Text = text;
        Command = command;
    }

    /// <summary>Client to server (true) or server to client (false).</summary>
    public bool Outbound { get; }

    /// <summary>The message as it arrived at the interceptor (decompressed when it came compressed).</summary>
    public string Original { get; }

    /// <summary>The command (<c>%xt%zm%command%...</c>, the xt command, or a JSON message's <c>cmd</c>).</summary>
    public string? Command { get; }

    /// <summary>What is sent or applied; starts as <see cref="Original"/>.</summary>
    public string Text { get; set; }

    /// <summary>Drop the message: not sent (outbound), or not applied nor reported to anyone (inbound).</summary>
    public bool Blocked { get; set; }

    public bool Changed => Blocked || !string.Equals(Text, Original, StringComparison.Ordinal);
}

/// <summary>
/// Sees, changes or drops game messages on an <see cref="AqwConnection"/> (set
/// <see cref="AqwConnectionOptions.Interceptor"/>): the desktop's capture-proxy interceptors, directly on the
/// headless connection, which has no separate client to put a proxy in front of.
/// </summary>
/// <remarks>
/// <para>Safety, enforced by the connection and not up to the interceptor:</para>
/// <list type="bullet">
/// <item>nothing is offered before the character of a connection has loaded (after the first <c>moveToArea</c>, the
/// character data and the inventory): the SmartFox handshake, the login (with the token), the login response and the
/// character load are never seen, changed or dropped; a reconnect starts over;</item>
/// <item>SmartFox system (XML) messages never, only <c>%xt%</c> and JSON game messages;</item>
/// <item>a message that contains a redacted secret (the login token) is passed on untouched and not offered;</item>
/// <item>the messages the connection's own safety depends on are never offered (<see cref="InterceptionRules.ProtectedInbound"/>,
/// <see cref="InterceptionRules.ProtectedOutbound"/>): login, the liveness probe, "slow down" warnings and logout notices;</item>
/// <item>an outbound change must still be one <c>%xt%</c> message without NUL; an inbound change must still parse;
/// otherwise the original is used. An interceptor that throws is logged and the original is used.</item>
/// </list>
/// <para>Called on the connection's reader (inbound) and writer (outbound) threads: keep it short.</para>
/// </remarks>
public interface IPacketInterceptor
{
    /// <summary>Cheap: when false, no frame is built (the off state costs one check per message).</summary>
    bool IsActive { get; }

    void Intercept(InterceptedFrame frame);
}

/// <summary>The commands an interceptor never sees (see <see cref="IPacketInterceptor"/>).</summary>
public static class InterceptionRules
{
    /// <summary>
    /// Server messages the connection reacts to itself: the login answer, the "slow down" warnings (the send pacer
    /// widens its gap on them), the other-login and logout notices.
    /// </summary>
    public static IReadOnlySet<string> ProtectedInbound { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "loginResponse", "warning", "server", "multiLoginWarning", "logoutWarning", "logout",
    };

    /// <summary>Client messages of the login and the connection's liveness probe.</summary>
    public static IReadOnlySet<string> ProtectedOutbound { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "login", "firstJoin", "retrieveUserData",
    };
}
