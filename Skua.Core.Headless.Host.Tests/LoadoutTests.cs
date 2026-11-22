using Skua.Core.Headless.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>
/// Outfits headless: a script lists the account's saved outfits through skua.swf's getLoadouts and equips one with
/// the game's equipLoadout request; Skua's own loadout service (ILoadoutService) equips a saved profile item by item.
/// </summary>
public sealed class LoadoutTests : IAsyncLifetime
{
    private const string Script = """
        using System.Linq;
        using Newtonsoft.Json.Linq;
        using Skua.Core.Interfaces;

        public class HeadlessLoadouts
        {
            public void ScriptMain(IScriptInterface bot)
            {
                string json = bot.Flash.Call("getLoadouts");
                bot.Log("loadouts=" + json);
                JObject sets = JObject.Parse(json);
                bot.Log("names=" + string.Join(",", sets.Properties().Select(p => p.Name)));
                string name = sets.Properties().First(p => (int)p.Value["ar"] == 50).Name;
                bot.Send.Packet($"%xt%zm%equipLoadout%{bot.Map.RoomID}%cmd%{name}%1%");
                bot.Wait.ForTrue(() => bot.Player.CurrentClass?.Name == "Rogue", 40);
                bot.Log("class=" + bot.Player.CurrentClass?.Name + " weapon=" + bot.Inventory.Items.First(i => i.Equipped && i.ItemGroup == "Weapon").Name);
                bot.Log("DONE");
            }
        }
        """;

    private static readonly string s_data = Path.Combine(Path.GetTempPath(), "skua-headless-loadouts-" + Environment.ProcessId);
    private readonly ITestOutputHelper _output;
    private readonly List<string> _log = new();
    private FakeWorld _world = null!;
    private HeadlessSkuaSession _session = null!;

    public LoadoutTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        _world = new FakeWorld();
        _world.Server.ExtraInventory.AddRange(new[] { 50, 51 });
        _world.Server.Loadouts["Rogue Set"] = JsonNode.Parse("""{"ar":50,"Weapon":51,"colors":{"hair":1,"skin":2,"eye":3,"base":4,"trim":5,"accessory":6}}""");
        _world.Server.Loadouts["Healer"] = JsonNode.Parse("""{"ar":2,"Weapon":1}""");
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

    private async Task<string[]> Run(string name, string source)
    {
        string file = Path.Combine(s_data, name + ".cs");
        Directory.CreateDirectory(s_data);
        await File.WriteAllTextAsync(file, source);
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? error = null;
        _session.ScriptError += (_, e) => error = e;
        _session.ScriptRunningChanged += (_, running) =>
        {
            if (!running)
                stopped.TrySetResult();
        };
        Assert.Null(await _session.StartScriptAsync(file));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(90));
        string[] lines;
        lock (_log)
            lines = _log.Where(l => l.StartsWith("[script]", StringComparison.Ordinal)).ToArray();
        foreach (string l in lines)
            _output.WriteLine(l);
        Assert.True(error is null, error?.ToString() + "\n" + string.Join("\n", _log));
        return lines;
    }

    [Fact]
    public async Task AScriptListsTheSavedOutfitsAndEquipsOne()
    {
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"), string.Join("\n", _log));
        string[] script = await Run("HeadlessLoadouts", Script);

        // skua.swf: JSON.stringify(world.objInfo["customs"].loadouts), in the server's order.
        Assert.Contains(script, l => l.EndsWith("""loadouts={"Rogue Set":{"ar":50,"Weapon":51,"colors":{"hair":1,"skin":2,"eye":3,"base":4,"trim":5,"accessory":6}},"Healer":{"ar":2,"Weapon":1}}""", StringComparison.Ordinal));
        Assert.Contains(script, l => l.EndsWith("names=Rogue Set,Healer", StringComparison.Ordinal));
        Assert.Contains(script, l => l.EndsWith("class=Rogue weapon=Pixie Blade", StringComparison.Ordinal));
        Assert.Contains(script, l => l.EndsWith("DONE", StringComparison.Ordinal));
        (string cmd, string[] args, string? violation) = Assert.Single(_world.Server.LoadoutRequests);
        Assert.Equal(("equipLoadout", (string?)null), (cmd, violation));
        Assert.Equal(new[] { "cmd", "Rogue Set", "1" }, args);
        Assert.DoesNotContain(_session.Gaps.Snapshot(), g => g.Name == "getLoadouts");
    }

    [Fact]
    public async Task GetLoadoutsAnswersAsSkuaSwfBeforeAndAfterLoadPrefs()
    {
        Assert.True(string.IsNullOrEmpty(_session.Flash.Call("getLoadouts")));   // no world yet: AS3 throws, the bridge answers the default
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"), string.Join("\n", _log));
        string? json = _session.Flash.Call("getLoadouts");
        Assert.NotNull(json);
        Assert.Equal(new[] { "Rogue Set", "Healer" }, JsonNode.Parse(json!)!.AsObject().Select(p => p.Key));
        Assert.Equal("{}", ((IFlashUtil)_session.Flash).GetGameObject("world.objInfo.customs.costumes"));
    }

    [Fact]
    public async Task SkuasLoadoutServiceEquipsASavedProfileHeadless()
    {
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"), string.Join("\n", _log));
        ILoadoutService service = _session.Services.GetRequiredService<ILoadoutService>();
        // Rogue is equipped by the service; the sword is already on, so it is not sent again. (Two equips in a row would
        // need Skua's world.lock wait, which never waits: GameActionLock's get-only CD/TS deserialize as 0 on desktop too.)
        LoadoutProfile profile = new() { Name = "Rogue", Class = "Rogue", ClassEnhancement = "Lucky", Weapon = "Default Sword", WeaponEnhancement = "Lucky" };
        Assert.True(service.SaveLoadout(profile));
        Assert.Contains(service.Loadouts, l => l.Name == "Rogue");

        List<string> missing = await service.EquipLoadoutAsync(service.Loadouts.Single(l => l.Name == "Rogue")).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(missing.Count == 0, string.Join(",", missing) + "\n" + string.Join("\n", _log));
        Assert.Equal("Rogue", _session.Bot.Player.CurrentClass?.Name);
        Assert.Contains(_session.Bot.Inventory.Items, i => i.Equipped && i.Name == "Default Sword");
        Assert.Equal(new[] { "%xt%zm%equipItem%901%50%" }, _world.Server.LastSession!.Received.Select(r => r.Text).Where(t => t.Contains("%equipItem%", StringComparison.Ordinal)));
        lock (_log)
            Assert.Contains(_log, l => l.Contains("All items are already properly enhanced for Rogue", StringComparison.Ordinal));
    }
}
