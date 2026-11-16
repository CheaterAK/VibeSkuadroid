using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Spike.HostApi;

namespace Spike.Core;

public sealed record SpikeOptions
{
    public int BackgroundSeconds { get; init; } = 30;
    public int CollectibleIterations { get; init; } = 10;
    public int BigCollectibleIterations { get; init; } = 5;
    /// <summary>Managed thread id of the UI/main thread, to prove steps run elsewhere.</summary>
    public int UiThreadId { get; init; } = -1;
}

/// <summary>Runs every spike step; each step catches and prints its own failure and the run continues.</summary>
public sealed class SpikeRunner(ISpikePlatform platform, Action<string> sink, SpikeOptions? options = null)
{
    /// <summary>Checksum of the synthetic CoreBots run as produced by the Linux baseline (CoreCLR x64).</summary>
    public const string ExpectedBigChecksum = "DFB06E97-7F895EFC-154";

    readonly SpikeOptions _opt = options ?? new SpikeOptions();
    readonly MemoryProbe _mem = new(platform);
    readonly List<(string Step, bool Ok, double Ms, string Note)> _summary = [];
    readonly Stopwatch _total = Stopwatch.StartNew();

    List<MetadataReference>? _assetRefs, _basicRefs, _frameworkRefs;
    MetadataReference? _hostApiRef;
    string _frameworkRefsSource = "none";
    byte[]? _bigImage;
    List<(string Path, string Source)>? _bigSources;

    void Log(string s) => sink(s);

    public void RunAll()
    {
        Log($"=== VibeSkua Android spike — {platform.Name} — {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz} ===");
        Log($"runner thread={Environment.CurrentManagedThreadId} uiThread={_opt.UiThreadId} background={Thread.CurrentThread.IsBackground}");
        Step("1. Environment", Step1Environment);
        Step("2a. Roslyn compile, refs from shipped assets (streams) [first Roslyn use: includes JIT warm-up]", Step2aAssets);
        Step("2b. Roslyn compile, refs from Basic.Reference.Assemblies.Net100", Step2bBasic);
        Step("3. Script calling host API (Spike.HostApi)", Step3HostApi);
        Step("4. Large CoreBots-sized compile (cold, then warm)", Step4Big);
        Step("5. Disk cache: load emitted assembly vs recompile", Step5DiskCache);
        Step("6. Collectible AssemblyLoadContext load/run/unload", Step6Collectible);
        Step("7. Background-thread script loop", Step7Background);

        Log("");
        Log("=== SUMMARY ===");
        foreach (var (step, ok, ms, note) in _summary)
            Log($"{(ok ? "PASS" : "FAIL")}  {step}  ({ms:F0}ms){(note.Length > 0 ? "  — " + note : "")}");
        Log($"framework references used for steps 3-7: {_frameworkRefsSource}");
        Log($"final memory: {_mem.Take()}");
        Log($"total time {_total.Elapsed.TotalSeconds:F1}s");
        Log("=== END ===");
    }

    string _note = "";

    void Step(string name, Action body)
    {
        Log("");
        Log($"--- {name} ---");
        Log($"  mem before: {_mem.Take()}");
        _note = "";
        var sw = Stopwatch.StartNew();
        bool ok;
        try
        {
            body();
            ok = true;
        }
        catch (Exception ex)
        {
            ok = false;
            if (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
            Log($"  !!! EXCEPTION {ex.GetType().FullName}: {ex.Message}");
            foreach (var line in ex.ToString().Split('\n'))
                Log("      " + line.TrimEnd());
            if (_note.Length == 0) _note = ex.GetType().Name + ": " + ex.Message;
        }
        sw.Stop();
        Log($"  mem after:  {_mem.Take()}");
        Log($"  step time: {sw.Elapsed.TotalMilliseconds:F0}ms -> {(ok ? "PASS" : "FAIL")}");
        _summary.Add((name, ok, sw.Elapsed.TotalMilliseconds, _note));
    }

    static void Check(bool cond, string what)
    {
        if (!cond) throw new InvalidOperationException("Check failed: " + what);
    }

    // ---------------------------------------------------------------- 1
    void Step1Environment()
    {
        bool isMono = Type.GetType("Mono.RuntimeStructs") != null;
        Log($"  runtime: {(isMono ? "Mono" : "CoreCLR (or other non-Mono)")}");
        Log($"  FrameworkDescription: {RuntimeInformation.FrameworkDescription}");
        Log($"  Environment.Version: {Environment.Version}");
        Log($"  RuntimeIdentifier: {RuntimeInformation.RuntimeIdentifier}");
        Log($"  OSDescription: {RuntimeInformation.OSDescription}");
        Log($"  ProcessArchitecture: {RuntimeInformation.ProcessArchitecture} OSArchitecture: {RuntimeInformation.OSArchitecture}");
        Log($"  RuntimeFeature.IsDynamicCodeSupported: {RuntimeFeature.IsDynamicCodeSupported}");
        Log($"  RuntimeFeature.IsDynamicCodeCompiled: {RuntimeFeature.IsDynamicCodeCompiled}");
        Log($"  typeof(object).Assembly.Location: '{typeof(object).Assembly.Location}'");
        Log($"  typeof(IScriptHost).Assembly.Location: '{typeof(IScriptHost).Assembly.Location}'");
        Log($"  Roslyn: {typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.GetName().Version} location='{typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.Location}'");
        Log($"  ProcessorCount: {Environment.ProcessorCount}  ServerGC: {System.Runtime.GCSettings.IsServerGC}  GCLatency: {System.Runtime.GCSettings.LatencyMode}");
        Log($"  GC total available memory: {MemSnapshot.Mb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)}");
        Log($"  AppContext.BaseDirectory: '{AppContext.BaseDirectory}'");
        Log($"  cache dir: {platform.CacheDirectory}");
        foreach (var switchName in new[] { "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", "System.Resources.UseSystemResourceKeys", "System.Reflection.NullabilityInfoContext.IsSupported", "System.Globalization.Invariant" })
            Log($"  AppContext switch {switchName}: {(AppContext.TryGetSwitch(switchName, out var v) ? v.ToString() : "(unset)")}");
        foreach (var kv in platform.EnvironmentFacts())
            Log($"  {kv.Key}: {kv.Value}");

        // Is Reflection.Emit usable? (Roslyn does not need it, but some script libraries might.)
        try
        {
            var dm = new DynamicMethod("spike_add", typeof(int), [typeof(int), typeof(int)]);
            var il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Add);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            var add = (Func<int, int, int>)dm.CreateDelegate(typeof(Func<int, int, int>));
            Log($"  Reflection.Emit DynamicMethod works: 2+3={add(2, 3)}");
        }
        catch (Exception ex)
        {
            Log($"  Reflection.Emit DynamicMethod failed: {ex.GetType().FullName}: {ex.Message}");
        }

        // Is the in-memory metadata of loaded assemblies accessible? (lets us skip shipping reference DLLs for app assemblies)
        unsafe
        {
            bool raw = System.Reflection.Metadata.AssemblyExtensions.TryGetRawMetadata(typeof(IScriptHost).Assembly, out byte* blob, out int len);
            Log($"  Assembly.TryGetRawMetadata(Spike.HostApi): {raw} ({len} bytes)");
        }
    }

    // ---------------------------------------------------------------- 2
    void Step2aAssets()
    {
        var sw = Stopwatch.StartNew();
        var images = platform.FrameworkReferenceImages();
        var refs = new List<MetadataReference>(images.Count);
        long bytes = 0;
        foreach (var (name, open) in images)
        {
            using var s = open();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            bytes += ms.Length;
            refs.Add(MetadataReference.CreateFromStream(ms, filePath: name));
        }
        Log($"  loaded {refs.Count} reference assemblies from shipped files/assets ({bytes / 1048576.0:F1}MB) in {sw.Elapsed.TotalMilliseconds:F0}ms");
        Check(refs.Count > 0, "at least one reference image shipped");
        CompileAndRunBasic(refs, "SpikeBasicAssets");
        _assetRefs = refs;
        _frameworkRefs ??= refs;
        if (ReferenceEquals(_frameworkRefs, refs)) _frameworkRefsSource = "shipped assets";
    }

    void Step2bBasic()
    {
        var sw = Stopwatch.StartNew();
        var refs = Basic.Reference.Assemblies.Net100.References.All.Cast<MetadataReference>().ToList();
        Log($"  materialised {refs.Count} references from Basic.Reference.Assemblies.Net100 in {sw.Elapsed.TotalMilliseconds:F0}ms");
        CompileAndRunBasic(refs, "SpikeBasicPkg");
        _basicRefs = refs;
        if (_frameworkRefs == null)
        {
            _frameworkRefs = refs;
            _frameworkRefsSource = "Basic.Reference.Assemblies.Net100 (assets failed)";
        }
    }

    void CompileAndRunBasic(List<MetadataReference> refs, string asmName)
    {
        var r = ScriptCompiler.Compile(asmName, [("Basic.cs", Scripts.Basic)], refs);
        Log($"  compile: {r.Timings}");
        if (!r.Success) { Log(ScriptCompiler.FormatErrors(r)); throw new InvalidOperationException($"compile failed with {r.Errors.Count} errors"); }
        var sw = Stopwatch.StartNew();
        var asm = Assembly.Load(r.Image!);
        double load = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var result = ScriptCompiler.InvokeScriptMain(asm);
        Log($"  Assembly.Load={load:F1}ms  ScriptMain (first call incl. JIT)={sw.Elapsed.TotalMilliseconds:F1}ms");
        Log($"  ScriptMain returned: {result}");
        _note = r.Timings;
    }

    List<MetadataReference> FrameworkRefs() => _frameworkRefs ?? throw new InvalidOperationException("no framework references available (steps 2a and 2b both failed)");

    // ---------------------------------------------------------------- 3
    unsafe void Step3HostApi()
    {
        var hostAsm = typeof(IScriptHost).Assembly;
        var candidates = new List<(string How, Func<MetadataReference> Make)>
        {
            ("Assembly.TryGetRawMetadata on the loaded Spike.HostApi (no file shipped)", () =>
            {
                if (!System.Reflection.Metadata.AssemblyExtensions.TryGetRawMetadata(hostAsm, out byte* blob, out int len)) throw new NotSupportedException("TryGetRawMetadata returned false");
                var module = ModuleMetadata.CreateFromMetadata((IntPtr)blob, len);
                return AssemblyMetadata.Create(module).GetReference(display: "Spike.HostApi (raw metadata)");
            }),
            ("shipped Spike.HostApi.dll image (asset/file stream)", () =>
            {
                using var s = platform.OpenHostApiReferenceImage() ?? throw new FileNotFoundException("platform ships no Spike.HostApi image");
                var ms = new MemoryStream(); s.CopyTo(ms); ms.Position = 0;
                return MetadataReference.CreateFromStream(ms, filePath: "Spike.HostApi.dll");
            }),
        };

        var okWays = new List<string>();
        foreach (var (how, make) in candidates)
        {
            Log($"  > host API reference via {how}");
            try
            {
                var hostRef = make();
                var r = ScriptCompiler.Compile("SpikeHostApi_" + okWays.Count + "_" + Guid.NewGuid().ToString("N")[..6], [("HostApi.cs", Scripts.HostApi)], [.. FrameworkRefs(), hostRef]);
                Log($"    compile: {r.Timings}");
                if (!r.Success) { Log(ScriptCompiler.FormatErrors(r)); throw new InvalidOperationException("compile failed"); }
                var asm = Assembly.Load(r.Image!);
                var host = new SpikeHost(Log);
                var sw = Stopwatch.StartNew();
                ScriptCompiler.InvokeScriptMain(asm, host);
                Log($"    ScriptMain(host) ran in {sw.Elapsed.TotalMilliseconds:F1}ms; log calls={host.LogCalls}");
                Check(host.Get("answer", 0) == 42, "script stored answer=42 via host.Set");
                Check(host.Get("events", 0) == 1, "script received exactly one host event");
                Check(host.Get("weapons", -1) == host.Inventory.Where(i => i.Category == ItemCategory.Weapon).Sum(i => i.Quantity), "script computed inventory sum");
                var pluginType = asm.GetTypes().First(t => t.Name == "Script");
                var plugin = (IScriptPlugin)Activator.CreateInstance(pluginType)!;
                int x = plugin.Execute(host, 21);
                Check(x == 21 * 2 + 1337, "host called back into script via IScriptPlugin");
                Log($"    host -> script via IScriptPlugin '{plugin.Name}': Execute(21)={x}; type identity of IScriptPlugin shared: {typeof(IScriptPlugin).IsAssignableFrom(pluginType)}");
                Log("    OK");
                okWays.Add(how);
                _hostApiRef ??= hostRef;
            }
            catch (Exception ex)
            {
                if (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
                Log($"    FAILED {ex.GetType().FullName}: {ex.Message}");
                foreach (var line in ex.ToString().Split('\n').Take(25)) Log("      " + line.TrimEnd());
            }
        }
        Check(_hostApiRef != null, "at least one way of referencing the host API works");
        _note = $"{okWays.Count}/{candidates.Count} reference methods OK";
    }

    MetadataReference HostRef() => _hostApiRef ?? throw new InvalidOperationException("no host API reference (step 3 failed)");

    // ---------------------------------------------------------------- 4
    void Step4Big()
    {
        _bigSources = Scripts.GenerateCoreBotsLibrary();
        _bigSources.Add(("BigScript.cs", Scripts.BigScript));
        var refs = new List<MetadataReference>([.. FrameworkRefs(), HostRef()]);
        string notes = "";
        for (int run = 1; run <= 2; run++)
        {
            string label = run == 1 ? "cold" : "warm";
            GC.Collect();
            CompileResult r;
            var before = _mem.Take();
            using (var peak = new PeakSampler())
            {
                r = ScriptCompiler.Compile("SpikeCoreBots", _bigSources, refs);
                peak.Dispose();
                Log($"  [{label}] compile: {r.Timings}");
                Log($"  [{label}] {peak}; managed delta vs before={MemSnapshot.DeltaMb(peak.PeakManaged - before.ManagedBytes)}, rss delta={MemSnapshot.DeltaKbMb(peak.PeakRssKb - before.RssKb)}");
            }
            if (!r.Success) { Log(ScriptCompiler.FormatErrors(r)); throw new InvalidOperationException("large compile failed"); }
            notes += $"{label} {r.TotalMs:F0}ms; ";
            _bigImage = r.Image;

            var sw = Stopwatch.StartNew();
            var asm = Assembly.Load(r.Image!);
            double load = sw.Elapsed.TotalMilliseconds;
            var host = new SpikeHost(Log);
            sw.Restart();
            ScriptCompiler.InvokeScriptMain(asm, host);
            string checksum = host.Get("checksum", "?");
            Log($"  [{label}] Assembly.Load={load:F1}ms ScriptMain={sw.Elapsed.TotalMilliseconds:F0}ms checksum={checksum} (Linux baseline: {ExpectedBigChecksum}) {(checksum == ExpectedBigChecksum ? "MATCH" : "MISMATCH/unknown")}");
        }
        _note = notes + $"{_bigSources.Count} trees, {_bigSources.Sum(s => s.Source.Count(c => c == '\n') + 1)} lines";
    }

    // ---------------------------------------------------------------- 5
    void Step5DiskCache()
    {
        var sources = _bigSources ?? throw new InvalidOperationException("step 4 did not produce sources");
        var refs = new List<MetadataReference>([.. FrameworkRefs(), HostRef()]);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Concat(sources.Select(s => s.Source)))))[..16];
        string dir = Path.Combine(platform.CacheDirectory, "scriptcache");
        Directory.CreateDirectory(dir);
        string asmName = "SpikeCached_" + hash;
        string path = Path.Combine(dir, asmName + ".dll");

        bool existed = File.Exists(path);
        Log($"  cache file {path} existed before this run: {existed}{(existed ? $" ({new FileInfo(path).Length / 1024}KB, from a previous Run)" : "")}");

        var sw = Stopwatch.StartNew();
        var r = ScriptCompiler.Compile(asmName, sources, refs);
        double compileMs = sw.Elapsed.TotalMilliseconds;
        if (!r.Success) { Log(ScriptCompiler.FormatErrors(r)); throw new InvalidOperationException("compile failed"); }
        Log($"  recompile (warm Roslyn): {r.Timings}");
        if (!existed)
        {
            sw.Restart();
            File.WriteAllBytes(path, r.Image!);
            Log($"  wrote cache file in {sw.Elapsed.TotalMilliseconds:F1}ms");
        }

        // A: read bytes + Assembly.Load(byte[])
        sw.Restart();
        var bytes = File.ReadAllBytes(path);
        var asmA = Assembly.Load(bytes);
        double loadA = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var hostA = new SpikeHost(_ => { });
        ScriptCompiler.InvokeScriptMain(asmA, hostA);
        double runA = sw.Elapsed.TotalMilliseconds;
        Log($"  A) File.ReadAllBytes+Assembly.Load(byte[]): load={loadA:F1}ms first ScriptMain={runA:F0}ms checksum={hostA.Get("checksum", "?")}");

        // B: AssemblyLoadContext.LoadFromAssemblyPath (desktop uses LoadFrom/LoadFromStream)
        sw.Restart();
        var alc = new AssemblyLoadContext("spike-cache-path", isCollectible: false);
        var asmB = alc.LoadFromAssemblyPath(path);
        double loadB = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var hostB = new SpikeHost(_ => { });
        ScriptCompiler.InvokeScriptMain(asmB, hostB);
        Log($"  B) ALC.LoadFromAssemblyPath: load={loadB:F1}ms first ScriptMain={sw.Elapsed.TotalMilliseconds:F0}ms checksum={hostB.Get("checksum", "?")} location='{asmB.Location}'");

        // C: ALC.LoadFromStream (FileStream)
        sw.Restart();
        var alc2 = new AssemblyLoadContext("spike-cache-stream", isCollectible: false);
        Assembly asmC;
        using (var fs = File.OpenRead(path)) asmC = alc2.LoadFromStream(fs);
        double loadC = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var hostC = new SpikeHost(_ => { });
        ScriptCompiler.InvokeScriptMain(asmC, hostC);
        Log($"  C) ALC.LoadFromStream(FileStream): load={loadC:F1}ms first ScriptMain={sw.Elapsed.TotalMilliseconds:F0}ms");

        Check(hostA.Get("checksum", "a") == hostB.Get("checksum", "b"), "cached assembly gives the same result in both contexts");
        Log($"  => cache load is ~{compileMs / Math.Max(0.1, loadA):F0}x faster than recompiling ({compileMs:F0}ms vs {loadA:F1}ms)");
        _note = $"recompile {compileMs:F0}ms vs cache load {loadA:F1}ms";
    }

    // ---------------------------------------------------------------- 6
    void Step6Collectible()
    {
        var r = ScriptCompiler.Compile("SpikeCollectible", [("Collectible.cs", Scripts.Collectible)], [.. FrameworkRefs(), HostRef()]);
        if (!r.Success) { Log(ScriptCompiler.FormatErrors(r)); throw new InvalidOperationException("compile failed"); }
        var small = r.Image!;

        int collected = 0, total = 0;
        foreach (var (label, image, n) in new[] { ("small script", small, _opt.CollectibleIterations), ("CoreBots-sized", _bigImage, _opt.BigCollectibleIterations) })
        {
            if (image == null) { Log($"  [{label}] skipped: no image"); continue; }
            ForceGc();
            var baseline = _mem.Take();
            Log($"  [{label}] {n} iterations, image {image.Length / 1024}KB; baseline {baseline}");
            for (int i = 0; i < n; i++)
            {
                var sw = Stopwatch.StartNew();
                var (wr, result) = LoadRunUnload(image, $"{label}-{i}");
                double runMs = sw.Elapsed.TotalMilliseconds;
                int gcs = 0;
                for (; gcs < 10 && wr.IsAlive; gcs++) ForceGc();
                total++;
                if (!wr.IsAlive) collected++;
                var m = _mem.Take();
                Log($"  [{label}] #{i}: run={runMs:F0}ms result={result} collected={!wr.IsAlive} (after {gcs} forced GC rounds) " +
                    $"managed={MemSnapshot.Mb(m.ManagedBytes)} ({MemSnapshot.DeltaMb(m.ManagedBytes - baseline.ManagedBytes)}) rss={MemSnapshot.KbMb(m.RssKb)} ({MemSnapshot.DeltaKbMb(m.RssKb - baseline.RssKb)})");
            }
            var end = _mem.Take();
            Log($"  [{label}] growth over {n} iterations: managed {MemSnapshot.DeltaMb(end.ManagedBytes - baseline.ManagedBytes)}, rss {MemSnapshot.DeltaKbMb(end.RssKb - baseline.RssKb)}, pss {MemSnapshot.DeltaKbMb(end.PssKb - baseline.PssKb)}");
        }
        _note = $"{collected}/{total} contexts collected";
        // Not a hard failure: the point is to report whether unloading works on this runtime.
        if (collected < total) Log($"  NOTE: {total - collected} of {total} collectible contexts were NOT collected on this runtime.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (WeakReference, string) LoadRunUnload(byte[] image, string name)
    {
        var alc = new AssemblyLoadContext("spike-collectible-" + name, isCollectible: true);
        var asm = alc.LoadFromStream(new MemoryStream(image));
        var host = new SpikeHost(_ => { });
        ScriptCompiler.InvokeScriptMain(asm, host);
        string result = host.Get<object?>("collectible-result", null)?.ToString() ?? host.Get("checksum", "?");
        var wr = new WeakReference(alc, trackResurrection: false);
        alc.Unload();
        return (wr, result);
    }

    static void ForceGc()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // ---------------------------------------------------------------- 7
    void Step7Background()
    {
        var r = ScriptCompiler.Compile("SpikeLoop", [("Loop.cs", Scripts.Loop)], [.. FrameworkRefs(), HostRef()]);
        if (!r.Success) { Log(ScriptCompiler.FormatErrors(r)); throw new InvalidOperationException("compile failed"); }
        var asm = Assembly.Load(r.Image!);
        var host = new SpikeHost(Log);
        host.Set("durationMs", _opt.BackgroundSeconds * 1000);
        Exception? scriptError = null;
        int threadId = -1;
        var t = new Thread(() =>
        {
            threadId = Environment.CurrentManagedThreadId;
            try { ScriptCompiler.InvokeScriptMain(asm, host); }
            catch (Exception ex) { scriptError = ex is TargetInvocationException { InnerException: { } i } ? i : ex; }
        }) { IsBackground = true, Name = "spike-script-loop" };
        var sw = Stopwatch.StartNew();
        t.Start();
        Log($"  started script thread; running ~{_opt.BackgroundSeconds}s; runner thread {Environment.CurrentManagedThreadId} keeps reporting");
        int lastIter = 0;
        while (!t.Join(5000))
        {
            int iter = host.Get("iter", 0);
            Log($"  [runner] t={sw.Elapsed.TotalSeconds:F0}s script iterations={iter} (+{iter - lastIter}) {_mem.Take()}");
            lastIter = iter;
            if (sw.Elapsed.TotalSeconds > _opt.BackgroundSeconds + 60)
            {
                host.StopRequested = true;
                Log("  [runner] script overran by 60s; requested stop");
            }
        }
        if (scriptError != null) throw new InvalidOperationException("script loop threw", scriptError);
        int iterations = host.Get("iterations", 0);
        Log($"  script finished after {sw.Elapsed.TotalSeconds:F1}s: iterations={iterations} acc={host.Get("acc", 0L)} scriptThread={host.Get("loopThread", -1)} (ui={_opt.UiThreadId}, runner={Environment.CurrentManagedThreadId})");
        Check(iterations > 0, "loop made progress");
        Check(threadId != _opt.UiThreadId, "script ran off the UI thread");
        _note = $"{iterations} iterations in {sw.Elapsed.TotalSeconds:F0}s on thread {threadId}";
    }
}
