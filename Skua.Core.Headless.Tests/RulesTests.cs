using Skua.Core.Headless.Maps;
using Skua.Core.Headless.Rules;
using Skua.Core.Headless.Tests.Fakes;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Tests;

internal sealed class TestCharacter : IQuestCharacter
{
    public JsonObject ObjData { get; set; } = new() { ["intLevel"] = 30, ["iUpgDays"] = -1 };

    public Dictionary<int, int> Held { get; } = new();

    public HashSet<int> Equipped { get; } = new();

    public Dictionary<int, long> Rep { get; } = new();

    public int HeldQuantity(int itemId) => Held.GetValueOrDefault(itemId);

    public bool IsEquipped(int itemId) => Equipped.Contains(itemId);

    public bool HasItem(int itemId) => Held.ContainsKey(itemId);

    public int ClassPoints(int itemId) => Held.TryGetValue(itemId, out int q) ? q : -1;

    public long Reputation(int factionId) => Rep.GetValueOrDefault(factionId);
}

public class RankRulesTests
{
    [Theory]
    [InlineData(0, 1, 0, 900)]
    [InlineData(899, 1, 899, 900)]
    [InlineData(900, 2, 0, 2700)]
    [InlineData(3600, 3, 0, 6400)]
    [InlineData(302499, 9, 302499 - 202500, 100000)]
    [InlineData(302500, 10, 0, 0)]
    [InlineData(999999, 10, 999999 - 302500, 0)]
    [InlineData(-5, 1, 0, 900)]
    public void ProgressMatchesTheGameTable(long points, int rank, long inRank, long toNext)
    {
        RankProgress p = RankRules.Progress(points);
        Assert.Equal(rank, p.Rank);
        Assert.Equal(inRank, p.PointsInRank);
        Assert.Equal(toNext, p.PointsToNextRank);
    }

    [Fact]
    public void TableStartsAtZeroAndEndsAt302500()
    {
        Assert.Equal(new long[] { 0, 900, 3600, 10000, 22500, 44100, 78400, 129600, 202500, 302500 }, RankRules.RankStarts);
    }
}

public class QuestRulesTests
{
    [Fact]
    public void QuestValuesAreBase36DigitsAcrossStrings()
    {
        JsonObject o = new() { ["strQuests"] = "00a0z", ["strQuests2"] = "5" };
        Assert.Equal(10, QuestRules.GetQuestValue(o, 2));
        Assert.Equal(35, QuestRules.GetQuestValue(o, 4));
        Assert.Equal(0, QuestRules.GetQuestValue(o, 50));    // past the end
        Assert.Equal(5, QuestRules.GetQuestValue(o, 100));   // strQuests2[0]
        Assert.Equal(-1, QuestRules.GetQuestValue(o, 200));  // strQuests3 missing
        Assert.Equal(-1, QuestRules.GetQuestValue(o, -1));
    }

    [Fact]
    public void SetQuestValuePadsAndApplyUpdatesFolds()
    {
        JsonObject o = new() { ["strQuests"] = "00" };
        QuestRules.SetQuestValue(o, 4, 11);
        Assert.Equal("0000b", (string?)o["strQuests"]);
        QuestRules.ApplyUpdates(o, new Dictionary<int, int> { [1] = 2, [101] = 7 });
        Assert.Equal("0200b", (string?)o["strQuests"]);
        Assert.Equal("07", (string?)o["strQuests2"]);
    }

    [Fact]
    public void AchievementBits()
    {
        JsonObject o = new() { ["ia0"] = 5 };
        Assert.Equal(1, QuestRules.GetAchievement(o, "ia0", 0));
        Assert.Equal(0, QuestRules.GetAchievement(o, "ia0", 1));
        Assert.Equal(1, QuestRules.GetAchievement(o, "ia0", 2));
        Assert.Equal(-1, QuestRules.GetAchievement(o, "ia1", 0));
        Assert.Equal(-1, QuestRules.GetAchievement(o, "ia0", 32));
        QuestRules.SetAchievement(o, "ia0", 1, true);
        Assert.Equal(7L, (long)o["ia0"]!);
    }

    private static JsonObject Quest(string turnin) => (JsonObject)JsonNode.Parse($$"""{"QuestID":1,"turnin":{{turnin}}}""")!;

    [Fact]
    public void MaximumTurnInsUsesTheScarcestItemAndCaps()
    {
        TestCharacter c = new();
        JsonObject q = Quest("""[{"ItemID":10,"iQty":3},{"ItemID":11,"iQty":2}]""");
        Assert.Equal(0, QuestRules.MaximumTurnIns(q, c));
        c.Held[10] = 10;
        c.Held[11] = 4;
        Assert.Equal(2, QuestRules.MaximumTurnIns(q, c));
        c.Held[11] = 4000;
        c.Held[10] = 4000;
        Assert.Equal(250, QuestRules.MaximumTurnIns(q, c));
        c.Equipped.Add(11);
        Assert.Equal(0, QuestRules.MaximumTurnIns(q, c));
        Assert.Equal(-1, QuestRules.MaximumTurnIns(Quest("[]"), c));
    }

    [Fact]
    public void StatusIsCompletableOnlyWhenAllItemsAreHeld()
    {
        TestCharacter c = new();
        JsonObject q = Quest("""[{"ItemID":10,"iQty":3}]""");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Null(QuestRules.Status(q, accepted: false, c, now));
        Assert.Equal("p", QuestRules.Status(q, accepted: true, c, now));
        c.Held[10] = 3;
        Assert.Equal("c", QuestRules.Status(q, accepted: true, c, now));
        q["iTime"] = 1000;
        q["ts"] = now.ToUnixTimeMilliseconds() - 5000;
        Assert.Equal("p", QuestRules.Status(q, accepted: true, c, now));
    }

    [Fact]
    public void AcceptRefusals()
    {
        TestCharacter c = new();
        JsonObject q = (JsonObject)JsonNode.Parse("""{"bUpg":0,"iLvl":10,"FactionID":2,"iReqRep":500,"iClass":77,"iReqCP":900,"oReqd":{"55":{}}}""")!;
        Assert.Equal("Requires more reputation.", QuestRules.AcceptRefusal(q, c));
        c.Rep[2] = 600;
        Assert.Equal("Requires more class points.", QuestRules.AcceptRefusal(q, c));
        c.Held[77] = 1000;
        Assert.Equal("Missing a required item.", QuestRules.AcceptRefusal(q, c));
        c.Held[55] = 1;
        Assert.Null(QuestRules.AcceptRefusal(q, c));
        q["bUpg"] = 1;
        Assert.Equal("Member only.", QuestRules.AcceptRefusal(q, c));
        q["bUpg"] = 0;
        q["iLvl"] = 99;
        Assert.StartsWith("Requires level", QuestRules.AcceptRefusal(q, c));
    }
}

public class InventoryRulesTests
{
    private static JsonObject Item(int id, string type, int qty = 1, int stack = 1, string? meta = null, string es = "None")
    {
        JsonObject o = new() { ["ItemID"] = id, ["sType"] = type, ["iQty"] = qty, ["iStk"] = stack, ["sES"] = es };
        if (meta is not null)
            o["sMeta"] = meta;
        return o;
    }

    [Fact]
    public void PoolsFollowInvCat()
    {
        Assert.Equal("class", InventoryRules.PoolOf(Item(1, "Class")));
        Assert.Equal("misc", InventoryRules.PoolOf(Item(2, "Quest Item")));
        Assert.Equal("misc", InventoryRules.PoolOf(Item(3, "Item")));
        Assert.Equal("bag", InventoryRules.PoolOf(Item(4, "Item", meta: " 12 ")));
        Assert.Equal("house", InventoryRules.PoolOf(Item(5, "Floor Item")));
        Assert.Equal("bag", InventoryRules.PoolOf(Item(6, "Sword")));
        JsonObject house = Item(7, "Sword");
        house["bHouse"] = 1;
        Assert.Equal("house", InventoryRules.PoolOf(house));
    }

    [Fact]
    public void SpaceRespectsPoolsStacksAndFreedSlots()
    {
        List<JsonObject> items = new() { Item(1, "Sword"), Item(2, "Helm"), Item(3, "Class"), Item(10, "Resource", 5, 10), Item(11, "Note") };
        Assert.Equal(2, InventoryRules.CountBag(items));
        Assert.Equal(2, InventoryRules.CountMisc(items));
        List<JsonObject> house = new();
        // Bag full at 2 slots; a class never needs one; a stack with room always fits.
        Assert.False(InventoryRules.HasSpaceFor(Item(20, "Axe"), items, house, bagSlots: 2, houseSlots: 1, miscSlots: 100));
        Assert.True(InventoryRules.HasSpaceFor(Item(21, "Class"), items, house, 2, 1, 100));
        Assert.True(InventoryRules.HasSpaceFor(Item(10, "Resource"), items, house, 2, 1, 2));
        Assert.False(InventoryRules.HasSpaceFor(Item(12, "Resource"), items, house, 2, 1, 2));
        Assert.True(InventoryRules.HasSpaceFor(Item(12, "Resource"), items, house, 2, 1, 3));
        Assert.True(InventoryRules.HasSpaceFor(Item(30, "House"), items, house, 2, 1, 100));
        // A bank swap that sends a bag item back frees its slot.
        Assert.True(InventoryRules.HasSpaceFor(Item(20, "Axe"), items, house, 2, 1, 100, outgoing: Item(1, "Sword")));
        // A purchase that uses up a whole bag stack frees that slot.
        JsonObject merge = Item(40, "Axe");
        merge["iQty"] = 1;
        merge["turnin"] = new JsonArray(new JsonObject { ["ItemID"] = 2, ["iQty"] = 1 });
        Assert.Equal(1, InventoryRules.FreedBy(items, merge, 1));
        Assert.True(InventoryRules.HasSpaceFor(merge, items, house, 2, 1, 100, quantity: 1, purchase: true));
    }
}

public class ShopRulesTests
{
    [Fact]
    public void UnmetRequirementsListMaterialsTokensAndQuest()
    {
        JsonObject item = (JsonObject)JsonNode.Parse("""
            {"ItemID":500,"ShopItemID":9,"iQty":2,"iQSindex":3,"iQSvalue":4,"TokID":77,"TokQty":10,"TokName":"Token",
             "turnin":[{"ItemID":60,"iQty":5,"sName":"Ore"},{"ItemID":61,"iQty":1,"sName":"Gem"}]}
            """)!;
        Dictionary<int, (int, string?)> held = new() { [60] = (3, "Ore"), [77] = (100, "Token") };
        List<string> missing = ShopRules.UnmetPurchaseRequirements(item, 4, id => held.TryGetValue(id, out (int, string?) h) ? h : null, _ => 2);
        Assert.Equal(new[] { "Quest requirement not met.", "Missing 7 x Ore in inventory.", "Missing 2 x Gem in inventory." }, missing);
        Assert.Empty(ShopRules.UnmetPurchaseRequirements(item, 2, _ => (1000, "x"), _ => 9));
        Assert.Equal(new[] { "Item is not in the loaded shop." }, ShopRules.UnmetPurchaseRequirements(null, -1, _ => null, _ => 0));
        Assert.Equal(new[] { "Purchase quantity must be positive." }, ShopRules.UnmetPurchaseRequirements(item, 0, _ => null, _ => 0));
    }

    [Fact]
    public void MaximumBuysAndRefusals()
    {
        JsonObject objData = new() { ["intGold"] = 1000, ["intCoins"] = 5, ["iUpgDays"] = -1, ["intAccessLevel"] = 1 };
        JsonObject item = new() { ["ItemID"] = 1, ["iStk"] = 50, ["iCost"] = 30, ["bCoins"] = 0, ["iQty"] = 1 };
        Assert.Equal(33, ShopRules.MaximumShopBuys(item, objData, _ => null));
        Assert.Equal(20, ShopRules.MaximumShopBuys(item, objData, id => new JsonObject { ["iQty"] = 30 }));
        item["sES"] = "ar";
        Assert.Equal(1, ShopRules.MaximumShopBuys(item, objData, _ => null));
        item.Remove("sES");

        JsonObject shop = new() { ["ShopID"] = 5 };
        Assert.Null(ShopRules.BuyRefusal(item, shop, objData, _ => 0, _ => 0));
        item["iCost"] = 5000;
        Assert.Equal("Not enough gold.", ShopRules.BuyRefusal(item, shop, objData, _ => 0, _ => 0));
        item["iCost"] = 1;
        item["bUpg"] = 1;
        Assert.Equal("Member only.", ShopRules.BuyRefusal(item, shop, objData, _ => 0, _ => 0));
        Assert.Equal("No shop is loaded.", ShopRules.BuyRefusal(item, null, objData, _ => 0, _ => 0));
    }
}

public class SkillRulesTests
{
    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(0.2, 0.8)]
    [InlineData(0.9, 0.5)]
    [InlineData(-2, 2.0)]
    public void HasteIsClamped(double haste, double multiplier)
    {
        Assert.Equal(multiplier, SkillRules.HasteMultiplier(haste), 6);
    }

    [Fact]
    public void GlobalCooldownAndSkillCooldown()
    {
        Assert.Equal(1500, SkillRules.GlobalCooldown(0));
        Assert.Equal(1200, SkillRules.GlobalCooldown(0.2));
        // GCD blocks; then the skill's own (hasted) cooldown decides.
        Assert.False(SkillRules.IsOffCooldown(now: 1000, lastUse: 0, cooldown: 500, null, 0, globalCooldownStart: 0, globalCooldown: 1500));
        Assert.True(SkillRules.IsOffCooldown(now: 2000, lastUse: 0, cooldown: 2000, null, 0, 0, 1500));
        Assert.False(SkillRules.IsOffCooldown(now: 2000, lastUse: 1000, cooldown: 2000, null, 0, 0, 1500));
        Assert.True(SkillRules.IsOffCooldown(now: 2000, lastUse: 1000, cooldown: 2000, null, 0.5, 0, 1500));
        Assert.Equal(1000, SkillRules.EffectiveCooldown(9999, 2000, 0.5));
        Assert.True(SkillRules.HasMana(20, 1.0, 20));
        Assert.False(SkillRules.HasMana(20, 1.5, 20));
        Assert.Equal(1900, SkillRules.AutoAttackDelay(2000, 0, now: 1000, sentAt: 900));
        Assert.Equal(1400, SkillRules.AutoAttackDelay(2000, 0.25, now: 5000, sentAt: 0));
    }

    [Fact]
    public void ActionLocksThrottle()
    {
        ManualTime time = new();
        ActionLocks locks = new(time);
        Assert.True(locks.TryAcquire("loadShop"));
        Assert.False(locks.TryAcquire("loadShop"));
        Assert.False(locks.IsAvailable("loadShop"));
        time.Advance(TimeSpan.FromMilliseconds(3000));
        Assert.True(locks.TryAcquire("loadShop"));
        Assert.True(locks.TryAcquire("unknownAction"));
        JsonObject json = locks.ToJson();
        Assert.Equal(3000, (int)json["loadShop"]!["cd"]!);
        Assert.Equal(time.GetUtcNow().ToUnixTimeMilliseconds(), (long)json["loadShop"]!["ts"]!);
    }
}

public class AuraRulesTests
{
    [Fact]
    public void RebuildAuraArrayFollowsSkuaSwf()
    {
        JsonArray auras = (JsonArray)JsonNode.Parse("""
            [{"nam":"Burn","val":"x","cLeaf":{}},{"nam":"","val":2},{"nam":"Gone","e":1},{"nam":"Plain"},{"nam":"Old","dur":5,"ts":1000}]
            """)!;
        JsonArray r = AuraRules.RebuildAuraArray(auras, nowMs: 100000);
        Assert.Equal(new[] { "Burn", "Plain" }, r.Select(x => (string)x!["nam"]!));
        Assert.Equal(1, (int)r[0]!["val"]!);
        Assert.Equal("cycle_", (string)r[0]!["cLeaf"]!);
        Assert.Equal(1, (int)r[1]!["val"]!);
        Assert.Equal(3, AuraRules.RebuildAuraArray(auras).Count);
    }

    [Fact]
    public void HudAurasTrackStacksAndDurations()
    {
        HudAuraTable t = new();
        t.Apply("p:1", "aura+", (JsonObject)JsonNode.Parse("""{"auras":[{"nam":"Focus","t":"s","dur":10,"stk":2,"isNew":true,"desc":"d"}]}""")!, 1000);
        t.Apply("p:1", "aura=", (JsonObject)JsonNode.Parse("""{"aura":{"nam":"Focus","stk":5}}""")!, 2000);
        JsonObject s = (JsonObject)t.Snapshots("p:1", 6000)[0]!;
        Assert.Equal(5, (int)s["n"]!);
        Assert.Equal(10.0, (double)s["dur"]!);
        Assert.Equal(5.0, (double)s["remaining"]!);
        Assert.Equal("d", (string)s["desc"]!);
        t.Apply("p:1", "aura-", (JsonObject)JsonNode.Parse("""{"aura":{"nam":"Focus","stk":1}}""")!, 7000);
        Assert.Equal(1, (int)t.Snapshots("p:1", 7000)[0]!["n"]!);
        t.Apply("p:1", "aura-", (JsonObject)JsonNode.Parse("""{"aura":{"nam":"Focus"}}""")!, 7000);
        Assert.Empty(t.Snapshots("p:1", 7000));

        t.Seed("m:4", (JsonArray)JsonNode.Parse("""[["Shield","ic",3,2,1,8],["Bad"]]""")!, 0);
        JsonObject m = (JsonObject)Assert.Single(t.Snapshots("m:4", 0))!;
        Assert.Equal(8.0, (double)m["dur"]!);
        Assert.True((bool)m["persist"]!);
        t.ClearSubject("m:4", keepPersistent: true);
        Assert.Single(t.Snapshots("m:4", 0));
        t.ClearSubject("m:4", keepPersistent: false);
        Assert.Empty(t.Snapshots("m:4", 0));
    }
}

public class SwfReaderTests
{
    private static readonly (string, string[])[] s_cells =
    {
        ("Enter", new[] { "Spawn", "Left" }),
        ("r2", new[] { "Left", "Right" }),
        ("r3", Array.Empty<string>()),
    };

    [Theory]
    [InlineData("CWS", true)]
    [InlineData("FWS", true)]
    [InlineData("FWS", false)]
    public void ReadsLabelsAndPads(string compression, bool sceneTag)
    {
        SwfTimeline t = SwfReader.Read(SyntheticSwf.Map(s_cells, compression, sceneTag));
        Assert.Equal(compression == "CWS" ? SwfCompression.Zlib : SwfCompression.None, t.Compression);
        Assert.Equal(new[] { "Enter", "r2", "r3" }, t.Labels.Select(l => l.Name));
        Assert.Equal(new[] { 0, 1, 2 }, t.Labels.Select(l => l.Frame));
        Assert.Equal(new[] { "Spawn", "Left" }, t.Pads["Enter"]);
        Assert.Equal(new[] { "Left", "Right" }, t.Pads["r2"]);
        Assert.Empty(t.Pads["r3"]);
    }

    [Fact]
    public void LzmaIsRefusedAndReportedByTheCache()
    {
        byte[] zws = SyntheticSwf.Map(s_cells, "ZWS");
        Assert.Equal(SwfCompression.Lzma, SwfReader.DetectCompression(zws));
        Assert.Throws<NotSupportedException>(() => SwfReader.Read(zws));
        MapCells cells = MapCellCache.FromSwf("x.swf", zws);
        Assert.False(cells.HasCells);
        Assert.Equal("unsupported: lzma", cells.Source);
    }

    [Fact]
    public void GarbageIsAFormatError()
    {
        Assert.Throws<SwfFormatException>(() => SwfReader.Read(new byte[] { 1, 2, 3 }));
        byte[] truncated = SyntheticSwf.Map(s_cells, "FWS")[..30];
        Assert.ThrowsAny<Exception>(() => SwfReader.Read(truncated));
    }

    [Fact]
    public async Task CacheDownloadsOnceAndRemembersOnDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "skua-mapcells-" + Guid.NewGuid().ToString("N"));
        int downloads = 0;
        Task<byte[]> Download(string file, CancellationToken ct)
        {
            downloads++;
            return Task.FromResult(SyntheticSwf.Map(s_cells));
        }
        try
        {
            MapCells first = await new MapCellCache(Download, dir).GetAsync("test/map.swf");
            Assert.Equal("swf", first.Source);
            Assert.Equal("Left", first.CorrectPad("r2", "Top"));
            Assert.Equal("Right", first.CorrectPad("r2", "Right"));
            Assert.Equal("Top", first.CorrectPad("r3", "Top"));
            MapCells second = await new MapCellCache(Download, dir).GetAsync("test/map.swf");
            Assert.Equal("cache", second.Source);
            Assert.Equal(first.Cells, second.Cells);
            Assert.Equal(1, downloads);
            MapCells failed = await new MapCellCache((_, _) => throw new IOException("404"), null).GetAsync("nope.swf");
            Assert.StartsWith("error", failed.Source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("town-Mobius-21Feb14.swf", "Enter", "Side3", "Tower")]
    [InlineData("town-tavern-24Aug23.swf", "Enter", "Hall", "three")]
    public void RealMapsWhenAvailable(string file, string firstCell, string cell, string pad)
    {
        // Real map files are not in the repository. Set SKUA_TEST_MAPS to a folder holding them to run this.
        string? folder = Environment.GetEnvironmentVariable("SKUA_TEST_MAPS");
        if (folder is null || !File.Exists(Path.Combine(folder, file)))
            return;
        SwfTimeline t = SwfReader.Read(File.ReadAllBytes(Path.Combine(folder, file)));
        Assert.Contains(t.Labels, l => l.Name == firstCell);
        Assert.Contains(pad, t.Pads[cell]);
    }
}
