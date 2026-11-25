using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Core.Memory;

namespace Skua.App.Droid.Tests;

public class GcTelemetryTests
{
    [Fact]
    public void TheSampleNamesHeapCollectionsAndStalls()
    {
        GcTelemetry.Start();
        Assert.True(GcTelemetry.Running);
        int major = GC.CollectionCount(GC.MaxGeneration);
        GC.Collect();
        GcSample s = GcTelemetry.ReadWindow();
        Assert.True(s.HeapBytes > 0);
        Assert.True(s.Major > major);
        Assert.True(s.MaxStall >= TimeSpan.Zero);
        string text = s.Describe();
        Assert.Contains("heap ", text);
        Assert.Contains(" minor/", text);
        Assert.Contains("stall max", text);

        GcSample stalled = s with { LastLongStall = new GcStall(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(10.5), false, true) };
        Assert.Contains("last long stall 10.50 s (major GC)", stalled.Describe());
    }

    [Fact]
    public async Task MemoryPressureDropsCachesAndCollectsAtMostOncePerInterval()
    {
        int trims = 0;
        MemoryPressure.Register(() => Interlocked.Increment(ref trims));
        MemoryPressure.Register(() => throw new InvalidOperationException("a cache that cannot be dropped"));
        Assert.Equal("nothing to do", MemoryPressure.OnTrimMemory(MemoryPressure.RunningModerate));
        Assert.Equal("nothing to do", MemoryPressure.OnTrimMemory(MemoryPressure.UiHidden));
        Assert.EndsWith("caches dropped", MemoryPressure.OnTrimMemory(MemoryPressure.RunningLow));
        Assert.True(trims >= 1);
        TaskCompletionSource<string> collected = new();
        string first = MemoryPressure.OnTrimMemory(MemoryPressure.RunningCritical, line => collected.TrySetResult(line));
        string second = MemoryPressure.OnTrimMemory(MemoryPressure.Complete);
        Assert.EndsWith("full collection started", first);
        Assert.EndsWith("collected recently", second);
        Assert.Contains("full collection", await collected.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(trims >= 3);
    }

    [Fact]
    public void TheHeartbeatCarriesTheGcLineThroughTheMarker()
    {
        ProcessActivity a = new(UiState.Foreground, "Running", "Online", "heartbeat", DateTimeOffset.UtcNow, null, 700, "heap 650 MB, GC 120 minor/8 major, stall max 1.20 s");
        Assert.Contains("RSS 700 MB, heap 650 MB, GC 120 minor/8 major, stall max 1.20 s", a.Describe());
        Dictionary<string, string> values = a.ToMarkerLines().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(a.Gc, ProcessActivity.FromMarker(values)!.Gc);
        // The process state summary (127 bytes) does not carry it.
        Assert.Null(ProcessActivity.FromSummary(a.ToSummary())!.Gc);
    }
}
