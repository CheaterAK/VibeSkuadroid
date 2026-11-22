using Skua.Core.Headless.Client;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Headless.World;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Tests;

/// <summary>Outfits ("loadouts"): Game.as loadPrefs / addLoadout / removeLoadout / wearLoadout and the OutfitSets requests.</summary>
public class LoadoutTests
{
    private const string Prefs = """
        {"cmd":"loadPrefs","success":true,"result":{"costumes":{},"loadouts":{
          "Rogue Set":{"ar":50,"Weapon":51,"co":52,"colors":{"hair":1,"skin":2,"eye":3,"base":4,"trim":5,"accessory":6}},
          "Healer":{"ar":2,"Weapon":1}},"prefs":{}}}
        """;

    private static (GameClient Client, WorldModel World, RecordingSender Sender, ManualTime Time) Make()
    {
        ManualTime time = new();
        WorldModel w = WorldModelTests.LoggedIn();
        RecordingSender sender = new();
        GameClient c = new(w, sender, time);
        return (c, w, sender, time);
    }

    private static void Feed(GameClient c, WorldModel w, InboundPacket p)
    {
        w.Apply(p);
        c.OnInbound(p);
    }

    [Fact]
    public void LoadPrefsKeepsTheLiveShapeAndAnEmptyAccountHasNoSets()
    {
        WorldModel w = WorldModelTests.LoggedIn();
        Assert.False(w.HasLoadPrefs);
        Assert.Null(w.GetLoadouts());
        Assert.True(w.Apply(Pkt.Json("""{"cmd":"loadPrefs","success":true,"result":{"costumes":{},"loadouts":{},"prefs":{}}}""")));
        Assert.True(w.HasLoadPrefs);
        Assert.Empty(w.GetLoadouts()!);
        Assert.Equal("""{"costumes":{},"loadouts":{},"prefs":{}}""", w.GetGameObject("world.objInfo.customs"));

        // A failed loadPrefs leaves customs as it was (Game.as only assigns on success).
        Assert.True(w.Apply(Pkt.Json("""{"cmd":"loadPrefs","success":false}""")));
        Assert.Equal("""{"costumes":{},"loadouts":{},"prefs":{}}""", w.GetGameObject("world.objInfo.customs"));

        // A new connection forgets them.
        w.Reset();
        Assert.False(w.HasLoadPrefs);
    }

    [Fact]
    public void SavedSetsFollowAddAndRemoveRepliesInTheOrderTheyWereSent()
    {
        (GameClient c, WorldModel w, RecordingSender sender, ManualTime time) = Make();
        Feed(c, w, Pkt.Json(Prefs));
        w.Apply(Pkt.Json("""{"cmd":"initUserData","uid":40001,"data":{"strUsername":"Hero","iLoadoutSlots":3}}"""));
        Assert.Equal(new[] { "Rogue Set", "Healer" }, w.GetLoadouts()!.Select(p => p.Key));
        Assert.Equal(51, (int)w.GetLoadouts()!["Rogue Set"]!["Weapon"]!);

        // New set, as outfitedit.onSetUpdate sends it: name, the set without its name, "" (no original).
        JsonObject farm = new() { ["name"] = "Farm", ["ar"] = 2, ["he"] = 12, ["colors"] = new JsonObject { ["hair"] = 7 } };
        Assert.True(c.AddLoadout("Farm", farm));
        string sent = Assert.Single(sender.Of("addLoadout"));
        Assert.Equal("""%xt%zm%addLoadout%901%Farm%{"ar":2,"he":12,"colors":{"hair":7}}%%""", sent);
        w.ApplyOutbound(sent);
        Assert.False(c.AddLoadout("Other", farm));   // world.coolDown("addLoadout"): 1500 ms
        Assert.True(w.Apply(Pkt.Json("""{"cmd":"addLoadout","success":true}""")));
        Assert.Equal(new[] { "Rogue Set", "Healer", "Farm" }, w.GetLoadouts()!.Select(p => p.Key));
        Assert.Equal(12, (int)w.GetLoadouts()!["Farm"]!["he"]!);
        Assert.False(w.GetLoadouts()!["Farm"]!.AsObject().ContainsKey("name"));

        // Three sets, three slots: a fourth new one is refused before anything is sent, as the outfit window does.
        time.Advance(TimeSpan.FromSeconds(2));
        sender.Clear();
        Assert.False(c.AddLoadout("Fourth", farm));
        Assert.Empty(sender.Of("addLoadout"));

        // Renaming an existing set replaces it (original name in the third field) and needs no free slot.
        Assert.True(c.AddLoadout("Farm2", new JsonObject { ["ar"] = 2 }, "Farm"));
        w.ApplyOutbound(Assert.Single(sender.Of("addLoadout")));
        Assert.True(w.Apply(Pkt.Json("""{"cmd":"addLoadout","success":1}""")));
        Assert.Equal(new[] { "Rogue Set", "Healer", "Farm2" }, w.GetLoadouts()!.Select(p => p.Key));

        // A refusal changes nothing and is told (MsgBox.notify(resObj.msg)).
        time.Advance(TimeSpan.FromSeconds(2));
        sender.Clear();
        Assert.True(c.AddLoadout("Farm2", new JsonObject { ["ar"] = 50 }, "Farm2"));
        w.ApplyOutbound(Assert.Single(sender.Of("addLoadout")));
        Assert.True(w.Apply(Pkt.Json("""{"cmd":"addLoadout","success":false,"msg":"Invalid outfit."}""")));
        Assert.Equal(2, (int)w.GetLoadouts()!["Farm2"]!["ar"]!);
        Assert.Contains(w.GetRecentMessages(), m => m.Text == "Invalid outfit.");

        Assert.True(c.RemoveLoadout("Healer"));
        Assert.Equal("%xt%zm%removeLoadout%901%Healer%", Assert.Single(sender.Of("removeLoadout")));
        Assert.True(w.Apply(Pkt.Json("""{"cmd":"removeLoadout","success":true,"setName":"Healer"}""")));
        Assert.Equal(new[] { "Rogue Set", "Farm2" }, w.GetLoadouts()!.Select(p => p.Key));
    }

    [Fact]
    public void EquipAndWearSendTheOutfitWindowsRequestWithItsCooldown()
    {
        (GameClient c, _, RecordingSender sender, ManualTime time) = Make();
        Assert.False(c.EquipLoadout(""));
        Assert.True(c.EquipLoadout("Rogue Set"));
        Assert.False(c.EquipLoadout("Rogue Set"));   // equipLoadout cools down 3000 ms
        time.Advance(TimeSpan.FromMilliseconds(3001));
        Assert.True(c.EquipLoadout("Rogue Set", keepColors: false));
        Assert.Equal(new[] { "%xt%zm%equipLoadout%901%cmd%Rogue Set%1%", "%xt%zm%equipLoadout%901%cmd%Rogue Set%0%" }, sender.Of("equipLoadout"));
        Assert.True(c.WearLoadout("Rogue Set"));
        Assert.Equal("%xt%zm%wearLoadout%901%cmd%Rogue Set%1%", Assert.Single(sender.Of("wearLoadout")));

        // A script that sends the packet itself starts the same cooldown.
        time.Advance(TimeSpan.FromSeconds(4));
        c.OnOutbound("%xt%zm%equipLoadout%901%cmd%Healer%1%");
        Assert.False(c.Locks.IsAvailable("equipLoadout"));
    }

    [Fact]
    public void WearLoadoutShowsTheCostumeOverTheEquippedItems()
    {
        WorldModel w = WorldModelTests.LoggedIn();
        Assert.True(w.Apply(Pkt.Json("""{"cmd":"wearItem","uid":40001,"ItemID":1,"sES":"Weapon","sFile":"a.swf","sLink":"A","sType":"Sword"}""")));
        Assert.Equal("1", w.GetGameObject("world.myAvatar.items.0.bWear"));
        Assert.Equal("\"A\"", w.GetGameObject("world.myAvatar.objData.eqp.Weapon.sLink"));

        // Each costume slot loses its shown item first (unwearItemAtES), so a costume without a weapon clears the sword.
        Assert.True(w.Apply(Pkt.Json("""{"cmd":"wearLoadout","uid":40001,"success":1,"costume":{}}""")));
        Assert.Equal("0", w.GetGameObject("world.myAvatar.items.0.bWear"));

        Assert.True(w.Apply(Pkt.Json("""
            {"cmd":"wearLoadout","uid":40001,"success":1,"costume":{"Weapon":{"ItemID":1,"sFile":"undefined","sLink":"Blade","sType":"Sword"}}}
            """)));
        Assert.Equal("1", w.GetGameObject("world.myAvatar.items.0.bWear"));
        Assert.Equal("\"\"", w.GetGameObject("world.myAvatar.objData.eqp.Weapon.sFile"));
        Assert.Equal("\"Blade\"", w.GetGameObject("world.myAvatar.objData.eqp.Weapon.sLink"));

        Assert.True(w.Apply(Pkt.Json("""{"cmd":"unwearItem","uid":40001,"ItemID":1,"sES":"Weapon","bRemove":1}""")));
        Assert.Equal("0", w.GetGameObject("world.myAvatar.items.0.bWear"));
        Assert.Null(w.GetGameObject("world.myAvatar.objData.eqp.Weapon"));

        Assert.True(w.Apply(Pkt.Json("""{"cmd":"buyLoadoutSlots","bitSuccess":1,"iSlots":2}""")));
        Assert.Equal("2", w.GetGameObject("world.myAvatar.objData.iLoadoutSlots"));
        Assert.Equal("-397", w.GetGameObject("world.myAvatar.objData.intCoins"));
    }

    [Fact]
    public async Task TheFakeServerTakesTheGamesRequestsAndEquipsTheSet()
    {
        await using FakeAqwServer server = new();
        server.ExtraInventory.AddRange(new[] { 50, 51 });
        server.Loadouts["Rogue Set"] = JsonNode.Parse("""{"ar":50,"Weapon":51,"colors":{"hair":1}}""");
        await using AqwConnection conn = new(ConnectionTests.Fast());
        await conn.ConnectAsync(server.Server, "Hero", FakeAqwServer.Token, "5.99");
        await ConnectionTests.Until(() => conn.World.HasLoadPrefs && conn.World.IsInventoryLoaded);
        Assert.Equal(new[] { "Rogue Set" }, conn.World.GetLoadouts()!.Select(p => p.Key));

        GameClient client = new(conn.World, new ConnectionSender(conn));
        Assert.True(client.EquipLoadout("Rogue Set"));
        await ConnectionTests.Until(() => conn.World.GetPlayer()?.ClassName == "Rogue");
        await ConnectionTests.Until(() => conn.World.GetInventory().Any(i => i.ItemId == 51 && i.Equipped));
        (string cmd, string[] args, string? violation) = Assert.Single(server.LoadoutRequests);
        Assert.Equal("equipLoadout", cmd);
        Assert.Null(violation);
        Assert.Equal(new[] { "cmd", "Rogue Set", "1" }, args);
    }

    private sealed class ConnectionSender(AqwConnection connection) : IPacketSender
    {
        public bool Send(string packet)
        {
            _ = connection.SendAsync(packet);
            return true;
        }
    }
}
