namespace Skua.App.Droid.Core.Memory;

/// <summary>What the watchdog wants the host to do.</summary>
public enum MemoryVerdict
{
    /// <summary>Below the threshold.</summary>
    Normal,

    /// <summary>Over the threshold, but a restart must wait (script running, grace period, or auto restart off).</summary>
    RestartPending,

    /// <summary>Over the threshold, no script running, auto restart allowed: restart the process now.</summary>
    RestartNow,

    /// <summary>The process was already over the threshold when it started: the threshold is too low to be useful.</summary>
    ThresholdTooLow,
}

/// <summary>Settings for <see cref="MemoryWatchdog"/>.</summary>
public sealed record MemoryWatchdogOptions
{
    /// <summary>RSS above which a restart is wanted.</summary>
    public long ThresholdBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>Restart automatically (otherwise only offer it).</summary>
    public bool AutoRestart { get; init; } = true;

    /// <summary>How long no script must have been running before an automatic restart (the user may be about to start the next one).</summary>
    public TimeSpan IdleGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>No automatic restart in the first minutes of a process (protects against restart loops).</summary>
    public TimeSpan MinimumUptime { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>No automatic restart sooner than this after the previous automatic one (persisted by the host).</summary>
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Decides when the engine process should be restarted to give back memory that Mono never returns (script
/// AssemblyLoadContexts are not collected on Android). Pure logic with an injected clock; the Android service feeds
/// it samples and performs the restart.
/// </summary>
public sealed class MemoryWatchdog
{
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _processStart;
    private DateTimeOffset? _idleSince;
    private long? _baseline;
    private bool _fired;

    /// <param name="options">Thresholds.</param>
    /// <param name="lastAutoRestartAt">When the host last restarted automatically (persisted across processes), if ever.</param>
    /// <param name="time">Clock.</param>
    public MemoryWatchdog(MemoryWatchdogOptions options, DateTimeOffset? lastAutoRestartAt = null, TimeProvider? time = null)
    {
        Options = options;
        LastAutoRestartAt = lastAutoRestartAt;
        _time = time ?? TimeProvider.System;
        _processStart = _time.GetUtcNow();
    }

    public MemoryWatchdogOptions Options { get; set; }

    public DateTimeOffset? LastAutoRestartAt { get; private set; }

    public MemoryVerdict LastVerdict { get; private set; } = MemoryVerdict.Normal;

    /// <summary>Feeds one sample. <paramref name="scriptBusy"/> is true while a script compiles, runs or stops.</summary>
    public MemoryVerdict Evaluate(long rssBytes, bool scriptBusy)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (rssBytes > 0)
            _baseline ??= rssBytes;

        if (scriptBusy)
            _idleSince = null;
        else
            _idleSince ??= now;

        LastVerdict = Decide(rssBytes, scriptBusy, now);
        return LastVerdict;
    }

    private MemoryVerdict Decide(long rssBytes, bool scriptBusy, DateTimeOffset now)
    {
        if (rssBytes <= 0 || rssBytes < Options.ThresholdBytes)
        {
            _fired = false;
            return MemoryVerdict.Normal;
        }

        // A threshold the process exceeds right after starting would restart it forever.
        if (_baseline is long baseline && baseline >= Options.ThresholdBytes * 9 / 10)
            return MemoryVerdict.ThresholdTooLow;

        if (scriptBusy || !Options.AutoRestart || _fired)
            return MemoryVerdict.RestartPending;
        if (now - _processStart < Options.MinimumUptime)
            return MemoryVerdict.RestartPending;
        if (LastAutoRestartAt is DateTimeOffset last && now - last < Options.MinimumInterval)
            return MemoryVerdict.RestartPending;
        if (_idleSince is not DateTimeOffset idle || now - idle < Options.IdleGrace)
            return MemoryVerdict.RestartPending;

        _fired = true;
        LastAutoRestartAt = now;
        return MemoryVerdict.RestartNow;
    }
}
