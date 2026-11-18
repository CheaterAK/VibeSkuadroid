using Skua.Core.Flash;
using Skua.Core.Headless.Flash;
using Skua.Core.Interfaces;
using System.Dynamic;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>The IFlashUtil surface against the fake server, without Skua's services.</summary>
public sealed class BridgeTests : IAsyncLifetime
{
    private FakeWorld _world = null!;
    private HeadlessGame _game = null!;
    private HeadlessFlashUtil _flash = null!;
    private readonly List<(string Name, object[] Args)> _events = new();

    public Task InitializeAsync()
    {
        _world = new FakeWorld();
        _game = new HeadlessGame(new HeadlessGameOptions { Session = _world.SessionOptions, Client = new() { MapLoadTime = TimeSpan.FromMilliseconds(100) } }, _world.Api);
        _flash = new HeadlessFlashUtil(_game);
        _flash.FlashCall += (name, args) =>
        {
            lock (_events)
                _events.Add((name, args));
        };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _flash.Dispose();
        await _game.DisposeAsync();
        await _world.DisposeAsync();
    }

    private IFlashUtil F => _flash;

    private async Task LogIn()
    {
        _flash.InitializeFlash();
        _flash.Call("loadClient");
        F.CallGameFunction("login", FakeWorld.User, FakeWorld.Pass);
        Assert.True(await FakeWorld.Until(() => F.GetGameObject<int>("mcLogin.sl.iList.numChildren") == 2));
        Assert.True(F.Call<bool>("clickServer", "sandbox"));
        Assert.True(await FakeWorld.Until(() => F.Call<bool>("isLoggedIn") && !F.IsNull("world")));
        Assert.True(await FakeWorld.Until(() => !F.GetGameObject<bool>("world.mapLoadInProgress")));
    }

    [Fact]
    public async Task LoginLifecycleMatchesTheFlashScreens()
    {
        Assert.True(F.IsNull("world"));
        Assert.False(F.Call<bool>("isLoggedIn"));
        Assert.Equal("null", F.GetGameObject("world.strMapName"));   // null intermediate: _getObjectA answers null
        _flash.InitializeFlash();
        _flash.Call("loadClient");
        Assert.Equal(new[] { "requestLoadGame", "pre-load", "loaded" }, _events.Select(e => e.Name));

        F.CallGameFunction("login", FakeWorld.User, "wrong");
        Assert.True(await FakeWorld.Until(() => !string.IsNullOrEmpty(F.Call<string>("getActiveModalMessage"))));
        Assert.Contains("Wrong password", F.Call<string>("getActiveModalMessage"));
        F.Call("killLoginModals");
        Assert.Equal(string.Empty, F.Call<string>("getActiveModalMessage"));

        F.CallGameFunction("login", FakeWorld.User, FakeWorld.Pass);
        Assert.True(await FakeWorld.Until(() => F.GetGameObject<int>("mcLogin.sl.iList.numChildren") == 2));
        Assert.True(F.GetGameObject<bool>("mcLogin.visible"));
        Assert.Equal(FakeWorld.User, F.GetGameObjectStatic<string>("loginInfo.strUsername"));
        Assert.Equal(-1, F.GetGameObjectStatic<int>("objLogin.iUpgDays", int.MinValue));
        List<Skua.Core.Models.Servers.Server> servers = F.GetGameObject<List<Skua.Core.Models.Servers.Server>>("serialCmd.servers")!;
        Assert.Equal(new[] { "Sandbox", "Offline" }, servers.Select(s => s.Name));
        Assert.False(servers[1].Online);

        Assert.False(F.Call<bool>("clickServer", "nowhere"));
        Assert.True(F.Call<bool>("clickServer", "sandbox"));
        Assert.True(await FakeWorld.Until(() => F.Call<bool>("isLoggedIn")));
        Assert.False(F.IsNull("world"));
        Assert.False(F.IsNull("world.myAvatar"));
        Assert.True(F.CallGameFunction<bool>("world.myAvatar.pMC.artLoaded"));
        Assert.Equal("127.0.0.1", F.GetGameObjectStatic<string>("serverIP"));
        Assert.False(F.GetGameObject<bool>("mcLogin.visible"));

        F.CallGameFunction("logout");
        Assert.True(await FakeWorld.Until(() => !F.Call<bool>("isLoggedIn")));
        Assert.True(F.IsNull("world"));
        Assert.True(await FakeWorld.Until(() => _events.Any(e => e.Name == "connectionLost")));
        Assert.Contains(_events, e => e.Name == "packet" && ((string)e.Args[0]) == "%xt%zm%cmd%1%logout%");
    }

    [Fact]
    public async Task PathReadsHaveSkuaSwfShapes()
    {
        await LogIn();
        Assert.Equal("\"testmap\"", F.GetGameObject("world.strMapName"));
        Assert.Equal("testmap", F.GetGameObject<string>("world.strMapName"));
        Assert.Equal(string.Empty, F.GetGameObject("world.myAvatar.objData.noSuchField"));   // undefined
        Assert.Equal("null", F.GetGameObject("world.shopinfo"));
        Assert.True(F.IsNull("world.shopinfo"));
        Assert.Equal(3, F.GetGameObject<int>("world.myAvatar.items.length"));
        Assert.Equal(FakeWorld.User.ToLowerInvariant(), F.GetGameObject<string>("sfc.myUserName"));
        Assert.Equal(1000, F.GetGameObject<int>("world.myAvatar.dataLeaf.intHP"));
        Assert.Equal(2, F.GetGameObject<int>("world.myAvatar.objData.iRank"));   // 3000 class points
        Assert.Equal(3000, F.GetGameObject<int>("world.myAvatar.items.1.iQty"));  // the equipped class
        Assert.Equal(2, F.GetGameObject<int>("world.myAvatar.factions.0.iRank"));
        Assert.Equal(new[] { "Enter", "r2", "r3" }, F.SelectArrayObjects<string>("world.map.currentScene.labels", "name"));
        Assert.Equal(3000, F.GetGameObject<int>("world.lock.loadShop.cd"));
        Assert.Equal(5, F.GetGameObject<Skua.Core.Models.Skills.SkillInfo[]>("world.actions.active")!.Length);
        // getGameObjectKey: what IFlashUtil sends for a bracketed path.
        Assert.NotNull(F.GetGameObject<Skua.Core.Models.Players.PlayerInfo>("world.uoTree[\"hero\"]"));
        Assert.Null(F.GetGameObject<object>("world.actions.active[0].isOK"));   // ".isOK" is the key, as on Windows

        // Display-only state reads back what was written; unknown display paths are gaps.
        F.SetGameObject("litePreference.data.bCustomDrops", true);
        Assert.True(F.GetGameObject<bool>("litePreference.data.bCustomDrops"));
        F.SetGameObject("ui.mcFPS.visible", true);
        Assert.True(F.GetGameObject<bool>("ui.mcFPS.visible"));
        F.SetGameObject("world.WALKSPEED", 12);
        Assert.Equal(12, F.GetGameObject<int>("world.WALKSPEED"));
        Assert.Equal("null", F.GetGameObject("ui.someWindow.text"));   // a missing intermediate gives null, as _getObjectA
        Assert.Contains(_flash.Gaps.Snapshot(), g => g.Name == "path:ui.someWindow.text" && g.Kind == FlashGapKind.Missing);
    }

    [Fact]
    public async Task EventsComeInSkuaSwfOrderAndShape()
    {
        await LogIn();
        int start = _events.Count;
        F.Call("jumpCorrectRoom", "r2", "Nowhere", true, false);
        Assert.True(await FakeWorld.Until(() => _events.Skip(start).Any(e => e.Name == "pext")));
        List<(string Name, object[] Args)> evs;
        lock (_events)
            evs = _events.Skip(start).ToList();
        (string Name, object[] Args) sent = evs.First(e => e.Name == "packet");
        Assert.Equal("%xt%zm%moveToCell%901%r2%Left%", sent.Args[0]);   // pad corrected from the map file
        int received = evs.FindIndex(e => e.Name == "packetFromServer");
        Assert.Equal("pext", evs[received + 1].Name);
        JsonObject pext = (JsonObject)JsonNode.Parse((string)evs[received + 1].Args[0])!;
        Assert.Equal("str", (string?)pext["params"]!["type"]);
        Assert.Equal("uotls", (string?)pext["params"]!["dataObj"]![0]);
        Assert.Equal("r2", F.GetGameObject<string>("world.strFrame"));
    }

    [Fact]
    public async Task CombatThroughTheCallbacks()
    {
        await LogIn();
        F.Call("jumpCorrectRoom", "r2", "Left");
        Assert.True(await FakeWorld.Until(() => F.Call<string>("availableMonsters")!.Contains("Pixie")));
        Assert.False(F.Call<bool>("canUseSkill", 0));   // no target
        Assert.True(F.Call<bool>("attackMonsterName", "pixie"));
        JsonObject target = (JsonObject)JsonNode.Parse(F.Call("getTargetMonster")!)!;
        Assert.Equal("Pixie", (string?)target["strMonName"]);
        Assert.True(await FakeWorld.Until(() => _game.World.GetMonster((int)target["MonMapID"]!)!.Hp < 100));
        Assert.True(await FakeWorld.Until(() => F.Call("getTargetMonster") == "{}", 15000));
        bool dropped = await FakeWorld.Until(() => _game.World.GetDrops().Any(d => d.Name == "Pixie Wing"));
        Assert.True(dropped, string.Join(" | ", _world.Server.LastSession!.Commands) + " || " + string.Join(",", _game.World.GetPacketCounters().Handled.Keys) + " || " + string.Join(",", _game.World.GetMonsters().Select(m => m.MonMapId + ":" + m.Hp + "/" + m.State)));
        Assert.Contains("\"nam\":\"Judged\"", F.Call("GetMonsterAuraByID", (int)target["MonMapID"]!) ?? string.Empty);
    }

    [Fact]
    public async Task LinkedObjectsAndArraysWorkWithSkuaFlashObject()
    {
        await LogIn();
        using FlashArray<object> skills = (FlashArray<object>)F.CreateFlashObject<object>("world.actions.active").ToArray();
        int count = 0;
        foreach (FlashObject<object> skill in skills)
        {
            using FlashObject<long> ts = (FlashObject<long>)skill.GetChild<long>("ts");
            ts.Value = 1234 + count;
            count++;
        }
        Assert.Equal(5, count);
        Assert.Equal(1236, F.GetGameObject<long>("world.actions.active.2.ts"));
        using IFlashObject<string> name = F.CreateFlashObject<string>("world.strMapName");
        Assert.Equal("testmap", name.Value);
    }

    [Fact]
    public async Task GameFunctionsAndGaps()
    {
        await LogIn();
        dynamic item = new ExpandoObject();
        item.ItemID = 30;
        Assert.True(F.CallGameFunction<bool>("world.sendEquipItemRequest", (object)item));
        Assert.Equal(-1, F.CallGameFunction<int>("world.getQuestValue", 500));
        Assert.Equal(0, F.CallGameFunction<int>("world.getQuestValue", 5));
        Assert.Equal(-1, F.CallGameFunction<int>("world.myAvatar.getCPByID", 999));
        Assert.Equal(1000, F.CallGameFunction<int>("world.myAvatar.getRep", 2));
        Assert.Equal(string.Empty, F.CallGameFunction("world.someUnknownFunction", 1));   // undefined -> the string default
        Assert.Equal(string.Empty, F.Call("injectScript", "http://example/x.swf"));
        Assert.Equal(string.Empty, F.Call("notACallback"));
        IReadOnlyList<FlashGap> gaps = _flash.Gaps.Snapshot();
        Assert.Contains(gaps, g => g.Name == "fn:world.someUnknownFunction" && g.Kind == FlashGapKind.Missing && g.ArgsShape == "(int)");
        Assert.Contains(gaps, g => g.Name == "injectScript" && g.Kind == FlashGapKind.Impossible);
        Assert.Contains(gaps, g => g.Name == "notACallback");
        Assert.Contains("Flash gap log", _flash.Gaps.Format());
        // Every Externalizer callback is known to the bridge.
        Assert.Equal(89, _flash.Callbacks.Count);   // Externalizer.as registers 89
    }
}
