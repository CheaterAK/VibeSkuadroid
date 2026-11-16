using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.World;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Tests;

internal static class Pkt
{
    public static InboundPacket Json(string json)
    {
        JsonObject o = (JsonObject)JsonNode.Parse(json)!;
        return PacketCodec.Decode(new JsonObject { ["t"] = "xt", ["b"] = new JsonObject { ["r"] = -1, ["o"] = o } }.ToJsonString());
    }

    public static InboundPacket Xt(string cmd, params string[] args)
    {
        return PacketCodec.Decode($"%xt%{cmd}%-1%{string.Join('%', args)}%");
    }

    public static InboundPacket Xml(string text)
    {
        return PacketCodec.Decode(text);
    }
}

public class WorldModelTests
{
    private const string MoveToArea = """
        {"cmd":"moveToArea","areaId":901,"areaName":"testmap-901","strMapName":"testmap","strMapFileName":"test/testmap.swf","intType":1,
         "uoBranch":[
           {"uoName":"hero","strUsername":"Hero","entID":40001,"intHP":1000,"intHPMax":1000,"intMP":100,"intMPMax":100,"intState":1,"intLevel":30,"strFrame":"Enter","strPad":"Spawn","afk":false},
           {"uoName":"other","strUsername":"Other","entID":40002,"intHP":"800","intHPMax":"800","intState":1,"intLevel":12,"strFrame":"r2","strPad":"Left"}],
         "monBranch":[
           {"MonMapID":1,"MonID":5,"intHP":100,"intHPMax":100,"intState":1,"iLvl":3},
           {"MonMapID":2,"MonID":5,"intHP":100,"intHPMax":100,"intState":1,"iLvl":3},
           {"MonMapID":3,"MonID":7,"intHP":400,"intHPMax":400,"intState":1,"iLvl":10}],
         "mondef":[{"MonID":5,"strMonName":"Pixie","intLevel":3,"sRace":"Magical"},{"MonID":6,"strMonName":"Pixie Queen","intLevel":10},{"MonID":7,"strMonName":"Pixie King","intLevel":12}],
         "monmap":[{"MonMapID":1,"MonID":5,"strFrame":"r2"},{"MonMapID":2,"MonID":5,"strFrame":"r2"},{"MonMapID":3,"MonID":6,"strFrame":"r3"},{"MonMapID":3,"MonID":7,"strFrame":"r3"}]}
        """;

    private const string Inventory = """
        {"cmd":"loadInventoryBig","bankCount":1,
         "items":[{"ItemID":1,"CharItemID":5000,"sName":"Sword","sType":"Sword","sES":"Weapon","iQty":"1","iStk":1,"bEquip":1},
                  {"ItemID":30,"CharItemID":5002,"sName":"Old Boots","sType":"Misc","sES":"None","iQty":1,"iStk":1,"bEquip":0},
                  {"ItemID":40,"CharItemID":5004,"sName":"Stack","sType":"Item","sES":"None","iQty":5,"iStk":99,"bEquip":0}],
         "hitems":[{"ItemID":90,"CharItemID":5010,"sName":"Chair","sType":"Floor Item","iQty":1}],
         "factions":[{"FactionID":2,"sName":"Good","iRep":1000}],
         "guild":{"Name":"Testers"}}
        """;

    internal static WorldModel LoggedIn()
    {
        WorldModel w = new();
        Assert.True(w.Apply(Pkt.Xt("loginResponse", "true", "40001", "hero", "Welcome", "")));
        Assert.True(w.Apply(Pkt.Json(MoveToArea)));
        Assert.True(w.Apply(Pkt.Json("""{"cmd":"initUserData","uid":40001,"data":{"strUsername":"Hero","intLevel":30,"intGold":500,"intCoins":3,"intExp":10,"intExpToLevel":1000,"strClassName":"Healer","iCP":3000,"iBagSlots":40,"iBankSlots":20,"iHouseSlots":10,"intAccessLevel":1,"iUpgDays":-1,"intHP":0,"strQuests":"00a0","strQuests2":"0000"}}""")));
        Assert.True(w.Apply(Pkt.Json(Inventory)));
        return w;
    }

    [Fact]
    public void LoginResponseSetsIdentity()
    {
        WorldModel w = new();
        w.Apply(Pkt.Xt("loginResponse", "true", "40001", "Hero", "Welcome", ""));
        Assert.Equal(40001, w.MyUserId);
        Assert.Equal("hero", w.MyUsername);
        Assert.Equal("40001", w.GetGameObject("world.myAvatar.uid"));
        Assert.Equal("40001", w.GetGameObject("sfc.myUserId"));
    }

    [Fact]
    public void MoveToAreaBuildsRoomPlayersAndMonsters()
    {
        WorldModel w = LoggedIn();
        AreaInfo area = w.GetArea();
        Assert.Equal(901, area.RoomId);
        Assert.Equal(901, w.RoomId);
        Assert.Equal("testmap", area.MapName);
        Assert.Equal("testmap-901", area.AreaName);
        Assert.Equal("test/testmap.swf", area.MapFileName);
        Assert.Equal("Enter", area.Cell);
        Assert.Equal(2, area.UserCount);

        IReadOnlyList<AreaUserInfo> users = w.GetAreaUsers();
        AreaUserInfo other = users.Single(u => !u.IsMe);
        Assert.Equal(40002, other.UserId);
        Assert.Equal(800, other.Hp); // numeric string normalised
        Assert.Equal("r2", other.Cell);

        IReadOnlyList<MonsterInfo> monsters = w.GetMonsters();
        Assert.Equal(3, monsters.Count); // slot 3 is a cluster: only the live MonID (7) counts
        MonsterInfo king = monsters.Single(m => m.MonMapId == 3);
        Assert.Equal(7, king.MonId);
        Assert.Equal("Pixie King", king.Name);
        Assert.Equal("r3", king.Cell);
        Assert.Equal(400, king.HpMax);
        Assert.All(monsters, m => Assert.True(m.IsAlive));
    }

    [Fact]
    public void MoveToAreaKeepsOwnAurasAndResetsTheCell()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"aura+","cInf":"p:40001","tInf":"p:40001","auras":[{"nam":"Buff","dur":30,"isNew":true}]}"""));
        w.ApplyOutbound("%xt%zm%moveToCell%901%r2%Left%");
        Assert.Equal("r2", w.GetArea().Cell);
        w.Apply(Pkt.Json("""{"cmd":"moveToArea","areaId":902,"areaName":"shopmap-902","strMapName":"shopmap","uoBranch":[],"monBranch":[],"mondef":[],"monmap":[]}"""));
        Assert.Equal("Enter", w.GetArea().Cell);
        Assert.Single(w.GetMyAuras());
        Assert.Empty(w.GetMonsters());
        Assert.DoesNotContain(w.GetAreaUsers(), u => u.Name == "Other");
    }

    [Fact]
    public void InitUserDataMergesIntoObjDataButNotHitPoints()
    {
        WorldModel w = LoggedIn();
        PlayerInfo me = w.GetPlayer()!;
        Assert.Equal("Hero", me.Username);
        Assert.Equal(30, me.Level);
        Assert.Equal(500, me.Gold);
        Assert.Equal(3, me.Coins);
        Assert.Equal("Healer", me.ClassName);
        Assert.Equal(3000, me.ClassPoints);
        Assert.Equal(1000, me.Hp); // the leaf (moveToArea), not initUserData's intHP 0
        Assert.True(me.CharacterLoaded);
        Assert.True(me.InventoryLoaded);
        Assert.Equal("\"Hero\"", w.GetGameObject("world.myAvatar.objData.strUsername"));
    }

    [Fact]
    public void InitUserDatasStoresOtherPlayers()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"initUserDatas","a":[{"uid":40002,"data":{"strUsername":"Other","intLevel":12,"strClassName":"Rogue"}}]}"""));
        AreaUserInfo other = w.GetAreaUsers().Single(u => u.UserId == 40002);
        Assert.Equal("Rogue", JsonUtil.AsString(other.ObjData!["strClassName"]));
        Assert.Equal("\"Rogue\"", w.GetGameObject("world.avatars.40002.objData.strClassName"));
    }

    [Fact]
    public void UotlsStringAndJsonFormsUpdateTheLeaf()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Xt("uotls", "hero", "intHP:250,intMP:40,strFrame:r3,strPad:Right,afk:true"));
        PlayerInfo me = w.GetPlayer()!;
        Assert.Equal(250, me.Hp);
        Assert.Equal(40, me.Mp);
        Assert.Equal("r3", me.Cell);
        Assert.Equal("Right", me.Pad);
        Assert.True(me.Afk);
        Assert.True(w.IsMeAfk());

        w.Apply(Pkt.Json("""{"cmd":"uotls","unm":"Hero","o":{"intHP":900,"afk":false}}"""));
        Assert.Equal(900, w.GetPlayer()!.Hp);
        Assert.False(w.IsMeAfk());

        w.Apply(Pkt.Xt("uotls", "newcomer", "strFrame:Enter,strPad:Spawn,intHP:50"));
        Assert.Contains(w.GetAreaUsers(), u => u.Name == "newcomer" && u.Hp == 50);
        Assert.Contains("\"newcomer\"", w.GetGameObject("world.areaUsers"));
    }

    [Fact]
    public void MtlsStringAndJsonFormsUpdateMonsters()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Xt("mtls", "1", "intHP:40,intState:2"));
        Assert.Equal(40, w.GetMonster(1)!.Hp);
        Assert.Equal(2, w.GetMonster(1)!.State);
        w.Apply(Pkt.Json("""{"cmd":"mtls","id":1,"o":{"intHP":0,"intState":0}}"""));
        Assert.False(w.GetMonster(1)!.IsAlive);
        w.Apply(Pkt.Xt("respawnMon", "1"));
        Assert.True(w.GetMonster(1)!.IsAlive);
        Assert.Equal(100, w.GetMonster(1)!.Hp);
    }

    [Fact]
    public void CombatPacketUpdatesMonstersPlayersAurasAndResults()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""
            {"cmd":"ct","m":{"2":{"intHP":50,"intState":2}},"p":{"hero":{"intHP":980,"intState":2}},
             "a":[{"cmd":"aura+","cInf":"p:40001","tInf":"m:2","auras":[{"nam":"Judged","dur":5,"val":0.1,"isNew":true}]}],
             "sarsa":[{"cInf":"p:40001","actID":3,"iRes":1,"a":[{"tInf":"m:2","hp":50}]}]}
            """));
        Assert.Equal(50, w.GetMonster(2)!.Hp);
        PlayerInfo me = w.GetPlayer()!;
        Assert.Equal(980, me.Hp);
        Assert.True(me.InCombat);
        AuraInfo aura = Assert.Single(w.GetAuras("m:2"));
        Assert.Equal("Judged", aura.Name);
        Assert.Equal(0.1, aura.Value);
        Assert.Equal("p:40001", aura.Caster);
        Assert.Single(w.GetRecentActionResults());

        w.Apply(Pkt.Json("""{"cmd":"ct","m":{"2":{"intHP":0,"intState":0}}}"""));
        Assert.Empty(w.GetAuras("m:2")); // a dead monster loses its auras
    }

    [Fact]
    public void AuraEventsAddRefreshStackAndRemove()
    {
        WorldModel w = LoggedIn();
        string me = "p:40001";
        w.Apply(Pkt.Json("""{"cmd":"aura+","cInf":"p:40001","tInf":"p:40001","auras":[{"nam":"Focus","dur":10,"val":2,"isNew":true}]}"""));
        w.Apply(Pkt.Json("""{"cmd":"aura+","cInf":"p:40001","tInf":"p:40001","auras":[{"nam":"Focus","dur":12,"val":3}]}"""));
        AuraInfo focus = Assert.Single(w.GetAuras(me));
        Assert.Equal(3, focus.Value);
        Assert.Equal(12, focus.DurationSeconds);
        w.Apply(Pkt.Json("""{"cmd":"aura=","tInf":"p:40001","auras":[{"nam":"Focus","stk":4}]}"""));
        Assert.Equal(4, Assert.Single(w.GetAuras(me)).Stacks);
        w.Apply(Pkt.Json("""{"cmd":"aura+p","cInf":"p:40001","tInf":"p:40001","auras":[{"nam":"Passive"}]}"""));
        Assert.Contains(w.GetAuras(me), a => a.Passive && a.Name == "Passive");
        w.Apply(Pkt.Json("""{"cmd":"aura-","tInf":"p:40001","aura":{"nam":"Focus"}}"""));
        Assert.DoesNotContain(w.GetAuras(me), a => a.Name == "Focus");
        w.Apply(Pkt.Json("""{"cmd":"aura+","tInf":"p:40001","auras":[{"nam":"Keep","persist":true},{"nam":"Go"}]}"""));
        w.Apply(Pkt.Json("""{"cmd":"clearAuras"}"""));
        Assert.Equal(new[] { "Keep" }, w.GetAuras(me).Where(a => !a.Passive).Select(a => a.Name));
    }

    [Fact]
    public void LoadInventoryBigFillsEveryBag()
    {
        WorldModel w = LoggedIn();
        Assert.True(w.IsInventoryLoaded);
        IReadOnlyList<ItemInfo> items = w.GetInventory();
        Assert.Equal(3, items.Count);
        ItemInfo sword = items.Single(i => i.ItemId == 1);
        Assert.Equal(1, sword.Quantity); // "1" normalised
        Assert.True(sword.Equipped);
        Assert.Equal("Chair", Assert.Single(w.GetHouseInventory()).Name);
        FactionInfo good = Assert.Single(w.GetFactions());
        Assert.Equal(1000, good.Rep);
        Assert.Equal("\"Testers\"", w.GetGameObject("world.myAvatar.objData.guild.Name"));
        Assert.Equal("1", w.GetGameObject("world.myAvatar.iBankCount"));
    }

    [Fact]
    public void AddItemsRoutesToBackpackTempAndBank()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"addItems","items":{"10":{"ItemID":10,"sName":"Pixie Dust","iQty":1,"iQtyNow":1,"bTemp":1,"iStk":10}}}"""));
        w.Apply(Pkt.Json("""{"cmd":"addItems","items":{"10":{"ItemID":10,"sName":"Pixie Dust","iQty":1,"iQtyNow":2,"bTemp":1,"iStk":10}}}"""));
        Assert.Equal(2, Assert.Single(w.GetTempInventory()).Quantity);

        w.Apply(Pkt.Json("""{"cmd":"addItems","items":{"40":{"ItemID":40,"CharItemID":5004,"iQty":2}}}""")); // stack update, no name
        ItemInfo stack = w.GetInventory().Single(i => i.ItemId == 40);
        Assert.Equal(7, stack.Quantity);
        Assert.Equal("Stack", stack.Name);

        w.Apply(Pkt.Json("""{"cmd":"addItems","items":{"50":{"ItemID":50,"CharItemID":5050,"sName":"Banked","iQty":1,"bBank":true}}}"""));
        Assert.Equal("Banked", Assert.Single(w.GetBank()).Name);
    }

    [Fact]
    public void DropsAccumulateAndGetDropMovesThemToTheBackpack()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"dropItem","items":{"11":{"ItemID":11,"sName":"Pixie Wing","iQty":1,"iStk":99}}}"""));
        w.Apply(Pkt.Json("""{"cmd":"dropItem","items":{"11":{"ItemID":11,"sName":"Pixie Wing","iQty":1,"iStk":99}}}"""));
        DropInfo drop = Assert.Single(w.GetDrops());
        Assert.Equal(2, drop.Quantity);
        Assert.Equal(2, drop.TimesOffered);
        Assert.Contains("Pixie Wing", w.GetGameObject("headless.drops"));

        w.Apply(Pkt.Json("""{"cmd":"getDrop","bSuccess":0,"ItemID":11}"""));
        Assert.Single(w.GetDrops());
        w.Apply(Pkt.Json("""{"cmd":"getDrop","bSuccess":1,"ItemID":11,"CharItemID":6000,"iQty":1,"iQtyNow":1,"bBank":false}"""));
        Assert.Empty(w.GetDrops());
        ItemInfo wing = w.GetInventory().Single(i => i.ItemId == 11);
        Assert.Equal("Pixie Wing", wing.Name);
        Assert.Equal(6000, wing.CharItemId);
        Assert.Equal(1, wing.Quantity);
    }

    [Fact]
    public void QuestsLoadAcceptAndComplete()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"getQuests","quests":{"1000":{"QuestID":1000,"sName":"Pixie Problem","iSlot":-1,"iLvl":1,"bOnce":0}}}"""));
        QuestInfo q = w.GetQuest(1000)!;
        Assert.True(q.Loaded);
        Assert.False(q.Accepted);
        Assert.Null(q.Status);
        Assert.Equal("\"Pixie Problem\"", w.GetGameObject("world.questTree.1000.sName"));

        w.Apply(Pkt.Json("""{"cmd":"acceptQuest","QuestID":1000,"bSuccess":1}"""));
        q = w.GetQuest(1000)!;
        Assert.True(q.Accepted);
        Assert.Equal("p", q.Status);
        Assert.Equal(new[] { 1000 }, w.GetAcceptedQuestIds());

        w.Apply(Pkt.Json("""{"cmd":"ccqr","QuestID":1000,"bSuccess":0,"msg":"Missing turn in item."}"""));
        Assert.True(w.GetQuest(1000)!.Accepted);
        w.Apply(Pkt.Json("""{"cmd":"ccqr","QuestID":1000,"bSuccess":1}"""));
        Assert.False(w.GetQuest(1000)!.Accepted);
        Assert.Null(w.GetQuest(1000)!.Status);
    }

    [Fact]
    public void AcceptBeforeLoadIsKeptAndRefusalClears()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"acceptQuest","QuestID":2000,"bSuccess":1}"""));
        QuestInfo pending = w.GetQuest(2000)!;
        Assert.False(pending.Loaded);
        Assert.True(pending.Accepted);
        w.Apply(Pkt.Json("""{"cmd":"getQuests","quests":{"2000":{"QuestID":2000,"sName":"Later"}}}"""));
        Assert.Equal("p", w.GetQuest(2000)!.Status);
        w.Apply(Pkt.Json("""{"cmd":"acceptQuest","QuestID":2000,"bSuccess":0}"""));
        Assert.False(w.GetQuest(2000)!.Accepted);
    }

    [Fact]
    public void TurnInRemovesItemsAndUpdateQuestIsRecordedRaw()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"addItems","items":{"10":{"ItemID":10,"sName":"Pixie Dust","iQty":3,"iQtyNow":3,"bTemp":1}}}"""));
        w.Apply(Pkt.Json("""{"cmd":"turnIn","sItems":"10:3,40:2"}"""));
        Assert.Empty(w.GetTempInventory());
        Assert.Equal(3, w.GetInventory().Single(i => i.ItemId == 40).Quantity);

        w.Apply(Pkt.Json("""{"cmd":"updateQuest","iIndex":3,"iValue":7}"""));
        Assert.Equal(7, w.GetQuestValueUpdates()[3]);
        Assert.Equal("00a0", w.GetQuestStrings()["strQuests"]); // raw, not decoded (next phase)
    }

    [Fact]
    public void GoldExpLevelAndBalance()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"addGoldExp","typ":"m","id":1,"intExp":10,"intGold":5,"iCP":2,"FactionID":2,"iRep":50}"""));
        PlayerInfo me = w.GetPlayer()!;
        Assert.Equal(505, me.Gold);
        Assert.Equal(20, me.Exp);
        Assert.Equal(3002, me.ClassPoints);
        Assert.Equal(1050, w.GetFactions().Single().Rep);
        w.Apply(Pkt.Json("""{"cmd":"levelUp","intLevel":31,"intExpToLevel":2000,"xp":0}"""));
        Assert.Equal(31, w.GetPlayer()!.Level);
        Assert.Equal(2000, w.GetPlayer()!.ExpToLevel);
        w.Apply(Pkt.Json("""{"cmd":"balance","intGold":1234,"intCoins":9}"""));
        Assert.Equal(1234, w.GetPlayer()!.Gold);
        Assert.Equal(9, w.GetPlayer()!.Coins);
    }

    [Fact]
    public void SkillsStatsAndClass()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"sAct","actions":{"active":[{"ref":"aa","nam":"Auto attack","typ":"aa","tgt":"h","cd":2000,"mp":0,"auto":true},{"ref":"a1","nam":"Heal","typ":"m","tgt":"f","cd":3000,"mp":10}],"passive":[{"ref":"p1","nam":"Passive"}]}}"""));
        IReadOnlyList<SkillInfo> skills = w.GetSkills();
        Assert.Equal(3, skills.Count);
        Assert.True(skills[0].Auto);
        Assert.Equal(3000, skills[1].CooldownMs);
        Assert.True(skills[2].Passive);
        Assert.Null(skills[1].LastSent);
        w.ApplyOutbound("%xt%zm%gar%1%1%a1>p:40001%wvz%");
        Assert.NotNull(w.GetSkills()[1].LastSent);

        w.Apply(Pkt.Json("""{"cmd":"stu","sta":{"$tha":0.2,"$STR":20},"wDPS":50}"""));
        Assert.Equal(0.2, JsonUtil.AsDoubleOrNull(w.GetStats()!["$tha"]));
        Assert.Equal("0.2", w.GetGameObject("world.myAvatar.dataLeaf.sta.$tha"));

        w.Apply(Pkt.Json("""{"cmd":"updateClass","uid":40001,"sClassName":"Rogue","iCP":10}"""));
        Assert.Equal("Rogue", w.GetPlayer()!.ClassName);
        w.Apply(Pkt.Json("""{"cmd":"updateClass","uid":40002,"sClassName":"Mage"}"""));
        Assert.Equal("Rogue", w.GetPlayer()!.ClassName);
    }

    [Fact]
    public void EquipAndUnequip()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"addItems","items":{"60":{"ItemID":60,"CharItemID":5060,"sName":"Axe","sType":"Axe","sES":"Weapon","iQty":1,"bTemp":0}}}"""));
        w.Apply(Pkt.Json("""{"cmd":"equipItem","uid":40001,"ItemID":60,"strES":"Weapon","sFile":"axe.swf","sLink":"axe","sType":"Axe"}"""));
        Assert.True(w.GetInventory().Single(i => i.ItemId == 60).Equipped);
        Assert.False(w.GetInventory().Single(i => i.ItemId == 1).Equipped);
        Assert.Equal("\"axe.swf\"", w.GetGameObject("world.myAvatar.objData.eqp.Weapon.sFile"));
        w.Apply(Pkt.Json("""{"cmd":"unequipItem","uid":40001,"ItemID":60,"strES":"Weapon"}"""));
        Assert.False(w.GetInventory().Single(i => i.ItemId == 60).Equipped);
        Assert.Null(w.GetGameObject("world.myAvatar.objData.eqp.Weapon"));
    }

    [Fact]
    public void BankLoadAndMoves()
    {
        WorldModel w = LoggedIn();
        Assert.False(w.IsBankLoaded);
        w.Apply(Pkt.Json("""{"cmd":"loadBank","bitSuccess":1,"items":[{"ItemID":11,"CharItemID":5003,"sName":"Pixie Wing","iQty":"2","bCoins":0}]}"""));
        Assert.True(w.IsBankLoaded);
        Assert.Equal(2, Assert.Single(w.GetBank()).Quantity);
        w.Apply(Pkt.Json("""{"cmd":"loadBank","bitSuccess":1,"items":[{"ItemID":11,"CharItemID":5003,"sName":"Pixie Wing","iQty":3}]}"""));
        Assert.Equal(3, Assert.Single(w.GetBank()).Quantity); // replaced, not duplicated

        w.Apply(Pkt.Json("""{"cmd":"bankToInv","ItemID":11,"CharItemID":5003,"bSuccess":1}"""));
        Assert.Empty(w.GetBank());
        Assert.Contains(w.GetInventory(), i => i.ItemId == 11);
        w.Apply(Pkt.Json("""{"cmd":"bankFromInv","ItemID":30,"CharItemID":5002,"bSuccess":1}"""));
        Assert.Contains(w.GetBank(), i => i.ItemId == 30);
        Assert.DoesNotContain(w.GetInventory(), i => i.ItemId == 30);
    }

    [Fact]
    public void ShopBuySellAndRemove()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"loadShop","shopinfo":{"ShopID":100,"sName":"Test Shop","items":[{"ItemID":20,"ShopItemID":7001,"sName":"Health Potion","iCost":5,"bCoins":0,"iStk":50}]}}"""));
        ShopInfo shop = w.GetShop()!;
        Assert.Equal(100, shop.ShopId);
        Assert.Equal(7001, Assert.Single(shop.Items).ShopItemId);
        Assert.Equal("\"Test Shop\"", w.GetGameObject("world.shopinfo.sName"));

        w.Apply(Pkt.Json("""{"cmd":"buyItem","bitSuccess":1,"ItemID":20,"CharItemID":7777,"iQty":2}"""));
        ItemInfo potion = w.GetInventory().Single(i => i.ItemId == 20);
        Assert.Equal("Health Potion", potion.Name);
        Assert.Equal(2, potion.Quantity);

        w.Apply(Pkt.Json("""{"cmd":"sellItem","CharItemID":7777,"iQty":1,"iQtyNow":1,"intAmount":2,"bCoins":0}"""));
        Assert.Equal(1, w.GetInventory().Single(i => i.ItemId == 20).Quantity);
        Assert.Equal(502, w.GetPlayer()!.Gold);

        w.Apply(Pkt.Json("""{"cmd":"removeItem","CharItemID":7777,"iQty":1}"""));
        Assert.DoesNotContain(w.GetInventory(), i => i.ItemId == 20);
    }

    [Fact]
    public void FactionsLoadAndAdd()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Json("""{"cmd":"loadFactions","factions":[{"FactionID":3,"sName":"Evil","iRep":5}]}"""));
        w.Apply(Pkt.Json("""{"cmd":"addFaction","faction":{"FactionID":4,"sName":"Chaos","iRep":0}}"""));
        Assert.Equal(new[] { "Evil", "Chaos" }, w.GetFactions().Select(f => f.Name));
    }

    [Fact]
    public void PlayersEnterAndLeave()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Xml("<msg t='sys'><body action='uER' r='901'><u i='40003' m='0'><n><![CDATA[Visitor]]></n></u></body></msg>"));
        Assert.Contains(w.GetAreaUsers(), u => u.Name == "visitor" && u.UserId == 40003);
        w.Apply(Pkt.Xml("<msg t='sys'><body action='userGone' r='901'><user id='40003' /></body></msg>"));
        Assert.DoesNotContain(w.GetAreaUsers(), u => u.Name == "visitor");
        w.Apply(Pkt.Xt("exitArea", "40002", "other"));
        Assert.DoesNotContain(w.GetAreaUsers(), u => u.UserId == 40002);
        w.Apply(Pkt.Xt("exitArea", "40001", "hero"));
        Assert.Contains(w.GetAreaUsers(), u => u.IsMe); // never removes ourselves
    }

    [Fact]
    public void ServerMovesAndMonsterSwapsAreRecorded()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Xt("gtc", "r5", "Left"));
        Assert.Equal(("r5", "Left"), (w.GetPendingServerMove()!.Cell, w.GetPendingServerMove()!.Pad));
        w.Apply(Pkt.Xt("spcs", "1", "6"));
        Assert.Equal(6, w.GetMonster(1)!.MonId);
        w.Apply(Pkt.Json("""{"cmd":"event","args":{"zoneSet":"A"}}"""));
        Assert.Equal("A", w.Zone);
    }

    [Fact]
    public void MessagesAreKept()
    {
        WorldModel w = LoggedIn();
        w.Apply(Pkt.Xt("warning", "Please slow down. Last action was too soon!"));
        w.Apply(Pkt.Xt("chatm", "zone~hello there", "Other"));
        IReadOnlyList<ServerMessageInfo> messages = w.GetRecentMessages();
        Assert.Equal("Please slow down. Last action was too soon!", messages[0].Text);
        Assert.Equal(("hello there", "Other"), (messages[1].Text.Split('~')[1], messages[1].Sender));
    }

    [Fact]
    public void UnhandledPacketsAreCountedSampledAndRaised()
    {
        WorldModel w = LoggedIn();
        List<InboundPacket> raised = new();
        w.UnhandledPacket += (_, p) => raised.Add(p);
        Assert.False(w.Apply(Pkt.Xt("mysteryCmd", "1", "2")));
        Assert.False(w.Apply(Pkt.Xt("mysteryCmd", "3")));
        Assert.False(w.Apply(Pkt.Json("""{"cmd":"brandNewThing","x":1}""")));
        Assert.False(w.Apply(PacketCodec.Decode("Zgarbage!!")));
        PacketCounters counters = w.GetPacketCounters();
        Assert.Equal(2, counters.Unhandled["xt:mysteryCmd"]);
        Assert.Equal(1, counters.Unhandled["json:brandNewThing"]);
        Assert.Equal(1, counters.Unhandled["undecodable"]);
        Assert.Contains("mysteryCmd", counters.UnhandledSamples["xt:mysteryCmd"]);
        Assert.Equal(1, counters.Handled["json:moveToArea"]);
        Assert.Equal(4, raised.Count);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        WorldModel w = LoggedIn();
        w.Reset();
        Assert.Null(w.GetPlayer());
        Assert.Empty(w.GetInventory());
        Assert.Empty(w.GetMonsters());
        Assert.Equal(1, w.RoomId);
    }
}

public class WorldPathTests
{
    [Fact]
    public void ResolvesTheSamePathsSkuaCoreBinds()
    {
        WorldModel w = WorldModelTests.LoggedIn();
        Assert.Equal("\"Hero\"", w.GetGameObject("world.myAvatar.objData.strUsername"));
        Assert.Equal("500", w.GetGameObject("world.myAvatar.objData.intGold"));
        Assert.Equal("1", w.GetGameObject("world.myAvatar.dataLeaf.intState"));
        Assert.Equal("false", w.GetGameObject("world.myAvatar.dataLeaf.afk"));
        Assert.Equal("\"testmap\"", w.GetGameObject("world.strMapName"));
        Assert.Equal("\"Enter\"", w.GetGameObject("world.strFrame"));
        Assert.Equal("901", w.GetGameObject("world.curRoom"));
        Assert.Equal("40", w.GetGameObject("world.myAvatar.objData.iBagSlots"));
        Assert.Equal("[]", w.GetGameObject("world.bankinfo.items"));
        Assert.Equal("true", w.GetGameObject("headless.inventoryLoaded"));
        Assert.StartsWith("[", w.GetGameObject("world.myAvatar.items"));
        Assert.Equal("\"Sword\"", w.GetGameObject("world.myAvatar.items.0.sName"));
        Assert.Equal("\"Sword\"", w.GetGameObject("world.myAvatar.items[0].sName"));
        Assert.Equal("\"Pixie\"", w.GetGameObject("world.monsters.0.objData.strMonName"));
        Assert.Equal("100", w.GetGameObject("world.monsters.0.dataLeaf.intHP"));
        Assert.Equal("\"r2\"", w.GetGameObject("world.monTree.1.strFrame"));
        Assert.Equal("1000", w.GetGameObject("world.uoTree.hero.intHP"));
        Assert.Equal("false", w.GetGameObject("sfc.isConnected"));
    }

    [Fact]
    public void MissingPathsAreNullAndNullValuesAreJsonNull()
    {
        WorldModel w = WorldModelTests.LoggedIn();
        Assert.Null(w.GetGameObject("world.nothing.here"));
        Assert.Null(w.GetGameObject("nowhere"));
        Assert.Null(w.GetGameObject("world.myAvatar.items.99"));
        Assert.Equal("null", w.GetGameObject("world.shopinfo"));
        Assert.True(w.IsNull("world.shopinfo"));
        Assert.True(w.IsNull("world.nothing"));
        Assert.False(w.IsNull("world.myAvatar"));
    }

    [Fact]
    public void WholeObjectsIncludeVirtualMembers()
    {
        WorldModel w = WorldModelTests.LoggedIn();
        Assert.True(w.TryGetGameObjectNode("world.myAvatar", out JsonNode? avatar));
        Assert.Equal(1000, (int)avatar!["dataLeaf"]!["intHP"]!);
        Assert.True(w.TryGetGameObjectNode("world", out JsonNode? world));
        Assert.Equal(3, ((JsonArray)world!["monsters"]!).Count);
    }

    [Fact]
    public void ReturnedNodesAreDetachedCopies()
    {
        WorldModel w = WorldModelTests.LoggedIn();
        Assert.True(w.TryGetGameObjectNode("world.myAvatar.objData", out JsonNode? objData));
        objData!["strUsername"] = "Mallory";
        w.GetPlayer()!.ObjData["intGold"] = 0;
        Assert.Equal("\"Hero\"", w.GetGameObject("world.myAvatar.objData.strUsername"));
        Assert.Equal(500, w.GetPlayer()!.Gold);
    }

    [Fact]
    public async Task ConcurrentReadsDuringWritesAreSafe()
    {
        WorldModel w = WorldModelTests.LoggedIn();
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(1));
        Task writer = Task.Run(() =>
        {
            int hp = 0;
            while (!stop.IsCancellationRequested)
            {
                w.Apply(Pkt.Xt("uotls", "hero", $"intHP:{hp++ % 1000}"));
                w.Apply(Pkt.Json("""{"cmd":"addItems","items":{"40":{"ItemID":40,"CharItemID":5004,"iQty":1}}}"""));
                w.Apply(Pkt.Xt("mtls", "1", $"intHP:{hp % 100}"));
            }
        });
        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Assert.NotNull(w.GetPlayer());
                Assert.NotEmpty(w.GetInventory());
                Assert.NotNull(w.GetGameObject("world.myAvatar.dataLeaf.intHP"));
                Assert.NotNull(w.GetGameObject("world"));
                Assert.NotEmpty(w.GetMonsters());
            }
        })).ToArray();
        await Task.WhenAll(readers.Append(writer));
        Assert.True(w.Version > 100);
    }
}
