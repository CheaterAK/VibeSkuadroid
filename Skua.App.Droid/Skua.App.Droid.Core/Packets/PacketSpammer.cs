using System.Globalization;
using Skua.App.Droid.Core.Engine;

namespace Skua.App.Droid.Core.Packets;

/// <summary>Why a spammer run ended.</summary>
public enum SpamStopReason
{
    /// <summary>Stopped by the user (or the page).</summary>
    Stopped,

    /// <summary>The run sent its maximum number of packets.</summary>
    LimitReached,

    /// <summary>Not online, or the connection dropped.</summary>
    Disconnected,

    /// <summary>The server warned "slow down" during the run.</summary>
    SlowDown,

    /// <summary>A packet was not sent (send queue full or the connection closing).</summary>
    Dropped,

    /// <summary>Something failed (the message says what).</summary>
    Failed,
}

/// <summary>The end of a run.</summary>
public sealed record SpamResult(SpamStopReason Reason, int Sent, string Message);

/// <summary>A packet about to be sent: its position in the list, and how many the run sent so far.</summary>
public sealed record SpamProgress(int Index, int Count, int Sent, int MaxSends);

/// <summary>
/// The spammer's limits. The interval is never below <see cref="MinimumInterval"/>, nor below twice the gap the
/// headless send pacer keeps after a "slow down" warning or four times its normal gap (the game's own pacing); a run
/// never sends more than <see cref="MaxSendsPerRun"/> packets.
/// </summary>
public sealed record SpammerLimits(TimeSpan MinimumInterval, int MaxSendsPerRun)
{
    /// <summary>1 s (the desktop's default delay) and 500 packets.</summary>
    public static SpammerLimits Default { get; } = new(TimeSpan.FromSeconds(1), 500);

    /// <summary>The shortest interval allowed on <paramref name="connection"/> (its pacer's gaps).</summary>
    public TimeSpan MinimumFor(ConnectionDetails? connection)
    {
        TimeSpan min = MinimumInterval;
        if (connection is not null)
        {
            if (connection.SlowSendGap * 2 > min)
                min = connection.SlowSendGap * 2;
            if (connection.SendGap * 4 > min)
                min = connection.SendGap * 4;
        }
        return min;
    }
}

/// <summary>
/// The desktop's Packet Spammer on the headless connection: a list of packets sent in turn, one every interval,
/// until stopped. Limits: the interval is clamped to <see cref="SpammerLimits.MinimumFor"/>, a run sends at most
/// <see cref="SpammerLimits.MaxSendsPerRun"/> (or fewer if asked), and it stops by itself when the account is not
/// online, a packet is dropped, or the server says "slow down" (a warning the game itself treats as too fast).
/// Packets go through the engine's paced send queue like every other packet.
/// </summary>
public sealed class PacketSpammer
{
    /// <summary>Longest packet the spammer sends.</summary>
    public const int MaxPacketLength = 2000;

    private readonly IBotEngine _engine;
    private readonly TimeProvider _time;

    public PacketSpammer(IBotEngine engine, SpammerLimits? limits = null, TimeProvider? time = null)
    {
        _engine = engine;
        Limits = limits ?? SpammerLimits.Default;
        _time = time ?? TimeProvider.System;
    }

    public SpammerLimits Limits { get; }

    /// <summary>The shortest interval allowed now.</summary>
    public TimeSpan MinimumInterval => Limits.MinimumFor(_engine.GetConnectionDetails());

    /// <summary>
    /// Why <paramref name="packet"/> cannot be spammed, or null: it must be one client game packet
    /// (<c>%xt%zm%command%room%...</c>), on one line, at most <see cref="MaxPacketLength"/> characters. SmartFox
    /// system messages (the login handshake is made of them) and JSON are refused.
    /// </summary>
    public static string? Validate(string? packet)
    {
        string p = packet?.Trim() ?? string.Empty;
        if (p.Length == 0)
            return "The packet is empty.";
        if (p.Length > MaxPacketLength)
            return $"The packet is longer than {MaxPacketLength} characters.";
        if (p.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return "A packet is one line.";
        if (!p.StartsWith("%xt%zm%", StringComparison.Ordinal))
            return "Only game packets (%xt%zm%command%room%...) can be sent here.";
        string[] parts = p.Split('%');
        if (parts.Length < 5 || parts[3].Length == 0)
            return "The packet has no command (%xt%zm%command%room%...).";
        if (parts[3] is "login" or "firstJoin")
            return "Login packets are never sent from here.";
        return null;
    }

    /// <summary>The interval actually used for <paramref name="requested"/>.</summary>
    public TimeSpan Clamp(TimeSpan requested)
    {
        TimeSpan min = MinimumInterval;
        return requested < min ? min : requested;
    }

    /// <summary>
    /// Sends <paramref name="packets"/> in turn, one every <paramref name="interval"/> (clamped), until
    /// <paramref name="maxSends"/> (clamped to the limit) were sent or a stop condition is met. Never throws for those:
    /// the result says why it ended.
    /// </summary>
    public async Task<SpamResult> RunAsync(IReadOnlyList<string> packets, TimeSpan interval, int maxSends, IProgress<SpamProgress>? progress, CancellationToken cancellationToken)
    {
        if (packets.Count == 0)
            return new SpamResult(SpamStopReason.Failed, 0, "Add a packet first.");
        foreach (string p in packets)
        {
            if (Validate(p) is string why)
                return new SpamResult(SpamStopReason.Failed, 0, $"{why} ({Short(p)})");
        }
        int max = Math.Clamp(maxSends, 1, Limits.MaxSendsPerRun);
        TimeSpan every = Clamp(interval);
        if (!_engine.Snapshot.IsOnline || _engine.GetConnectionDetails() is not { IsReady: true } start)
            return new SpamResult(SpamStopReason.Disconnected, 0, "Log in first.");
        long warnings = start.SlowDownWarnings;
        object? socket = start.Identity;
        bool dropped = false;
        void OnSnapshot(object? sender, EngineSnapshot s)
        {
            if (!s.IsOnline)
                dropped = true;
        }
        _engine.SnapshotChanged += OnSnapshot;
        _engine.WriteLog(EngineLogLevel.Warning, "spammer", string.Create(CultureInfo.InvariantCulture,
            $"Packet spammer started: {packets.Count} packet(s) every {every.TotalMilliseconds:0} ms, at most {max} sends."));
        int sent = 0;
        try
        {
            while (true)
            {
                for (int i = 0; i < packets.Count; i++)
                {
                    if (Check(warnings, socket, dropped) is SpamResult stop)
                        return stop with { Sent = sent };
                    progress?.Report(new SpamProgress(i, packets.Count, sent, max));
                    bool ok;
                    try
                    {
                        ok = await _engine.SendPacketAsync(packets[i].Trim(), log: false, cancellationToken).ConfigureAwait(false);
                    }
                    catch (EngineException e)
                    {
                        return new SpamResult(SpamStopReason.Disconnected, sent, e.Message);
                    }
                    if (!ok)
                        return new SpamResult(SpamStopReason.Dropped, sent, "A packet was not sent (send queue full or the connection closing): stopped.");
                    sent++;
                    if (sent >= max)
                        return new SpamResult(SpamStopReason.LimitReached, sent, $"Sent {sent} packets, the most for one run.");
                    await Task.Delay(every, _time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SpamResult(SpamStopReason.Stopped, sent, "Stopped.");
        }
        finally
        {
            _engine.SnapshotChanged -= OnSnapshot;
        }
    }

    /// <summary>A reason to stop before the next packet, or null.</summary>
    private SpamResult? Check(long warningsAtStart, object? socket, bool dropped)
    {
        // Any drop during the run ends it, even when a reconnect already brought the account back.
        if (dropped || !_engine.Snapshot.IsOnline || _engine.GetConnectionDetails() is not { IsReady: true } c || !ReferenceEquals(c.Identity, socket))
            return new SpamResult(SpamStopReason.Disconnected, 0, "The connection dropped: stopped.");
        if (c.SlowDownWarnings > warningsAtStart)
            return new SpamResult(SpamStopReason.SlowDown, 0, "The server said to slow down: stopped. Wait before sending more.");
        return null;
    }

    /// <summary>Logs the end of a run in the Console.</summary>
    public void LogEnd(SpamResult r) =>
        _engine.WriteLog(r.Reason is SpamStopReason.Stopped or SpamStopReason.LimitReached ? EngineLogLevel.Info : EngineLogLevel.Warning,
            "spammer", $"Packet spammer ended after {r.Sent} packet(s): {r.Message}");

    private static string Short(string p) => p.Length > 40 ? p[..40] + "..." : p;
}
