using Skua.Core.Headless.Client;
using Skua.Core.Headless.Maps;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Headless.World;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Tests;

internal sealed class RecordingSender : IPacketSender
{
    public List<string> Sent { get; } = new();

    public bool Send(string packet)
    {
        lock (Sent)
            Sent.Add(packet);
        return true;
    }

    public List<string> Of(string cmd)
    {
        lock (Sent)
            return Sent.Where(p => Packets.CommandOf(p) == cmd).ToList();
    }

    public void Clear()
    {
        lock (Sent)
            Sent.Clear();
    }
}

public class GameClientTests
{
    private const string SkillBar = """
        {"cmd":"sAct","actions":{"active":[
          {"ref":"aa","nam":"Auto attack","typ":"aa","tgt":"h","cd":2000,"mp":0,"auto":true,"isOK":true},
          {"ref":"a1","nam":"Heal","typ":"m","tgt":"f","cd":3000,"mp":10,"isOK":true},
          {"ref":"a2","nam":"Smite","typ":"m","tgt":"h","cd":4000,"mp":15,"isOK":true},
          {"ref":"a3","nam":"Big","typ":"m","tgt":"h","cd":4000,"mp":500,"isOK":true},
          {"ref":"i1","nam":"Potion","typ":"i","tgt":"s","cd":30000,"isOK":true}],"passive":[{"ref":"p1","nam":"Passive"}]}}
        """;

    private static (GameClient Client, WorldModel World, RecordingSender Sender, ManualTime Time) Make(MapCellCache? maps = null)
    {
        ManualTime time = new();
        WorldModel w = WorldModelTests.LoggedIn();
        RecordingSender sender = new();
        GameClient c = new(w, sender, time, maps, new GameClientOptions { MapLoadTime = TimeSpan.FromMilliseconds(500) });
        Feed(c, w, Pkt.Json(SkillBar));
        Feed(c, w, Pkt.Json("""{"cmd":"stu","sta":{"$tha":0,"$cmc":1}}"""));
        return (c, w, sender, time);
    }

    private static void Feed(GameClient c, WorldModel w, InboundPacket p)
    {
        w.Apply(p);
        c.OnInbound(p);
    }

    private static void ToCell(GameClient c, WorldModel w, string cell)
    {
        c.MoveToCell(cell, "Left");
    }

    [Fact]
    public void SkillBarKeepsThePotionSlotAcrossClassChanges()
    {
        (GameClient c, WorldModel w, _, _) = Make();
        JsonArray active = (JsonArray)c.ActionsJson()["active"]!;
        Assert.Equal(new[] { "aa", "a1", "a2", "a3", null, "i1" }, active.Select(a => (string?)a?["ref"]));
        Assert.Equal(0, (int)active[0]!["ts"]!);
        Assert.False((bool)active[0]!["lock"]!);
        Assert.Single((JsonArray)c.ActionsJson()["passive"]!);

        Feed(c, w, Pkt.Json("""{"cmd":"updateClass","uid":40001,"sClassName":"Rogue","iCP":10}"""));
        Feed(c, w, Pkt.Json("""{"cmd":"sAct","actions":{"active":[{"ref":"aa","nam":"Stab","typ":"aa","tgt":"h","cd":2000,"auto":true,"isOK":true},{"ref":"a1","nam":"Cut","typ":"m","tgt":"h","cd":3000,"isOK":false}]}}"""));
        active = (JsonArray)c.ActionsJson()["active"]!;
        Assert.Equal("Stab", (string?)active[0]!["nam"]);
        Assert.Equal("i1", (string?)active[5]!["ref"]);
        Assert.Null(active[2]);

        // Same class: only newly unlocked skills change.
        Feed(c, w, Pkt.Json("""{"cmd":"sAct","actions":{"active":[{"ref":"aa","nam":"Other","typ":"aa","tgt":"h","cd":2000,"auto":true,"isOK":true},{"ref":"a1","nam":"Cut","typ":"m","tgt":"h","cd":3000,"isOK":true}]}}"""));
        active = (JsonArray)c.ActionsJson()["active"]!;
        Assert.Equal("Stab", (string?)active[0]!["nam"]);
        Assert.True((bool)active[1]!["isOK"]!);
    }

    [Fact]
    public void AttackTargetsTheWeakestLiveMonsterAndStartsTheAutoAttack()
    {
        (GameClient c, WorldModel w, RecordingSender s, ManualTime time) = Make();
        Assert.False(c.AttackMonster("Pixie"));   // none in Enter
        ToCell(c, w, "r2");
        Feed(c, w, Pkt.Json("""{"cmd":"mtls","id":1,"o":{"intHP":90}}"""));
        Assert.True(c.AttackMonster("pix"));
        Assert.Equal(new TargetRef('m', 1, null), c.Target);
        Assert.Equal("%xt%zm%gar%1%0%aa>m:1%wvz%", Assert.Single(s.Of("gar")));
        Assert.True(c.AutoAttacking);

        // The server answers the swing: the next one is armed for the hasted cooldown since the send.
        time.Advance(TimeSpan.FromMilliseconds(300));
        Feed(c, w, Pkt.Json("""{"cmd":"ct","sarsa":[{"cInf":"p:40001","actID":0,"iRes":1,"a":[{"tInf":"m:1","hp":50}]}],"m":{"1":{"intHP":40,"intState":2}}}"""));
        time.Advance(TimeSpan.FromMilliseconds(1850));
        Assert.Single(s.Of("gar"));   // at least cd - 100 ms after the answer (World.showActionResult)
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, s.Of("gar").Count);
        Assert.Equal("%xt%zm%gar%1%1%aa>m:1%wvz%", s.Of("gar")[1]);

        // The target dies: getTargetMonster answers {} and drops it; the auto attack stops.
        Feed(c, w, Pkt.Json("""{"cmd":"ct","m":{"1":{"intHP":0,"intState":0}}}"""));
        Assert.Empty(c.TargetMonsterJson());   // first World.cancelTarget stops the auto attack
        Assert.Empty(c.TargetMonsterJson());   // the next drops the target
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(c.Target);
        Assert.False(c.AutoAttacking);
    }

    [Fact]
    public void SkillsRespectGlobalCooldownManaAndTargets()
    {
        (GameClient c, WorldModel w, RecordingSender s, ManualTime time) = Make();
        Assert.False(c.CanUseSkill(2));   // no target
        ToCell(c, w, "r2");
        Assert.True(c.SelectMonster(2));
        Assert.True(c.CanUseSkill(2));
        Assert.True(c.UseSkill(2));
        Assert.Contains("%xt%zm%gar%1%0%a2>m:2%wvz%", s.Of("gar"));
        Assert.Contains(s.Of("gar"), g => g.Contains("aa>m:2", StringComparison.Ordinal));   // a hostile skill starts the auto attack
        Assert.False(c.CanUseSkill(1));   // global cooldown
        time.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.False(c.CanUseSkill(2));   // its own 4 s cooldown
        Assert.True(c.CanUseSkill(1));

        // Heal targets a friend: with a monster targeted it falls back to the player.
        s.Clear();
        Assert.True(c.UseSkill(1));
        Assert.Equal("%xt%zm%gar%1%2%a1>p:40001%wvz%", Assert.Single(s.Of("gar")));

        // Not enough mana: nothing is sent.
        time.Advance(TimeSpan.FromSeconds(5));
        s.Clear();
        Assert.True(c.UseSkill(3));
        Assert.Empty(s.Of("gar"));

        // The potion slot needs an item.
        Assert.True(c.UseSkill(5));
        Assert.Empty(s.Of("gar"));
    }

    [Fact]
    public void TooSoonRestoresTheCooldown()
    {
        (GameClient c, WorldModel w, RecordingSender s, ManualTime time) = Make();
        ToCell(c, w, "r2");
        c.SelectMonster(1);
        Assert.True(c.UseSkill(2));
        Assert.False(c.CanUseSkill(2));
        Feed(c, w, Pkt.Xt("warning", "Please slow down. Last action was too soon!"));
        // The lock from the refused send is released and its time stamp restored; only the old GCD remains.
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(c.CanUseSkill(2));
    }

    [Fact]
    public void ViewsDecorateQuestValuesRanksFactionsAndQuestStatus()
    {
        (GameClient c, WorldModel w, _, _) = Make();
        Feed(c, w, Pkt.Json("""{"cmd":"updateQuest","iIndex":1,"iValue":12}"""));
        JsonObject od = c.DecorateObjData((JsonObject)w.Read((t, _) => t["myAvatar"]!["objData"]!.DeepClone()));
        Assert.Equal("0ca0", (string?)od["strQuests"]);
        Assert.Equal(12, c.GetQuestValue(1));
        Assert.Equal(2, (int)od["iRank"]!);          // 3000 class points
        Assert.Equal(2100, (long)od["iCurCP"]!);
        Assert.Equal(2700, (long)od["iCPToRank"]!);

        JsonArray factions = GameClient.DecorateFactions(new JsonArray(new JsonObject { ["FactionID"] = 2, ["iRep"] = 1000 }));
        Assert.Equal(2, (int)factions[0]!["iRank"]!);
        Assert.Equal(100, (long)factions[0]!["iSpillRep"]!);

        Feed(c, w, Pkt.Json("""{"cmd":"getQuests","quests":{"1000":{"QuestID":1000,"sName":"Q","turnin":[{"ItemID":40,"iQty":5}]},"1001":{"QuestID":1001,"sName":"R","turnin":[{"ItemID":40,"iQty":6}]}}}"""));
        Feed(c, w, Pkt.Json("""{"cmd":"acceptQuest","QuestID":1000,"bSuccess":1}"""));
        Feed(c, w, Pkt.Json("""{"cmd":"acceptQuest","QuestID":1001,"bSuccess":1}"""));
        JsonObject tree = c.DecorateQuestTree((JsonObject)w.Read((t, _) => t["questTree"]!.DeepClone()));
        Assert.Equal("c", (string?)tree["1000"]!["status"]);
        Assert.Equal("p", (string?)tree["1001"]!["status"]);
        Assert.Equal(1, c.MaximumQuestTurnIns(1000));
        Assert.True(c.IsQuestInProgress(1000));
    }

    [Fact]
    public void QuestRequestsAreNotRepeatedWhilePending()
    {
        (GameClient c, WorldModel w, RecordingSender s, ManualTime time) = Make();
        c.ShowQuests("2000,2001");
        c.ShowQuests("2000");
        Assert.Equal(new[] { "%xt%zm%getQuests%901%2000%2001%" }, s.Of("getQuests"));
        c.AcceptQuest(2002);
        Assert.Equal("%xt%zm%getQuests%901%2002%", s.Of("getQuests")[1]);
        Assert.Equal("%xt%zm%acceptQuest%901%2002%", Assert.Single(s.Of("acceptQuest")));
        time.Advance(TimeSpan.FromSeconds(9));
        c.ShowQuests("2000");
        Assert.Equal(3, s.Of("getQuests").Count);
        c.TryQuestComplete(1000, -1, false, 2);
        Assert.Equal("%xt%zm%tryQuestComplete%901%1000%-1%false%2%wvz%", Assert.Single(s.Of("tryQuestComplete")));
    }

    [Fact]
    public void ServerMovesAreFollowedAndMapLoadingClears()
    {
        (GameClient c, WorldModel w, RecordingSender s, ManualTime time) = Make();
        Feed(c, w, Pkt.Xt("gtc", "r3", "Right"));
        Assert.Equal("%xt%zm%moveToCell%901%r3%Right%", Assert.Single(s.Of("moveToCell")));
        Assert.Equal("r3", w.GetArea().Cell);

        Feed(c, w, Pkt.Xt("resTimed", "", ""));
        Assert.Equal("%xt%zm%moveToCell%901%Enter%Spawn%", s.Of("moveToCell")[1]);

        Assert.False(c.MapLoadInProgress);
        Feed(c, w, Pkt.Json("""{"cmd":"moveToArea","areaId":902,"areaName":"other-902","strMapName":"other","uoBranch":[],"monBranch":[],"mondef":[],"monmap":[]}"""));
        Assert.True(c.MapLoadInProgress);
        time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(c.MapLoadInProgress);
    }

    [Fact]
    public async Task CellsComeFromTheMapFile()
    {
        MapCellCache maps = new((_, _) => Task.FromResult(SyntheticSwf.Map(new[] { ("Enter", new[] { "Spawn" }), ("r2", new[] { "Left" }) })), null);
        (GameClient c, WorldModel w, RecordingSender s, _) = Make(maps);
        Feed(c, w, Pkt.Json("""{"cmd":"moveToArea","areaId":902,"areaName":"other-902","strMapName":"other","strMapFileName":"x/other.swf","uoBranch":[],"monBranch":[],"mondef":[],"monmap":[]}"""));
        for (int i = 0; i < 50 && c.Cells is null; i++)
            await Task.Delay(20);
        Assert.Equal(new[] { "Enter", "r2" }, c.Cells!.Cells);
        c.JumpCorrectRoom("r2", "Bottom");
        Assert.EndsWith("%r2%Left%", s.Of("moveToCell").Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShopChecksAndThrottles()
    {
        (GameClient c, WorldModel w, RecordingSender s, ManualTime time) = Make();
        Feed(c, w, Pkt.Json("""{"cmd":"loadShop","shopinfo":{"ShopID":7,"sName":"S","items":[{"ItemID":300,"ShopItemID":1,"sName":"Cheap","iCost":10,"bCoins":0,"iQty":1,"iStk":5},{"ItemID":301,"ShopItemID":2,"sName":"Dear","iCost":100000,"bCoins":0}]}}"""));
        Assert.Null(c.BuyItem(c.ShopItem("cheap")));
        Assert.Equal("%xt%zm%buyItem%901%300%7%1%", Assert.Single(s.Of("buyItem")));
        Assert.Equal("Buying too fast.", c.BuyItem(c.ShopItem("Cheap")));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("Not enough gold.", c.BuyItem(c.ShopItem(301, -1)));
        c.LoadShop(7);   // already loaded
        Assert.Empty(s.Of("loadShop"));
        c.LoadShop(8);
        c.LoadShop(9);   // throttled
        Assert.Single(s.Of("loadShop"));
        Assert.Equal(5, c.MaximumShopBuys(c.ShopItem(300, 1)));
    }

    [Fact]
    public void RestAndBankFollowTheClient()
    {
        (GameClient c, WorldModel w, RecordingSender s, ManualTime time) = Make();
        c.Rest();
        c.Rest();
        Assert.Single(s.Of("restRequest"));
        c.ToggleBank();
        Assert.Equal("Bank", c.PopupLabel);
        Assert.Single(s.Of("loadBank"));
        c.ToggleBank();
        Assert.Equal("Init", c.PopupLabel);
    }
}
