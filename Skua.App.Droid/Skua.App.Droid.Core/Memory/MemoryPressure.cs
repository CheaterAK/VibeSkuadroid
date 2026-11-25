using System.Diagnostics;
using System.Globalization;

namespace Skua.App.Droid.Core.Memory;

/// <summary>
/// What the process does when Android reports memory pressure (<c>onTrimMemory</c>): drop the caches that can be
/// rebuilt (the bank's JSON text and parsed list, the bank rows), and at the critical levels run one full collection on
/// a worker thread, at a moment the app chooses, instead of leaving it to the next allocation (which may be the UI
/// thread's, under the same pressure, with pages in zram).
/// </summary>
public static class MemoryPressure
{
    /// <summary>Android's ComponentCallbacks2 levels.</summary>
    public const int RunningModerate = 5, RunningLow = 10, RunningCritical = 15, UiHidden = 20, Background = 40, Moderate = 60, Complete = 80;

    /// <summary>The shortest time between two collections this class starts.</summary>
    public static readonly TimeSpan CollectInterval = TimeSpan.FromSeconds(60);

    private static readonly List<Action> s_trims = new();
    private static long s_lastCollectTicks = long.MinValue / 2;
    private static int s_collecting;

    /// <summary>Registers a cache to drop under pressure (it must be cheap and safe on any thread).</summary>
    public static void Register(Action trim)
    {
        lock (s_trims)
            s_trims.Add(trim);
    }

    /// <summary>Drops the registered caches (no collection).</summary>
    public static int Trim()
    {
        Action[] trims;
        lock (s_trims)
            trims = s_trims.ToArray();
        int done = 0;
        foreach (Action trim in trims)
        {
            try
            {
                trim();
                done++;
            }
            catch (Exception)
            {
                // A cache that cannot be dropped stays; the others are.
            }
        }
        return done;
    }

    /// <summary>
    /// Handles one <c>onTrimMemory</c> level: from <see cref="RunningLow"/> the caches are dropped; at
    /// <see cref="RunningCritical"/> and from <see cref="Background"/> a full collection follows on a worker thread
    /// (at most every <see cref="CollectInterval"/>). <paramref name="report"/> gets one line when the collection ends.
    /// </summary>
    /// <returns>What was started, for the events log.</returns>
    public static string OnTrimMemory(int level, Action<string>? report = null)
    {
        if (level < RunningLow || level == UiHidden)
            return "nothing to do";
        int trimmed = Trim();
        if (level is not (RunningCritical or >= Background))
            return $"{trimmed} caches dropped";
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref s_lastCollectTicks), now) < CollectInterval || Interlocked.CompareExchange(ref s_collecting, 1, 0) != 0)
            return $"{trimmed} caches dropped; collected recently";
        Interlocked.Exchange(ref s_lastCollectTicks, now);
        Task.Run(() =>
        {
            try
            {
                long before = GC.GetTotalMemory(false);
                Stopwatch sw = Stopwatch.StartNew();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                sw.Stop();
                report?.Invoke(string.Create(CultureInfo.InvariantCulture,
                    $"memory pressure {level}: full collection {sw.Elapsed.TotalMilliseconds:0} ms, heap {before >> 20} -> {GC.GetTotalMemory(false) >> 20} MB"));
            }
            finally
            {
                Volatile.Write(ref s_collecting, 0);
            }
        });
        return $"{trimmed} caches dropped; full collection started";
    }
}
