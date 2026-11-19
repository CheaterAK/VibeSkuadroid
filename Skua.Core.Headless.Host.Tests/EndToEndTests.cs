using Skua.Core.Headless.Flash;
using Xunit.Abstractions;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>
/// Unmodified Skua.Core (ScriptManager, compiler, ScriptInterface, Script*) runs a real script against the fake
/// server through the headless bridge: Skua's own login code, map join and jump, Kill and Hunt, quest accept and
/// turn-in, drop pickup, inventory and bank reads.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private const string Script = """
        using System.Linq;
        using Skua.Core.Interfaces;

        public class HeadlessEndToEnd
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Log("start user=" + bot.Player.Username + " map=" + bot.Map.Name + " loaded=" + bot.Player.Loaded);
                bot.Options.HuntDelay = 0;
                bot.Map.Join("testmap");
                bot.Log("cells=" + string.Join(",", bot.Map.Cells));
                bot.Quests.EnsureLoad(1000);
                bool accepted = bot.Quests.EnsureAccept(1000);
                bot.Log("accepted=" + accepted);
                bot.Map.Jump("r2", "Left");
                bot.Log("cell=" + bot.Player.Cell + " monsters=" + bot.Monsters.CurrentAvailableMonsters.Count);
                bot.Skills.StartAdvanced("1 | 2", 150);
                int kills = 0;
                while (!bot.TempInv.Contains("Pixie Dust", 3) && kills < 12)
                {
                    bot.Kill.Monster("Pixie");
                    kills++;
                }
                bot.Log("dust=" + bot.TempInv.GetQuantity("Pixie Dust") + " kills=" + kills);
                bot.Wait.ForDrop("Pixie Wing");
                bot.Drops.Pickup("Pixie Wing");
                bot.Wait.ForPickup("Pixie Wing");
                bot.Log("wing=" + bot.Inventory.GetQuantity("Pixie Wing"));
                bot.Log("canComplete=" + bot.Quests.CanComplete(1000));
                bool completed = bot.Quests.EnsureComplete(1000);
                bot.Log("completed=" + completed + " crown=" + bot.Inventory.Contains("Pixie Crown"));
                bot.Map.Jump("Enter", "Spawn");
                bot.Hunt.Monster("Pixie");
                bot.Log("hunted cell=" + bot.Player.Cell);
                bot.Skills.Stop();
                bot.Bank.Load();
                bot.Log("bank=" + string.Join(",", bot.Bank.Items.Select(i => i.Name + "x" + i.Quantity)));
                bot.Log("inventory=" + string.Join(",", bot.Inventory.Items.Select(i => i.Name + "x" + i.Quantity)) + " used=" + bot.Inventory.UsedSlots + "/" + bot.Inventory.Slots);
                bot.Log("gold=" + bot.Player.Gold + " rank=" + bot.Player.CurrentClassRank + " level=" + bot.Player.Level);
                bot.Log("DONE");
            }
        }
        """;

    private static readonly string s_data = Path.Combine(Path.GetTempPath(), "skua-headless-e2e-" + Environment.ProcessId);
    private readonly ITestOutputHelper _output;
    private readonly List<string> _log = new();
    private FakeWorld _world = null!;
    private HeadlessSkuaSession _session = null!;

    public EndToEndTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        _world = new FakeWorld();
        HeadlessSkuaOptions options = new()
        {
            DataDirectory = s_data,
            WebApi = _world.Api,
            Game = new HeadlessGameOptions { Session = _world.SessionOptions, Client = new() { MapLoadTime = TimeSpan.FromMilliseconds(200) } },
            Log = e =>
            {
                lock (_log)
                    _log.Add($"[{e.Source}] {e.Message}");
            },
        };
        _session = HeadlessSkuaSession.Create(options);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _world.DisposeAsync();
    }

    private string[] Lines(string source)
    {
        lock (_log)
            return _log.Where(l => l.StartsWith($"[{source}]", StringComparison.Ordinal)).ToArray();
    }

    [Fact]
    public async Task ScriptRunsToCompletionThroughUnmodifiedSkuaCore()
    {
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"), string.Join("\n", _log));
        Assert.True(_session.Bot.Player.Playing);
        Assert.Equal("testmap", _session.Bot.Map.Name);

        string file = Path.Combine(s_data, "HeadlessEndToEnd.cs");
        Directory.CreateDirectory(s_data);
        await File.WriteAllTextAsync(file, Script);
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? error = null;
        _session.ScriptError += (_, e) => error = e;
        _session.ScriptRunningChanged += (_, running) =>
        {
            if (!running)
                stopped.TrySetResult();
        };
        Exception? start = await _session.StartScriptAsync(file);
        Assert.Null(start);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(120));

        string[] script = Lines("script");
        foreach (string l in script)
            _output.WriteLine(l);
        _output.WriteLine(_session.Gaps.Format());
        Assert.True(error is null, error?.ToString() + "\n" + string.Join("\n", _log));
        Assert.Contains(script, l => l.Contains("cells=Enter,r2,r3", StringComparison.Ordinal));
        Assert.Contains(script, l => l.Contains("accepted=True", StringComparison.Ordinal));
        Assert.Contains(script, l => l.Contains("cell=r2 monsters=2", StringComparison.Ordinal));
        Assert.Contains(script, l => l.Contains("dust=3", StringComparison.Ordinal));
        Assert.Contains(script, l => l.Contains("wing=1", StringComparison.Ordinal));
        Assert.Contains(script, l => l.Contains("completed=True crown=True", StringComparison.Ordinal));
        Assert.Contains(script, l => l.Contains("hunted cell=r2", StringComparison.Ordinal));
        Assert.Contains(script, l => l.Contains("bank=Pixie Wingx2", StringComparison.Ordinal));
        Assert.Contains(script, l => l.EndsWith("DONE", StringComparison.Ordinal));
        Assert.DoesNotContain(_session.Gaps.Snapshot(), g => g.Kind == FlashGapKind.Missing);

        HeadlessSkuaStatus status = _session.GetStatus();
        Assert.True(status.Kills >= 3);
        Assert.Equal(1, status.QuestsCompleted);

        await _session.LogoutAsync();
        Assert.True(await FakeWorld.Until(() => !_session.Game.IsInGame));
        Assert.True(_session.Bot.Servers.IsIntentionalLogout);
    }
}
