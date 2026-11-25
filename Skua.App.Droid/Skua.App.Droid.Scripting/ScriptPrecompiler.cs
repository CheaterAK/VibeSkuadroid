using Skua.App.Droid.Core.Army;

namespace Skua.App.Droid.Scripting;

/// <summary>
/// The engine's use of a separate compiler (<see cref="SkuaScriptHostAdapter.Precompiler"/>): before Skua compiles a
/// script in the engine process, the compiler service compiles it into the shared compiled-script cache; Skua then only
/// loads the cached assemblies. What happens when the service cannot be used is decided by
/// <see cref="InProcessRefusal"/>.
/// </summary>
/// <param name="Service">The compiler (the <c>:compiler</c> process on Android).</param>
/// <param name="ReferenceSet">This engine's <see cref="ScriptReferenceSet.Identity"/>, sent with every request.</param>
/// <param name="InProcessRefusal">Asked when the service failed: null lets the engine compile in its own process,
/// anything else is the reason it must not (shown to the user).</param>
public sealed record ScriptPrecompiler(IScriptCompileService Service, string ReferenceSet, Func<string?> InProcessRefusal)
{
    /// <summary>The longest a compile may take before the engine gives up on the service.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>What a CoreBots-sized compile adds to the compiling process at its peak (measured on the S21 Ultra).</summary>
    public static long CompilePeakBytes => MemoryEstimates.Default.FullCompilePeakBytes;

    /// <summary>
    /// The in-process fallback rule: allowed while the device keeps <see cref="MemoryEstimates.ReserveBytes"/> (or twice
    /// Android's low-memory threshold) free after a compile's peak. Unknown memory allows it.
    /// </summary>
    public static string? RefuseInProcessFor(DeviceMemory device)
    {
        if (!device.IsKnown)
            return null;
        long floor = Math.Max(MemoryEstimates.Default.ReserveBytes, device.LowThresholdBytes * 2);
        long left = device.AvailableBytes - CompilePeakBytes;
        return left >= floor
            ? null
            : $"only {device.AvailableBytes >> 20} MB of memory is available and a compile needs about {CompilePeakBytes >> 20} MB with {floor >> 20} MB left free. Close other apps or army slots and start again";
    }
}
