using System.Diagnostics;

namespace Spike.Core;

public readonly record struct MemSnapshot(long ManagedBytes, long GcHeapBytes, long RssKb, long HwmKb, long PssKb)
{
    public override string ToString() =>
        $"managed={Mb(ManagedBytes)} heapAtLastGC={Mb(GcHeapBytes)} rss={KbMb(RssKb)} peakRss(VmHWM)={KbMb(HwmKb)} pss={KbMb(PssKb)}";

    public static string Mb(long bytes) => bytes < 0 ? "n/a" : $"{bytes / 1048576.0:F1}MB";
    public static string KbMb(long kb) => kb < 0 ? "n/a" : $"{kb / 1024.0:F1}MB";
    public static string DeltaMb(long bytes) => $"{(bytes >= 0 ? "+" : "")}{bytes / 1048576.0:F1}MB";
    public static string DeltaKbMb(long kb) => $"{(kb >= 0 ? "+" : "")}{kb / 1024.0:F1}MB";
}

public sealed class MemoryProbe(ISpikePlatform platform)
{
    public MemSnapshot Take()
    {
        var (rss, hwm) = ReadStatus();
        long pss = -1;
        try { pss = platform.PlatformPssKb() ?? -1; } catch { }
        if (pss < 0) pss = ReadSmapsRollupPss();
        long gcHeap = -1;
        try { gcHeap = GC.GetGCMemoryInfo().HeapSizeBytes; } catch { }
        return new MemSnapshot(GC.GetTotalMemory(false), gcHeap, rss, hwm, pss);
    }

    /// <summary>Cheap sample for the peak sampler (no PSS: smaps_rollup is expensive).</summary>
    public static (long managed, long rssKb) Quick() => (GC.GetTotalMemory(false), ReadStatus().rss);

    static (long rss, long hwm) ReadStatus()
    {
        long rss = -1, hwm = -1;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmRSS:")) rss = ParseKb(line);
                else if (line.StartsWith("VmHWM:")) hwm = ParseKb(line);
            }
        }
        catch { }
        if (rss < 0)
        {
            try { rss = Process.GetCurrentProcess().WorkingSet64 / 1024; } catch { }
        }
        return (rss, hwm);
    }

    static long ReadSmapsRollupPss()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/smaps_rollup"))
                if (line.StartsWith("Pss:")) return ParseKb(line);
        }
        catch { }
        return -1;
    }

    static long ParseKb(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var v) ? v : -1;
    }
}

/// <summary>Background sampler tracking peak managed memory and RSS while an operation runs.</summary>
public sealed class PeakSampler : IDisposable
{
    readonly Thread _thread;
    volatile bool _stop;
    public long PeakManaged { get; private set; }
    public long PeakRssKb { get; private set; }

    public PeakSampler(int intervalMs = 20)
    {
        _thread = new Thread(() =>
        {
            while (!_stop)
            {
                var (m, r) = MemoryProbe.Quick();
                if (m > PeakManaged) PeakManaged = m;
                if (r > PeakRssKb) PeakRssKb = r;
                Thread.Sleep(intervalMs);
            }
        }) { IsBackground = true, Name = "spike-peak-sampler" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
        var (m, r) = MemoryProbe.Quick();
        if (m > PeakManaged) PeakManaged = m;
        if (r > PeakRssKb) PeakRssKb = r;
    }

    public override string ToString() => $"peak managed={MemSnapshot.Mb(PeakManaged)} peak rss={MemSnapshot.KbMb(PeakRssKb)} (sampled every 20ms)";
}
