namespace Skua.Core.Headless.Net;

/// <summary>What the liveness check decided.</summary>
internal enum LivenessVerdict
{
    Ok,

    /// <summary>Send a probe (a request the server always answers) now.</summary>
    Probe,

    /// <summary>A probe is out and its time has not run out yet.</summary>
    Probing,

    /// <summary>The probe went unanswered: the connection is dead.</summary>
    Dead,
}

/// <summary>
/// Decides whether a silent connection is dead. An OS can take many minutes to notice a socket the network
/// silently dropped; meanwhile commands vanish. Rule (after Seraph's headless client, MIT): if a request the server
/// always answers got nothing back within <c>awaitReply</c>, or nothing at all arrived for <c>idleProbe</c>, send a
/// probe; if the probe also goes unanswered for <c>probeTimeout</c>, the connection is dead.
/// </summary>
internal sealed class LivenessMonitor
{
    private readonly object _gate = new();
    private readonly TimeSpan _awaitReply;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _idleProbe;
    private DateTimeOffset _lastInbound;
    private DateTimeOffset? _awaitingSince;
    private DateTimeOffset? _probeAt;

    public LivenessMonitor(TimeSpan awaitReply, TimeSpan probeTimeout, TimeSpan idleProbe, DateTimeOffset now)
    {
        _awaitReply = awaitReply;
        _probeTimeout = probeTimeout;
        _idleProbe = idleProbe;
        _lastInbound = now;
    }

    public DateTimeOffset LastInbound
    {
        get { lock (_gate) return _lastInbound; }
    }

    public void OnInbound(DateTimeOffset now)
    {
        lock (_gate)
        {
            _lastInbound = now;
            _awaitingSince = null;
            _probeAt = null;
        }
    }

    /// <summary>A request that the server always answers was sent.</summary>
    public void OnRequestExpectingReply(DateTimeOffset now)
    {
        lock (_gate) _awaitingSince ??= now;
    }

    public void OnProbeSent(DateTimeOffset now)
    {
        lock (_gate) _probeAt = now;
    }

    public LivenessVerdict Check(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_probeAt is DateTimeOffset probe)
                return now - probe >= _probeTimeout ? LivenessVerdict.Dead : LivenessVerdict.Probing;
            if (_awaitingSince is DateTimeOffset since && now - since >= _awaitReply && now - _lastInbound >= _awaitReply)
                return LivenessVerdict.Probe;
            if (_idleProbe > TimeSpan.Zero && now - _lastInbound >= _idleProbe)
                return LivenessVerdict.Probe;
            return LivenessVerdict.Ok;
        }
    }
}
