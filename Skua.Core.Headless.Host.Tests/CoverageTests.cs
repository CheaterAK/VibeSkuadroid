using Skua.Core.Headless.Flash;
using System.Text;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>
/// Measures what the bridge answers for a list of Flash calls, paths and game functions extracted statically from
/// source (a JSON file of {kind: {name: [files]}}, kinds call/gamefn/get/isnull/set/arrayobj/select/flashobj/getstatic/module).
/// Runs only when SKUA_SCAN_FILES (';'-separated) is set; writes the table to SKUA_COVERAGE_OUT.
/// </summary>
public sealed class CoverageTests : IAsyncLifetime
{
    private FakeWorld _world = null!;
    private HeadlessGame _game = null!;
    private HeadlessFlashUtil _flash = null!;

    public async Task InitializeAsync()
    {
        _world = new FakeWorld();
        _game = new HeadlessGame(new HeadlessGameOptions { Session = _world.SessionOptions }, _world.Api);
        _flash = new HeadlessFlashUtil(_game);
        _flash.Call("loadClient");
        await _game.Login(FakeWorld.User, FakeWorld.Pass);
        _game.ConnectTo(_game.FindServer("Sandbox")!);
        await FakeWorld.Until(() => _game.IsInGame);
    }

    public async Task DisposeAsync()
    {
        _flash.Dispose();
        await _game.DisposeAsync();
        await _world.DisposeAsync();
    }

    private static readonly string[] s_noOpRoots = { "litePreference.", "ui.", "stage.", "userPreference.", "uoPref.", "mcExtSWF.", "world.myAvatar.pMC.pname", "world.rootClass.ui.", "world.visible" };

    private string Classify(string kind, string name)
    {
        string concrete = name.Replace("{}", "0", StringComparison.Ordinal);
        int bracket = concrete.IndexOf('[');
        if (bracket > 0)
            concrete = concrete[..bracket];
        switch (kind)
        {
            case "call":
                return _flash.Callbacks.TryGetValue(name, out BridgeSupport s) ? s.ToString() : "Missing";
            case "module":
                return "NoOp";
            case "gamefn":
                if (name.Contains("{}", StringComparison.Ordinal))
                    return _flash.GameFunctions.Keys.Any(k => k.StartsWith(name[..name.IndexOf('{')], StringComparison.Ordinal)) ? "Implemented" : "Missing";
                return _flash.GameFunctions.TryGetValue(name, out BridgeSupport f) ? f.ToString() : "Missing";
            case "getstatic":
                return WithGapCheck(() => _flash.ResolveStatic(concrete));
            case "set":
                if (s_noOpRoots.Any(r => concrete.StartsWith(r, StringComparison.Ordinal)))
                    return "NoOp";
                return WithGapCheck(() =>
                {
                    HeadlessFlashUtil.Lookup now = _flash.Resolve(concrete);
                    _flash.Assign(concrete, now.Node?.DeepClone());
                    return now;
                });
            default:
                string path = kind == "select" ? concrete.Split('#')[0] : concrete;
                if (s_noOpRoots.Any(r => path.StartsWith(r, StringComparison.Ordinal)))
                    return WithGapCheck(() => _flash.Resolve(path)) == "Implemented" ? "NoOp" : "Missing";
                return WithGapCheck(() => _flash.Resolve(path));
        }
    }

    private string WithGapCheck(Func<HeadlessFlashUtil.Lookup> run)
    {
        _flash.Gaps.Clear();
        run();
        IReadOnlyList<FlashGap> gaps = _flash.Gaps.Snapshot();
        if (gaps.Any(g => g.Kind == FlashGapKind.Missing))
            return "Missing";
        if (gaps.Any(g => g.Kind == FlashGapKind.Impossible))
            return "Impossible";
        return gaps.Any(g => g.Kind == FlashGapKind.Approximated) ? "Approximated" : "Implemented";
    }

    [Fact]
    public void Measure()
    {
        string? files = Environment.GetEnvironmentVariable("SKUA_SCAN_FILES");
        string? output = Environment.GetEnvironmentVariable("SKUA_COVERAGE_OUT");
        if (string.IsNullOrEmpty(files) || string.IsNullOrEmpty(output))
            return;
        Assert.True(_game.IsInGame);
        StringBuilder sb = new();
        foreach (string file in files.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonObject scan = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
            Dictionary<string, int> totals = new();
            List<string> rows = new();
            foreach (KeyValuePair<string, JsonNode?> kind in scan)
            {
                foreach (KeyValuePair<string, JsonNode?> entry in (JsonObject)kind.Value!)
                {
                    string verdict = Classify(kind.Key, entry.Key);
                    totals[verdict] = totals.GetValueOrDefault(verdict) + 1;
                    rows.Add($"{verdict,-13} {kind.Key,-10} {entry.Key}  ({((JsonArray)entry.Value!).Count} files)");
                }
            }
            sb.AppendLine($"## {Path.GetFileName(file)}: " + string.Join(", ", totals.OrderBy(t => t.Key).Select(t => $"{t.Key} {t.Value}")));
            foreach (string row in rows.OrderBy(r => r, StringComparer.Ordinal))
                sb.AppendLine(row);
            sb.AppendLine();
        }
        File.WriteAllText(output, sb.ToString());
    }
}
