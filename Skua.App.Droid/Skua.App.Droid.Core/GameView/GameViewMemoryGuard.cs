using System.Globalization;

namespace Skua.App.Droid.Core.GameView;

/// <summary>What the memory guard says about the game view.</summary>
public enum MemoryGuardVerdict
{
    Ok,

    /// <summary>Over <see cref="GameViewMemoryGuard.WarnFraction"/> of the limit: say so once.</summary>
    Warn,

    /// <summary>Over the limit, or the renderer died: close the view and return to headless.</summary>
    Close,
}

/// <summary>
/// Watches the game view process's PSS (the WebView's GPU side is accounted to it: with the wrong renderer it went from
/// ~600 MB to 2.7 GB within 20 s on the measured phone and the system reached RunningCritical) and the WebView renderer.
/// Over the limit, or once the renderer is gone, the verdict is <see cref="MemoryGuardVerdict.Close"/> and stays so.
/// A single sample over the limit is enough: growth that fast leaves no time for a second look.
/// </summary>
public sealed class GameViewMemoryGuard
{
    public const double WarnFraction = 0.85;

    private readonly object _gate = new();
    private bool _warned;

    public GameViewMemoryGuard(int limitMb)
    {
        LimitMb = Math.Clamp(limitMb, GameViewOptions.MinMemoryLimitMb, GameViewOptions.MaxMemoryLimitMb);
    }

    public int LimitMb { get; }

    /// <summary>Why the view must close (null while it may stay).</summary>
    public string? Reason { get; private set; }

    public bool Tripped => Reason is not null;

    /// <summary>Highest PSS seen (MB).</summary>
    public double PeakMb { get; private set; }

    /// <summary>
    /// One PSS sample of the view process (MB). <see cref="MemoryGuardVerdict.Warn"/> is returned once, the first time
    /// it passes <see cref="WarnFraction"/> of the limit.
    /// </summary>
    public MemoryGuardVerdict Sample(double pssMb)
    {
        lock (_gate)
        {
            if (Reason is not null)
                return MemoryGuardVerdict.Close;
            if (double.IsNaN(pssMb) || pssMb <= 0)
                return MemoryGuardVerdict.Ok;
            PeakMb = Math.Max(PeakMb, pssMb);
            if (pssMb >= LimitMb)
            {
                Reason = string.Create(CultureInfo.InvariantCulture, $"the game view used too much memory ({pssMb:F0} MB, limit {LimitMb} MB)");
                return MemoryGuardVerdict.Close;
            }
            if (!_warned && pssMb >= LimitMb * WarnFraction)
            {
                _warned = true;
                return MemoryGuardVerdict.Warn;
            }
            return MemoryGuardVerdict.Ok;
        }
    }

    /// <summary>The WebView's renderer process died (crashed, or killed by the system, usually for memory).</summary>
    public MemoryGuardVerdict RendererGone(bool crashed)
    {
        lock (_gate)
        {
            Reason ??= crashed ? "the WebView renderer crashed" : "the WebView renderer was killed by the system (likely memory)";
            return MemoryGuardVerdict.Close;
        }
    }

    /// <summary>PSS in MB from /proc/self/smaps_rollup text ("Pss:  123456 kB"); NaN when it is not there.</summary>
    public static double ParseSmapsRollupPss(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (!line.StartsWith("Pss:", StringComparison.Ordinal))
                continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long kb))
                return kb / 1024.0;
        }
        return double.NaN;
    }
}
