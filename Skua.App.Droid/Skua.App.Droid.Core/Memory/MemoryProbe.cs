using System.Diagnostics;

namespace Skua.App.Droid.Core.Memory;

/// <summary>One memory reading of this process.</summary>
public readonly record struct MemorySample(long RssBytes, long PeakRssBytes, long ManagedBytes);

/// <summary>
/// <c>/proc/self/smaps_rollup</c>: where the resident memory is. PSS split into anonymous memory (the managed heap,
/// the runtime's and native libraries' own allocations: what the app itself holds) and file-backed pages (code,
/// mapped assemblies and resources, which Android can drop and reread), and what was swapped out (zram on most
/// phones: a process with much swapped out makes the whole phone slow, because every touch of those pages
/// decompresses them). -1 for a value the kernel did not report.
/// </summary>
public readonly record struct SmapsRollup(long RssBytes, long PssBytes, long PssAnonBytes, long PssFileBytes, long SwapBytes);

/// <summary>Reads this process's memory use.</summary>
public interface IMemoryProbe
{
    MemorySample Read();
}

/// <summary>
/// Reads <c>/proc/self/status</c> (VmRSS, VmHWM), which exists on Linux and Android and costs microseconds, and the
/// GC heap size. Falls back to <see cref="Process.WorkingSet64"/> where /proc is missing.
/// </summary>
public sealed class ProcMemoryProbe : IMemoryProbe
{
    private readonly string _statusPath;

    public ProcMemoryProbe(string statusPath = "/proc/self/status")
    {
        _statusPath = statusPath;
    }

    public MemorySample Read()
    {
        long rss = -1, hwm = -1;
        try
        {
            foreach (string line in File.ReadLines(_statusPath))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    rss = ParseKb(line);
                else if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
                    hwm = ParseKb(line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        if (rss < 0)
        {
            try
            {
                using Process self = Process.GetCurrentProcess();
                rss = self.WorkingSet64;
                hwm = self.PeakWorkingSet64;
            }
            catch (Exception e) when (e is PlatformNotSupportedException or InvalidOperationException)
            {
            }
        }

        return new MemorySample(rss, hwm, GC.GetTotalMemory(false));
    }

    /// <summary>
    /// Proportional set size (shared pages divided among the processes sharing them; what Android's memory screens
    /// show) from <c>/proc/self/smaps_rollup</c> (Linux 4.14+, every supported Android). -1 when it cannot be read.
    /// It walks the page tables (a few milliseconds): read it on demand, not on a timer.
    /// </summary>
    public static long ReadPssBytes(string path = "/proc/self/smaps_rollup")
    {
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith("Pss:", StringComparison.Ordinal))
                    return ParseKb(line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return -1;
    }

    /// <summary>
    /// <see cref="SmapsRollup"/> from <c>/proc/self/smaps_rollup</c>, or null when it cannot be read. Like
    /// <see cref="ReadPssBytes"/> it walks the page tables: on demand or every heartbeat, not on a fast timer.
    /// </summary>
    public static SmapsRollup? ReadRollup(string path = "/proc/self/smaps_rollup")
    {
        try
        {
            long rss = -1, pss = -1, anon = -1, file = -1, swap = -1;
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith("Rss:", StringComparison.Ordinal))
                    rss = ParseKb(line);
                else if (line.StartsWith("Pss:", StringComparison.Ordinal))
                    pss = ParseKb(line);
                else if (line.StartsWith("Pss_Anon:", StringComparison.Ordinal))
                    anon = ParseKb(line);
                else if (line.StartsWith("Pss_File:", StringComparison.Ordinal))
                    file = ParseKb(line);
                else if (line.StartsWith("Swap:", StringComparison.Ordinal))
                    swap = ParseKb(line);
            }
            return pss < 0 && rss < 0 ? null : new SmapsRollup(rss, pss, anon, file, swap);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parses a "Key:   1234 kB" line into bytes; -1 when malformed.</summary>
    internal static long ParseKb(string line)
    {
        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out long kb) ? kb * 1024 : -1;
    }
}
