using System.Diagnostics;
using System.Globalization;
using Skua.App.Droid.Scripting;
using Skua.Core.Headless.Host;
using Skua.Core.Scripts;

namespace Skua.App.Droid.Tests;

/// <summary>
/// Memory of one CoreBots-based script compile on the engine's own pipeline (Skua.Core ScriptManager, the APK's
/// reference images, the app's assembly store): cold (empty compiled-script cache) or warm. Runs only when
/// SKUA_COMPILE_DATA names a Skua data folder whose VibeSkua/Scripts holds CoreBots.cs and its includes (the
/// community repository); run it alone, one mode per process (Skua's data folder is fixed once per process):
/// SKUA_COMPILE_MODE=cold|warm, SKUA_COMPILE_REPORT=file to append the numbers to.
/// </summary>
[Collection(nameof(MemoryHeavy))]
public sealed class CompileMemoryTests
{
    private const string Script = """
        //cs_include Scripts/CoreBots.cs
        //cs_include Scripts/CoreFarms.cs
        //cs_include Scripts/CoreAdvanced.cs
        //cs_include Scripts/CoreStory.cs
        //cs_include Scripts/CoreDailies.cs
        using Skua.Core.Interfaces;

        public class MeasureCoreBots
        {
            public CoreBots Core => CoreBots.Instance;
            public CoreFarms Farm = new();
            public CoreAdvanced Adv = new();
            public CoreStory Story = new();
            public CoreDailies Daily = new();

            public void ScriptMain(IScriptInterface bot)
            {
                Core.SetOptions();
                Core.SetOptions(false);
            }
        }
        """;

    internal static long ReadStatusKb(string key)
    {
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith(key, StringComparison.Ordinal))
                return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
        }
        return -1;
    }

    [Fact]
    public async Task CoreBotsCompileMemory()
    {
        string? data = Environment.GetEnvironmentVariable("SKUA_COMPILE_DATA");
        if (string.IsNullOrEmpty(data) || !OperatingSystem.IsLinux())
            return;
        string mode = Environment.GetEnvironmentVariable("SKUA_COMPILE_MODE") ?? "cold";
        string scripts = Path.Combine(data, "VibeSkua", "Scripts");
        string cache = Path.Combine(scripts, "Cached-Scripts");
        if (mode == "cold" && Directory.Exists(cache))
            Directory.Delete(cache, recursive: true);
        string file = Path.Combine(scripts, "MeasureCoreBots.cs");
        await File.WriteAllTextAsync(file, Script);

        ScriptReferenceSet refs = ScriptReferenceSet.Load(new DirectoryScriptReferenceSource(Path.Combine(AppContext.BaseDirectory, "script-refs")));
        await using HeadlessSkuaSession session = HeadlessSkuaSession.Create(new HeadlessSkuaOptions
        {
            DataDirectory = data,
            ScriptReferences = refs.Provider,
            ScriptNamespaces = ScriptReferenceSet.Namespaces,
        });
        ScriptManager manager = (ScriptManager)session.Scripts;
        manager.LoadContextFactory = new StoreScriptLoadContext(new ScriptAssemblyStore()).BeginCompile;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long heap0 = GC.GetTotalMemory(true);
        long rss0 = ReadStatusKb("VmRSS:");
        File.WriteAllText("/proc/self/clear_refs", "5"); // resets VmHWM (peak RSS) to the current RSS
        long allocated0 = GC.GetTotalAllocatedBytes(true);
        long peakHeap = heap0;
        bool done = false;
        Thread sampler = new(() =>
        {
            while (!Volatile.Read(ref done))
            {
                peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
                Thread.Sleep(10);
            }
        }) { IsBackground = true };
        sampler.Start();
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        Stopwatch sw = Stopwatch.StartNew();
        manager.SetLoadedScript(file);
        object? instance = await Task.Run(() => manager.Compile(Script));
        sw.Stop();
        Volatile.Write(ref done, true);
        sampler.Join();
        Assert.NotNull(instance);
        long peakRss = ReadStatusKb("VmHWM:");
        long heapNoGc = GC.GetTotalMemory(false);
        instance = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long retained = GC.GetTotalMemory(true);
        long rssAfter = ReadStatusKb("VmRSS:");

        string report = string.Create(CultureInfo.InvariantCulture,
            $"{mode,-5} compile {sw.Elapsed.TotalSeconds,6:0.00} s | heap before {heap0 / 1048576.0,6:0.0} MB, peak {peakHeap / 1048576.0,6:0.0} MB, right after {heapNoGc / 1048576.0,6:0.0} MB, "
            + $"retained after full GC {retained / 1048576.0,6:0.0} MB (+{(retained - heap0) / 1048576.0:0.0}) | allocated {(GC.GetTotalAllocatedBytes(true) - allocated0) / 1048576.0,7:0.0} MB | "
            + $"RSS before {rss0 / 1024.0,6:0} MB, peak {peakRss / 1024.0,6:0} MB, after {rssAfter / 1024.0,6:0} MB | GCs gen0 {GC.CollectionCount(0) - gen0}, gen2 {GC.CollectionCount(2) - gen2} | "
            + $"refs read {refs.Provider.Loads}, resident {refs.Provider.Resident} | cache {Directory.GetFiles(cache, "*.dll").Length} dlls {Directory.GetFiles(cache, "*.dll").Sum(f => new FileInfo(f).Length) / 1048576.0:0.0} MB");
        if (Environment.GetEnvironmentVariable("SKUA_COMPILE_REPORT") is string reportFile && reportFile.Length > 0)
            File.AppendAllText(reportFile, report + "\n");
        Assert.True(Directory.GetFiles(cache, "*.dll").Length >= 6);
        // Retention: no reference image stays in memory after a compile; a cached start reads none and keeps nothing.
        Assert.Equal(0, refs.Provider.Resident);
        if (mode == "warm")
        {
            Assert.Equal(0, refs.Provider.Loads);
            Assert.True(retained - heap0 < 3 * 1048576, report);
        }
    }
}
