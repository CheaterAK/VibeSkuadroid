namespace Skua.App.Droid.Core.Army;

/// <summary>The device's memory as Android reports it (<c>ActivityManager.MemoryInfo</c>).</summary>
/// <param name="TotalBytes">totalMem.</param>
/// <param name="AvailableBytes">availMem: what can be used before Android starts killing background processes.</param>
/// <param name="LowThresholdBytes">threshold: below this availMem Android considers memory low.</param>
public sealed record DeviceMemory(long TotalBytes, long AvailableBytes, long LowThresholdBytes)
{
    public static DeviceMemory Unknown { get; } = new(-1, -1, -1);

    public bool IsKnown => AvailableBytes > 0;
}

/// <summary>What one more slot costs, by kind.</summary>
public sealed record MemoryEstimates
{
    /// <summary>A full slot process at rest: 300-450 MB RSS measured on a Galaxy S21 Ultra; the high end is used.</summary>
    public long FullIdleBytes { get; init; } = 450L << 20;

    /// <summary>A CoreBots compile adds about 430 MB at its peak (Roslyn), also measured on the S21 Ultra.</summary>
    public long FullCompilePeakBytes { get; init; } = 430L << 20;

    /// <summary>
    /// A light session: 0.25 MB of managed heap measured per online session in the tests (64 KB socket buffer, world
    /// model, client rules, a 300-line log), against a small fake world. A real character (full inventory, crowded
    /// maps) holds several times that, plus a thread-pool share; 8 MB is the deliberately high figure admitted.
    /// </summary>
    public long LightBytes { get; init; } = 8L << 20;

    /// <summary>Left free for the UI, the system and other apps, on top of Android's own low-memory threshold.</summary>
    public long ReserveBytes { get; init; } = 512L << 20;

    public static MemoryEstimates Default { get; } = new();
}

/// <summary>Whether one more slot may start, and why not.</summary>
public sealed record Admission(bool Allowed, string Reason)
{
    public static Admission Ok { get; } = new(true, string.Empty);
}

/// <summary>
/// Refuses to start a slot the device cannot hold: Android kills background processes (the slots among them) when
/// available memory runs low, so starting one too many would cost the ones already running.
/// </summary>
public static class MemoryAdmission
{
    /// <param name="full">A full slot (else a light session).</param>
    /// <param name="device">The device now.</param>
    /// <param name="estimates">Costs.</param>
    /// <param name="fullRunning">Full slots running now (the main process's engine counts when it is in use).</param>
    /// <param name="maxFull">The configured maximum of full slots.</param>
    /// <param name="pendingBytes">Estimated cost of slots started moments ago that <paramref name="device"/> does not show yet.</param>
    public static Admission Check(bool full, DeviceMemory device, MemoryEstimates estimates, int fullRunning, int maxFull, long pendingBytes = 0)
    {
        if (full && fullRunning >= maxFull)
            return new(false, $"All {maxFull} full slots are in use. Use a light account, or raise the number of full slots in Army settings.");
        if (!device.IsKnown)
            return Admission.Ok;
        long need = full ? estimates.FullIdleBytes + estimates.FullCompilePeakBytes : estimates.LightBytes;
        long floor = Math.Max(estimates.ReserveBytes, device.LowThresholdBytes * 2);
        long left = device.AvailableBytes - pendingBytes - need;
        if (left >= floor)
            return Admission.Ok;
        string kind = full ? "A full slot" : "A light account";
        return new(false, $"Not enough memory: {Mb(device.AvailableBytes - pendingBytes)} available, {kind.ToLowerInvariant()} needs about {Mb(need)} and {Mb(floor)} must stay free.");
    }

    /// <summary>How many more of a kind fit now (for the Army page's hint).</summary>
    public static int HowManyMore(bool full, DeviceMemory device, MemoryEstimates estimates, int fullRunning, int maxFull)
    {
        if (!device.IsKnown)
            return full ? Math.Max(0, maxFull - fullRunning) : int.MaxValue;
        long need = full ? estimates.FullIdleBytes + estimates.FullCompilePeakBytes : estimates.LightBytes;
        long floor = Math.Max(estimates.ReserveBytes, device.LowThresholdBytes * 2);
        long room = Math.Max(0, device.AvailableBytes - floor);
        int byMemory = (int)Math.Min(int.MaxValue, room / Math.Max(1, need));
        return full ? Math.Min(byMemory, Math.Max(0, maxFull - fullRunning)) : byMemory;
    }

    public static string Mb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20} MB";
}
