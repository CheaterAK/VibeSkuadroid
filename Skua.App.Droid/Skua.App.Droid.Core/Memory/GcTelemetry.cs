using System.Diagnostics;
using System.Globalization;

namespace Skua.App.Droid.Core.Memory;

/// <summary>One reading of the managed heap and the collector.</summary>
/// <param name="HeapBytes">Managed heap in use (<see cref="GC.GetTotalMemory(bool)"/>, no collection).</param>
/// <param name="CommittedBytes">Memory the collector holds from the system (GC memory info), -1 when not reported.</param>
/// <param name="AllocatedBytes">Allocated since the process started.</param>
/// <param name="Minor">Nursery collections (Mono: gen 0).</param>
/// <param name="Major">Major collections (Mono: gen 1, its oldest generation).</param>
/// <param name="TotalPause">Time all managed threads were paused by the collector, when the runtime reports it (CoreCLR); null on Mono.</param>
/// <param name="MaxStall">The longest stall the stall probe saw since the previous <see cref="GcTelemetry.ReadWindow"/>.</param>
/// <param name="LastLongStall">The last stall of 100 ms or more, its length and whether a major collection happened in it.</param>
/// <param name="AllocatedPerSecond">Bytes allocated per second since the previous <see cref="GcTelemetry.ReadWindow"/>
/// (the garbage rate that drives collections; a script polling the game makes most of it), -1 when not known.</param>
public readonly record struct GcSample(
    long HeapBytes,
    long CommittedBytes,
    long AllocatedBytes,
    int Minor,
    int Major,
    TimeSpan? TotalPause,
    TimeSpan MaxStall,
    GcStall? LastLongStall,
    double AllocatedPerSecond = -1)
{
    /// <summary>One compact line ("heap 512 MB, GC 120 minor/8 major, stall max 1.20 s ...") for heartbeats and logs.</summary>
    public string Describe()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string text = string.Create(c, $"heap {HeapBytes / 1048576.0:0} MB");
        if (CommittedBytes >= 0)
            text += string.Create(c, $" (committed {CommittedBytes / 1048576.0:0} MB)");
        if (AllocatedPerSecond >= 0)
            text += string.Create(c, $", allocating {AllocatedPerSecond / 1048576.0:0.0} MB/s");
        text += string.Create(c, $", GC {Minor} minor/{Major} major");
        if (TotalPause is TimeSpan pause)
            text += string.Create(c, $", paused {pause.TotalSeconds:0.0} s in all");
        text += string.Create(c, $", stall max {MaxStall.TotalSeconds:0.00} s");
        if (LastLongStall is GcStall s)
            text += string.Create(c, $", last long stall {s.Length.TotalSeconds:0.00} s{(s.DuringMajor ? " (major GC)" : s.DuringMinor ? " (minor GC)" : string.Empty)} at {s.At:HH:mm:ss}");
        return text;
    }
}

/// <summary>A stall the probe measured: its end, its length and which collections ran during it.</summary>
public readonly record struct GcStall(DateTimeOffset At, TimeSpan Length, bool DuringMinor, bool DuringMajor);

/// <summary>
/// The managed heap, collection counts and pauses for heartbeats and the Diagnostics page.
/// </summary>
/// <remarks>
/// Mono reports no pause times (<see cref="GC.GetTotalPauseDuration"/> stays zero), so a probe thread measures them
/// from outside: it sleeps <see cref="ProbeInterval"/> and records how much later it woke. A stop-the-world collection
/// stops every managed thread, the probe included, so its oversleep is the pause (plus any CPU starvation, which also
/// freezes the UI). Whether a nursery or a major collection ran during the stall comes from the collection counts.
/// 20 wake-ups a second, no allocation per tick.
/// </remarks>
public static class GcTelemetry
{
    /// <summary>The probe's sleep.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Stalls at least this long are remembered as <see cref="GcSample.LastLongStall"/>.</summary>
    public static readonly TimeSpan LongStall = TimeSpan.FromMilliseconds(100);

    private static readonly object s_gate = new();
    private static Thread? s_probe;
    private static long s_windowMaxTicks;
    private static long s_lastStallTicks;
    private static long s_lastStallAtTicks;
    private static int s_lastStallKind;

    /// <summary>Starts the stall probe (idempotent).</summary>
    public static void Start()
    {
        lock (s_gate)
        {
            if (s_probe is not null)
                return;
            s_probe = new Thread(Probe) { IsBackground = true, Name = "GC stall probe", Priority = ThreadPriority.AboveNormal };
            s_probe.Start();
        }
    }

    /// <summary>Whether the probe runs.</summary>
    public static bool Running => Volatile.Read(ref s_probe) is not null;

    private static void Probe()
    {
        long interval = (long)(ProbeInterval.TotalSeconds * Stopwatch.Frequency);
        long longStall = (long)(LongStall.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            int minor = GC.CollectionCount(0);
            int major = GC.CollectionCount(GC.MaxGeneration);
            long start = Stopwatch.GetTimestamp();
            Thread.Sleep(ProbeInterval);
            long stall = Stopwatch.GetTimestamp() - start - interval;
            if (stall <= 0)
                continue;
            long max = Interlocked.Read(ref s_windowMaxTicks);
            while (stall > max)
            {
                long seen = Interlocked.CompareExchange(ref s_windowMaxTicks, stall, max);
                if (seen == max)
                    break;
                max = seen;
            }
            if (stall >= longStall)
            {
                int kind = (GC.CollectionCount(0) != minor ? 1 : 0) | (GC.CollectionCount(GC.MaxGeneration) != major ? 2 : 0);
                Volatile.Write(ref s_lastStallKind, kind);
                Interlocked.Exchange(ref s_lastStallAtTicks, DateTimeOffset.UtcNow.UtcTicks);
                Interlocked.Exchange(ref s_lastStallTicks, stall);
            }
        }
    }

    // Allocation at the start of the current window (the previous heartbeat), for the allocation rate.
    private static long s_windowAllocated = -1;
    private static long s_windowStartTicks;

    /// <summary>Reads the collector without touching the probe's window (Diagnostics page).</summary>
    public static GcSample Read() => Sample(Interlocked.Read(ref s_windowMaxTicks), newWindow: false);

    /// <summary>Reads the collector and starts a new window for <see cref="GcSample.MaxStall"/> (heartbeat).</summary>
    public static GcSample ReadWindow() => Sample(Interlocked.Exchange(ref s_windowMaxTicks, 0), newWindow: true);

    private static GcSample Sample(long maxTicks, bool newWindow)
    {
        long allocated = GC.GetTotalAllocatedBytes(false);
        long now = Stopwatch.GetTimestamp();
        double rate = -1;
        lock (s_gate)
        {
            if (s_windowAllocated >= 0 && now > s_windowStartTicks)
                rate = Math.Max(0, allocated - s_windowAllocated) / ((now - s_windowStartTicks) / (double)Stopwatch.Frequency);
            if (newWindow || s_windowAllocated < 0)
            {
                s_windowAllocated = allocated;
                s_windowStartTicks = now;
            }
        }
        long committed = -1;
        try
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            if (info.TotalCommittedBytes > 0)
                committed = info.TotalCommittedBytes;
        }
        catch (Exception e) when (e is NotSupportedException or PlatformNotSupportedException or NotImplementedException)
        {
        }
        TimeSpan? pause = null;
        try
        {
            TimeSpan total = GC.GetTotalPauseDuration();
            if (total > TimeSpan.Zero)
                pause = total;
        }
        catch (Exception e) when (e is NotSupportedException or PlatformNotSupportedException or NotImplementedException)
        {
        }
        long lastTicks = Interlocked.Read(ref s_lastStallTicks);
        GcStall? last = null;
        if (lastTicks > 0)
        {
            int kind = Volatile.Read(ref s_lastStallKind);
            last = new GcStall(new DateTimeOffset(Interlocked.Read(ref s_lastStallAtTicks), TimeSpan.Zero), Ticks(lastTicks), (kind & 1) != 0, (kind & 2) != 0);
        }
        return new GcSample(
            GC.GetTotalMemory(false),
            committed,
            allocated,
            GC.CollectionCount(0),
            GC.CollectionCount(GC.MaxGeneration),
            pause,
            Ticks(maxTicks),
            last,
            rate);
    }

    private static TimeSpan Ticks(long stopwatchTicks) => TimeSpan.FromSeconds(stopwatchTicks / (double)Stopwatch.Frequency);
}
