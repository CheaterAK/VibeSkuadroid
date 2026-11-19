using Skua.Core.Headless.Protocol;

namespace Skua.Core.Headless.Net;

/// <summary>Settings for <see cref="AqwConnection"/>. Defaults follow the reference clients' measured values.</summary>
public sealed class AqwConnectionOptions
{
    /// <summary>SmartFox API version for verChk. See <see cref="Packets.DefaultApiVersion"/>.</summary>
    public string ApiVersion { get; set; } = Packets.DefaultApiVersion;

    public string Zone { get; set; } = Packets.Zone;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>From socket open to the inventory being loaded.</summary>
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Minimum time between two game packets.</summary>
    public TimeSpan SendGap { get; set; } = TimeSpan.FromMilliseconds(220);

    /// <summary>The gap after a "slow down" warning.</summary>
    public TimeSpan SlowSendGap { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the wider gap lasts after the last warning.</summary>
    public TimeSpan SlowDownHold { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Queue cap; filler (aggroMon, mv) is dropped first, every drop is counted and logged.</summary>
    public int MaxQueuedPackets { get; set; } = 60;

    /// <summary>Clear the server's AFK flag as the game does (World.afkPostpone) before actions and when it is set.</summary>
    public bool ClearAfk { get; set; } = true;

    public TimeSpan AfkClearThrottle { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Silence after a request the server always answers before a probe is sent.</summary>
    public TimeSpan AwaitReplyTimeout { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>How long a probe may go unanswered before the connection is declared dead.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Probe after this much total silence even without a pending request. Zero disables.</summary>
    public TimeSpan IdleProbeAfter { get; set; } = TimeSpan.FromSeconds(120);

    public TimeSpan LivenessCheckInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// After each moveToArea, send moveToCell for the cell the server placed us in, as the Flash client does once
    /// the map loads; without it the server does not consider the character in a cell.
    /// </summary>
    public bool EnterCellAfterJoin { get; set; } = true;

    public int MaxFrameBytes { get; set; } = PacketFramer.DefaultMaxFrameBytes;

    /// <summary>
    /// Log every frame in and out, at Info level and redacted (token, password, e-mail addresses), from the socket
    /// opening until the first moveToArea; the loginResponse is logged in full. For diagnosing refused logins.
    /// </summary>
    public bool TraceHandshake { get; set; } = true;

    /// <summary>Opens the socket. Defaults to a TCP connection; tests or other transports may replace it.</summary>
    public Func<string, int, CancellationToken, ValueTask<Stream>>? Connector { get; set; }
}

/// <summary>
/// What the SmartFox login sends: the account name (lowercased on the wire, as the game does) and the web login's
/// <c>sToken</c>. <see cref="Origin"/> says where the token came from, for logs.
/// </summary>
public sealed class SocketLogin
{
    public SocketLogin(string username, string token, string? origin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(token);
        Username = username;
        Token = token;
        Origin = origin;
    }

    public string Username { get; }

    /// <summary>The socket password. Never logged.</summary>
    public string Token { get; }

    /// <summary>For logs, e.g. "web login #2, 0.4 s before". Must not contain secrets.</summary>
    public string? Origin { get; }

    public override string ToString()
    {
        return $"SocketLogin({Username}, token <redacted>{(Origin is null ? string.Empty : ", " + Origin)})";
    }
}

/// <summary>Lifecycle of one socket session.</summary>
public enum ConnectionState
{
    Created,
    Connecting,
    Handshaking,
    LoggingIn,
    Loading,
    Ready,
    Closed,
}

/// <summary>Why a connection ended.</summary>
public enum DisconnectReason
{
    /// <summary>This client asked for it (<see cref="AqwConnection.DisconnectAsync"/>).</summary>
    Requested,

    /// <summary>The server closed the socket.</summary>
    RemoteClosed,

    /// <summary>The socket failed (reset, unreachable).</summary>
    TransportError,

    /// <summary>Requests and a probe went unanswered.</summary>
    LivenessTimeout,

    /// <summary>The stream broke the framing rules (oversized message).</summary>
    ProtocolError,

    /// <summary>The login was refused (credentials, token, version, server full ...).</summary>
    LoginFailed,

    /// <summary>The character did not load in time.</summary>
    LoginTimeout,

    /// <summary>The same account logged in elsewhere (multiLoginWarning).</summary>
    LoggedInElsewhere,

    /// <summary>The server announced it is logging us out (logoutWarning).</summary>
    Kicked,

    /// <summary>The server reported an invalid session.</summary>
    InvalidSession,

    /// <summary>A cancellation token ended the connection.</summary>
    Cancelled,
}

/// <summary>Everything a reconnect policy needs to decide what to do.</summary>
public sealed record DisconnectInfo(
    DisconnectReason Reason,
    string Message,
    ConnectionState StateBefore,
    DateTimeOffset At,
    string? RequiredClientVersion = null)
{
    /// <summary>The disconnect was asked for by this client.</summary>
    public bool Intentional => Reason is DisconnectReason.Requested or DisconnectReason.Cancelled;

    /// <summary>
    /// Suggestion only: reconnecting is likely to help. False when this client asked for the disconnect, when the
    /// account is in use elsewhere (reconnecting would kick its owner) and for refusals that will repeat.
    /// </summary>
    public bool ShouldReconnect => Reason switch
    {
        DisconnectReason.RemoteClosed or DisconnectReason.TransportError or DisconnectReason.LivenessTimeout
            or DisconnectReason.ProtocolError or DisconnectReason.LoginTimeout or DisconnectReason.Kicked
            or DisconnectReason.InvalidSession => true,
        DisconnectReason.LoginFailed => RequiredClientVersion is not null,
        _ => false,
    };
}

/// <summary>A packet that was written to the socket.</summary>
public sealed class PacketSentEventArgs : EventArgs
{
    public PacketSentEventArgs(string text, bool redacted, DateTimeOffset at)
    {
        Text = text;
        Redacted = redacted;
        At = at;
    }

    /// <summary>The packet text. For the SmartFox login the token is replaced by <c>&lt;token&gt;</c>.</summary>
    public string Text { get; }

    public bool Redacted { get; }

    public DateTimeOffset At { get; }
}

/// <summary>A JSON or string extension response, already serialised the way skua.swf passes it to <c>pext</c>.</summary>
public sealed class ExtensionResponseEventArgs : EventArgs
{
    public ExtensionResponseEventArgs(InboundPacket packet, string json)
    {
        Packet = packet;
        Json = json;
    }

    public InboundPacket Packet { get; }

    /// <summary><c>{"type":"onExtensionResponse","params":{"type":...,"dataObj":...}}</c>.</summary>
    public string Json { get; }
}

public enum HeadlessLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Diagnostics from the connection. Never contains the password or token.</summary>
public sealed class HeadlessLogEventArgs : EventArgs
{
    public HeadlessLogEventArgs(HeadlessLogLevel level, string message)
    {
        Level = level;
        Message = message;
    }

    public HeadlessLogLevel Level { get; }

    public string Message { get; }

    public override string ToString()
    {
        return $"[{Level}] {Message}";
    }
}

/// <summary>Counters for one connection.</summary>
public sealed record ConnectionStatistics(
    long PacketsIn,
    long PacketsOut,
    long BytesIn,
    long BytesOut,
    long CompressedIn,
    long UndecodableIn,
    long OutboundDropped,
    long OutboundSuperseded,
    int OutboundQueued,
    long SlowDownWarnings,
    long AfkClears,
    long Probes,
    long SubscriberErrors,
    DateTimeOffset? LastInbound);

/// <summary>Thrown to waiters when the connection closes under them.</summary>
public sealed class ConnectionClosedException : Exception
{
    public ConnectionClosedException(DisconnectInfo info)
        : base($"Connection closed ({info.Reason}): {info.Message}")
    {
        Info = info;
    }

    public DisconnectInfo Info { get; }
}
