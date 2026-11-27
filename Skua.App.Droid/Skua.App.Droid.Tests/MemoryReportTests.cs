using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Memory;
using Skua.App.Droid.Core.ViewModels;

namespace Skua.App.Droid.Tests;

/// <summary>
/// What a memory report says: whether the managed heap or something else fills the RSS, what is swapped out, and
/// how fast the app allocates (the garbage rate that drives the collector).
/// </summary>
public sealed class MemoryReportTests
{
    [Fact]
    public void SmapsRollupIsReadIntoItsParts()
    {
        string file = Path.Combine(Path.GetTempPath(), $"smaps-rollup-{Environment.ProcessId}-{Guid.NewGuid():N}");
        File.WriteAllText(file, """
            12c00000-7ffd5b5f3000 ---p 00000000 00:00 0                              [rollup]
            Rss:              742400 kB
            Pss:              690176 kB
            Pss_Anon:         512000 kB
            Pss_File:         178176 kB
            Pss_Shmem:             0 kB
            Shared_Clean:      60000 kB
            Private_Dirty:    500000 kB
            Anonymous:        512000 kB
            Swap:             204800 kB
            SwapPss:          204800 kB
            """);
        try
        {
            SmapsRollup r = ProcMemoryProbe.ReadRollup(file)!.Value;
            Assert.Equal(742400L * 1024, r.RssBytes);
            Assert.Equal(690176L * 1024, r.PssBytes);
            Assert.Equal(512000L * 1024, r.PssAnonBytes);
            Assert.Equal(178176L * 1024, r.PssFileBytes);
            Assert.Equal(204800L * 1024, r.SwapBytes);
        }
        finally
        {
            File.Delete(file);
        }
        Assert.Null(ProcMemoryProbe.ReadRollup(file));
    }

    [Fact]
    public void TheMemorySectionSplitsTheRss()
    {
        SmapsRollup rollup = new(725L << 20, 700L << 20, 520L << 20, 180L << 20, 200L << 20);
        MemoryReading m = DiagnosticsReport.ReadMemory(MemoryStatus.Unknown with { RssBytes = 725L << 20, PeakRssBytes = 800L << 20 }, 1024, true, "normal",
            rollup: () => rollup);
        m = m with { ManagedBytes = 150L << 20, Gc = new GcSample(150L << 20, 210L << 20, 9L << 30, 100, 5, null, TimeSpan.Zero, null, 4.5 * 1048576) };
        Dictionary<string, string> rows = DiagnosticsReport.MemoryRows(m).ToDictionary(r => r.Label, r => r.Value);

        Assert.Equal("700.0 MB (520.0 MB the app's own memory, 180.0 MB code and mapped files)", rows["PSS"]);
        Assert.StartsWith("200.0 MB (the phone is short of memory", rows["Swapped out"]);
        Assert.StartsWith("150.0 MB in use, 210.0 MB held by the collector", rows["Managed heap"]);
        Assert.StartsWith("515.0 MB of the RSS", rows["Outside the managed heap"]);
        Assert.Equal("4.5 MB/s since the last heartbeat", rows["Allocation rate"]);
    }

    [Fact]
    public void TheHeartbeatLineCarriesTheAllocationRate()
    {
        GcSample g = new(100L << 20, 120L << 20, 1L << 30, 10, 2, null, TimeSpan.FromMilliseconds(30), null, 12.25 * 1048576);
        Assert.Contains("heap 100 MB (committed 120 MB), allocating 12.3 MB/s, GC 10 minor/2 major", g.Describe());
        Assert.DoesNotContain("allocating", (g with { AllocatedPerSecond = -1 }).Describe());

        GcTelemetry.ReadWindow();
        byte[][] garbage = Enumerable.Range(0, 64).Select(_ => new byte[64 * 1024]).ToArray();
        GC.KeepAlive(garbage);
        Assert.True(GcTelemetry.ReadWindow().AllocatedPerSecond > 0);
    }

    [Fact]
    public void TheMemoryLineShowsTheHeapNextToTheRss()
    {
        MemoryStatus m = new(725L << 20, 800L << 20, 150L << 20, 3, DateTimeOffset.UtcNow);
        Assert.Equal("725 MB (heap 150) · 3 script asm", Formatting.Memory(m));
        Assert.Equal("mem n/a", Formatting.Memory(MemoryStatus.Unknown));
    }
}
