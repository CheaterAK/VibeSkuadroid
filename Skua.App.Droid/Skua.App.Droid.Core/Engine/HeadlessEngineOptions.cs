using Skua.App.Droid.Core.Security;
using Skua.Core.Headless;

namespace Skua.App.Droid.Core.Engine;

/// <summary>Settings for <see cref="HeadlessBotEngine"/>.</summary>
public sealed class HeadlessEngineOptions
{
    /// <summary>Creates the protocol session. Tests point it at a fake web API; the app uses the default.</summary>
    public Func<AqwSession> SessionFactory { get; init; } = () => new AqwSession();

    /// <summary>
    /// Where the engine registers the password and login tokens so they are redacted from its log and packets. The
    /// Android app passes the process-wide diagnostics registry, so crash reports are scrubbed of the same values.
    /// Null gives the engine a private one.
    /// </summary>
    public SecretScrubber? Scrubber { get; init; }

    /// <summary>Reconnect on its own after an unexpected drop that the protocol layer says is worth retrying.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Waits between reconnect attempts; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)];

    /// <summary>
    /// Taken before every reconnect attempt and released after it (the army's login stagger, so the light sessions of
    /// one process never log back in at the same moment). Null: no gate.
    /// </summary>
    public Func<CancellationToken, Task<IAsyncDisposable>>? LoginGate { get; init; }

    /// <summary>Give up after this many failed reconnect attempts in a row.</summary>
    public int MaxReconnectAttempts { get; init; } = 30;

    /// <summary>Snapshots are published at most this often.</summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often process memory is sampled into the snapshot.</summary>
    public TimeSpan MemorySampleInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int LogCapacity { get; init; } = 2000;

    public int PacketCapacity { get; init; } = 1500;

    /// <summary>Captured packets longer than this are cut (large JSON replies would otherwise hold tens of MB).</summary>
    public int MaxPacketChars { get; init; } = 8192;

    /// <summary>Upper bound for <see cref="IBotEngine.StopScriptAsync"/> before the engine gives up waiting.</summary>
    public TimeSpan ScriptStopTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
