using System.Globalization;
using System.Reflection;

namespace Skua.App.Droid.Core.Diagnostics;

/// <summary>
/// Names the GC frames of the Mono runtime in a native tombstone and recognises the known Mono GC crashes, so a crash
/// report says what died without the runtime's debug symbols. Also describes the GC mode a process runs with.
/// </summary>
/// <remarks>
/// <para>
/// The runtime pack ships libmonosgen-2.0.so stripped; its GC functions are static. The tables embedded here
/// (<c>monosgen-&lt;BuildId&gt;.sym</c>) map the address ranges of the sgen functions of one exact build, matched
/// against the unstripped objects of libmonosgen-2.0.a from the same runtime pack by
/// <c>build/android/tools/monosgen-symbols.py</c>. A tombstone from another runtime build is left as it is.
/// </para>
/// <para>
/// The crash this was written for (Galaxy S21 Ultra, .NET 10.0.15, 2026-11): SIGSEGV on a thread-pool thread in
/// <c>push_object</c> &lt;- <c>processing_stw_step</c> (sgen-tarjan-bridge.c) &lt;- <c>sgen_bridge_processing_stw_step</c>
/// &lt;- <c>finish_gray_stack</c> &lt;- <c>collect_nursery</c>, a nursery collection started by an array allocation:
/// the Java bridge's graph walk met a reference slot holding garbage (packet text). That is dotnet/runtime#123537
/// (also #123495): the Tarjan bridge, run in a nursery collection, scans major-heap objects and tags their headers
/// while the concurrent major collector may be marking them; fix proposed in dotnet/runtime#137047, not in .NET 10.
/// Its workaround is the non-concurrent major collector (<c>AndroidEnableSGenConcurrent=false</c>, i.e.
/// <c>MONO_GC_PARAMS=major=marksweep</c>), which this app now uses.
/// </para>
/// </remarks>
public static class MonoGcDiagnostics
{
    public const string GcParamsVariable = "MONO_GC_PARAMS";

    public const string RuntimeLibrary = "libmonosgen-2.0.so";

    private static readonly Lazy<Dictionary<string, Range[]>> s_tables = new(LoadTables);

    /// <summary>One sgen function of one runtime build: [Start, End) relative to the library's load address.</summary>
    public readonly record struct Range(ulong Start, ulong End, string Function, string Source);

    /// <summary>The Mono GC settings of this process, as the runtime read them at start.</summary>
    public static string? CurrentGcParams => Environment.GetEnvironmentVariable(GcParamsVariable);

    /// <summary>"concurrent" when the major collector runs concurrently with the app (the Android default), else "serial".</summary>
    public static string DescribeGcMode(string? gcParams)
    {
        if (string.IsNullOrWhiteSpace(gcParams))
            return "MONO_GC_PARAMS not set (runtime default major collector)";
        string major = gcParams.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.StartsWith("major=", StringComparison.Ordinal))?["major=".Length..] ?? "default";
        string mode = major switch
        {
            "marksweep-conc" or "marksweep-conc-par" => "concurrent major GC: exposed to the Mono bridge crash dotnet/runtime#123537",
            "marksweep" or "marksweep-par" => "serial major GC (workaround for dotnet/runtime#123537)",
            _ => "major collector " + major,
        };
        return $"{GcParamsVariable}={gcParams} ({mode})";
    }

    /// <summary>The function holding <paramref name="relPc"/>, e.g. "push_object+0x10 (sgen-tarjan-bridge.c)", when known.</summary>
    public static string? Symbolize(string? file, string? buildId, ulong relPc)
    {
        return Find(file, buildId, relPc) is { } r
            ? string.Create(CultureInfo.InvariantCulture, $"{r.Function}+0x{relPc - r.Start:x} ({r.Source})")
            : null;
    }

    /// <summary>The known sgen function at <paramref name="relPc"/> of the given library build, if any.</summary>
    public static Range? Find(string? file, string? buildId, ulong relPc)
    {
        if (file is null || buildId is null || !file.EndsWith(RuntimeLibrary, StringComparison.Ordinal))
            return null;
        if (!s_tables.Value.TryGetValue(buildId.ToLowerInvariant(), out Range[]? ranges))
            return null;
        int lo = 0, hi = ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (relPc < ranges[mid].Start)
                hi = mid - 1;
            else if (relPc >= ranges[mid].End)
                lo = mid + 1;
            else
                return ranges[mid];
        }
        return null;
    }

    /// <summary>Whether a symbol table for this libmonosgen build is embedded.</summary>
    public static bool HasTable(string buildId) => s_tables.Value.ContainsKey(buildId.ToLowerInvariant());

    /// <summary>
    /// What a native crash in the Mono GC means, from the crashing thread's frames (file, build id, relative pc), or
    /// null when they show none of the known Mono GC crashes.
    /// </summary>
    public static string? Diagnose(IReadOnlyList<(string? File, string? BuildId, ulong RelPc)> frames, string? threadName, string? gcParams)
    {
        List<Range> known = frames.Select(f => Find(f.File, f.BuildId, f.RelPc)).OfType<Range>().ToList();
        bool inRuntime = frames.Take(3).Any(f => f.File?.EndsWith(RuntimeLibrary, StringComparison.Ordinal) == true);
        string mode = "This process runs with " + DescribeGcMode(gcParams) + ".";
        if (known.Any(r => r.Source.StartsWith("sgen-tarjan-bridge", StringComparison.Ordinal) || r.Source.StartsWith("sgen-bridge", StringComparison.Ordinal)))
        {
            string where = known.Any(r => r.Function == "collect_nursery") ? "a nursery collection" : known.Any(r => r.Function.StartsWith("major_", StringComparison.Ordinal)) ? "a major collection" : "a collection";
            return "Crash inside the Mono GC's Java bridge (Tarjan) during " + where + ": it followed an object reference "
                + "holding garbage. This matches the known .NET 10 Mono bug dotnet/runtime#123537 (also #123495): bridge "
                + "processing in nursery collections walks and tags major-heap objects while the concurrent major collector "
                + "may be marking them, and managed memory ends up corrupted (fix proposed in dotnet/runtime#137047, not in "
                + ".NET 10). Workaround: the serial major collector (MONO_GC_PARAMS=major=marksweep). The bytes at the fault "
                + "address are whatever the heap held there (here usually game packet text), not a write by the app. " + mode;
        }
        if (known.Any(r => r.Function.Contains("concurrent", StringComparison.Ordinal) && r.Source.StartsWith("sgen-", StringComparison.Ordinal))
            || (inRuntime && threadName == "SGen worker"))
        {
            return "Crash in the Mono GC's concurrent marking (SGen worker): the same family of heap corruption as "
                + "dotnet/runtime#123537 (concurrent major GC with the Java bridge). Workaround: the serial major collector "
                + "(MONO_GC_PARAMS=major=marksweep). " + mode;
        }
        if (known.Count > 0)
            return "Crash inside the Mono garbage collector (" + known[0].Function + "): usually a corrupted managed heap. " + mode;
        if (inRuntime)
            return "Crash inside the Mono runtime (" + RuntimeLibrary + "); no symbol table is embedded for this runtime build. " + mode;
        return null;
    }

    private static Dictionary<string, Range[]> LoadTables()
    {
        Dictionary<string, Range[]> tables = new(StringComparer.Ordinal);
        Assembly asm = typeof(MonoGcDiagnostics).Assembly;
        foreach (string name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sym", StringComparison.Ordinal)))
        {
            try
            {
                using Stream? s = asm.GetManifestResourceStream(name);
                if (s is null)
                    continue;
                using StreamReader reader = new(s);
                string? buildId = reader.ReadLine()?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(buildId))
                    continue;
                List<Range> ranges = new();
                for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
                {
                    string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 3
                        && ulong.TryParse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong start)
                        && ulong.TryParse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong end)
                        && end > start)
                        ranges.Add(new Range(start, end, p[2], p.Length > 3 ? p[3] : "?"));
                }
                tables[buildId] = ranges.OrderBy(r => r.Start).ToArray();
            }
            catch (IOException)
            {
            }
        }
        return tables;
    }
}
