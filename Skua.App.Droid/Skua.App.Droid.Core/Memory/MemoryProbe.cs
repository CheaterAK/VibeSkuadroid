using System.Diagnostics;

namespace Skua.App.Droid.Core.Memory;

/// <summary>One memory reading of this process.</summary>
public readonly record struct MemorySample(long RssBytes, long PeakRssBytes, long ManagedBytes);

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

    /// <summary>Parses a "Key:   1234 kB" line into bytes; -1 when malformed.</summary>
    internal static long ParseKb(string line)
    {
        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out long kb) ? kb * 1024 : -1;
    }
}
