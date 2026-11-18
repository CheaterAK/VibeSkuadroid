using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Scripting;
using Skua.Core.Models;
using System.Diagnostics;
using System.Text;

namespace Skua.App.Droid.Tests;

/// <summary>
/// Manual check against the real repository (network): syncs github.com/auqw/Scripts into Skua's script folder of
/// this test process and reads the options of real scripts through the app's Skua host. Runs only when
/// SKUA_REAL_SYNC_OUT names a report file; run it alone, with TMPDIR pointing somewhere disposable (the sandbox,
/// and so the synced files, live under the temp folder). SKUA_REAL_SYNC_SCRIPTS: ';'-separated scripts (relative to
/// the Scripts folder) whose options to read.
/// </summary>
public class RealRepositoryTests
{
    [Fact]
    public async Task SyncTheRealRepositoryAndReadRealScriptOptions()
    {
        string? output = Environment.GetEnvironmentVariable("SKUA_REAL_SYNC_OUT");
        if (string.IsNullOrEmpty(output))
            return;
        StringBuilder report = new();
        ScriptLibrary library = new(ClientFileSources.SkuaScriptsDIR);
        using ScriptRepositorySync sync = new(library, ClientFileSources.SkuaDIR);
        Stopwatch clock = Stopwatch.StartNew();
        RepositorySyncResult result = await sync.SyncAsync(null, CancellationToken.None);
        report.AppendLine($"sync: {result.Summary}");
        report.AppendLine($"wall: {clock.Elapsed.TotalSeconds:0.0} s; files on disk: {Directory.EnumerateFiles(library.Root, "*.cs", SearchOption.AllDirectories).Count()}");
        foreach (string p in result.Problems.Take(10))
            report.AppendLine("problem: " + p);

        string[] scripts = (Environment.GetEnvironmentVariable("SKUA_REAL_SYNC_SCRIPTS") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (scripts.Length > 0)
        {
            ScriptAssemblyStore store = new();
            SkuaScriptHostAdapter host = new(null, store, library.Root);
            await using HeadlessBotEngine engine = new(new HeadlessEngineOptions(), host);
            foreach (string script in scripts)
            {
                clock.Restart();
                try
                {
                    ScriptOptionSet set = await engine.GetScriptOptionsAsync(Path.Combine(library.Root, script));
                    report.AppendLine($"{script}: {set.Items.Count} options in {set.Storage} ({clock.Elapsed.TotalSeconds:0.0} s, {store.Count} assemblies loaded so far)");
                    foreach (ScriptOptionItem i in set.Items.Take(12))
                        report.AppendLine($"    [{i.Category}] {i.Name} ({i.Kind}{(i.Transient ? ", transient" : string.Empty)}) = {i.Value}");
                }
                catch (EngineException e)
                {
                    report.AppendLine($"{script}: {e.Message}");
                }
            }
        }
        await File.WriteAllTextAsync(output, report.ToString());
        Assert.True(result.Listed > 0, report.ToString());
    }
}
