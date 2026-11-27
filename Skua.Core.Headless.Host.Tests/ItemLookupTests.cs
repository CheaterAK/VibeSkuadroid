using Newtonsoft.Json;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>
/// The inventory services' lookups read the bridge's shared snapshots (IItemListSnapshots, ItemLookup). They must
/// answer exactly what the interface's own code answers on a fresh parse of the list (the desktop's path): names
/// compared ignoring case (Caseless), the first match in list order, duplicates, classes counting as any quantity,
/// quantity 0, the backpack's class points; and hand out copies.
/// </summary>
public sealed class ItemLookupTests : IAsyncLifetime
{
    private static readonly string s_data = Path.Combine(Path.GetTempPath(), "skua-headless-lookup-" + Environment.ProcessId);
    private FakeWorld _world = null!;
    private HeadlessSkuaSession _session = null!;

    public Task InitializeAsync()
    {
        _world = new FakeWorld();
        _session = HeadlessSkuaSession.Create(new HeadlessSkuaOptions
        {
            DataDirectory = s_data,
            WebApi = _world.Api,
            Game = new HeadlessGameOptions { Session = _world.SessionOptions, Client = new() { MapLoadTime = TimeSpan.FromMilliseconds(50) } },
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _world.DisposeAsync();
    }

    /// <summary>The list as the interface's default methods see it: a fresh parse of getGameObject's text.</summary>
    private sealed class FreshParse<T>(IFlashUtil flash, string path) : ICheckInventory<T> where T : ItemBase
    {
        public List<T> Items => JsonConvert.DeserializeObject<List<T>>(flash.GetGameObject(path) ?? "[]") ?? new();
    }

    private static JsonObject Item(int id, string name, string type, int qty, int stack, int charItemId) => new()
    {
        ["ItemID"] = id, ["CharItemID"] = charItemId, ["sName"] = name, ["sDesc"] = "d", ["sType"] = type, ["sES"] = type == "Class" ? "ar" : "None",
        ["iQty"] = qty, ["iStk"] = stack, ["bCoins"] = 0, ["bUpg"] = 0, ["bTemp"] = 0, ["bEquip"] = 0, ["sFile"] = "", ["sLink"] = "", ["sMeta"] = "",
    };

    // Awkward items: names differing only by case, the same name twice, padded names, non-ASCII case, a class with
    // no quantity, a full stack, and the same ID twice.
    private static List<JsonObject> Awkward(int firstId, int charItem) =>
    [
        Item(firstId, "Dup Name", "Item", 2, 99, charItem),
        Item(firstId + 1, "DUP NAME", "Item", 7, 99, charItem + 1),
        Item(firstId + 2, "dup name", "Item", 1, 1, charItem + 2),
        Item(firstId + 3, "  Padded Thing  ", "Resource", 5, 500, charItem + 3),
        Item(firstId + 4, "Ärger Stein", "Item", 3, 99, charItem + 4),
        Item(firstId + 5, "Banked Class", "Class", 0, 1, charItem + 5),
        Item(firstId + 6, "Full Stack", "Item", 99, 99, charItem + 6),
        Item(firstId + 6, "Same Id", "Item", 4, 99, charItem + 7),
    ];

    private static readonly string[] s_names =
    [
        "Dup Name", "dup name", "DUP NAME", "dUp NaMe", "Padded Thing", "  Padded Thing  ", "padded thing", "Ärger Stein", "ärger stein", "ÄRGER STEIN",
        "Banked Class", "banked class", "Full Stack", "Same Id", "Pixie Wing", "PIXIE WING", "Healer", "healer", "Default Sword", "Pixie Dust", "Lost Ring",
        "Missing", "", " ",
    ];

    private static readonly int[] s_quantities = [0, 1, 2, 3, 5, 7, 99, 100, 3000, 3001, -1];

    private static void AssertSame<T>(ICheckInventory<T> service, ICheckInventory<T> fresh, IEnumerable<int> ids, string what) where T : ItemBase
    {
        foreach (string name in s_names)
        {
            foreach (int q in s_quantities)
                Assert.True(fresh.Contains(name, q) == service.Contains(name, q), $"{what}.Contains(\"{name}\", {q})");
            Assert.Equal(JsonConvert.SerializeObject(fresh.GetItem(name)), JsonConvert.SerializeObject(service.GetItem(name)));
            Assert.Equal(fresh.TryGetItem(name, out T? _), service.TryGetItem(name, out T? _));
            Assert.True(fresh.GetQuantity(name) == service.GetQuantity(name), $"{what}.GetQuantity(\"{name}\")");
            Assert.True(fresh.IsMaxStack(name) == service.IsMaxStack(name), $"{what}.IsMaxStack(\"{name}\")");
        }
        foreach (int id in ids.Concat([0, -5, 987654]))
        {
            foreach (int q in s_quantities)
                Assert.True(fresh.Contains(id, q) == service.Contains(id, q), $"{what}.Contains({id}, {q})");
            Assert.Equal(JsonConvert.SerializeObject(fresh.GetItem(id)), JsonConvert.SerializeObject(service.GetItem(id)));
            Assert.Equal(fresh.GetQuantity(id), service.GetQuantity(id));
            Assert.Equal(fresh.IsMaxStack(id), service.IsMaxStack(id));
        }
        Assert.Equal(fresh.Contains(true, 1, s_names), service.Contains(true, 1, s_names));
        Assert.Equal(fresh.Contains(false, 1, s_names), service.Contains(false, 1, s_names));
        Assert.Equal(fresh.Contains(false, 2, "Missing", "dup name"), service.Contains(false, 2, "Missing", "dup name"));
        Assert.Equal(JsonConvert.SerializeObject(fresh.Items), JsonConvert.SerializeObject(service.Items));
    }

    private static string Inbound(JsonObject o) => new JsonObject { ["t"] = "xt", ["b"] = new JsonObject { ["r"] = -1, ["o"] = o } }.ToJsonString();

    [Fact]
    public async Task LookupsAnswerWhatAFreshParseAnswers()
    {
        _world.Server.ExtraInventoryItems.AddRange(Awkward(700, 8000));
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"));
        FakeAqwServer.Session server = _world.Server.LastSession!;
        server.Bank.AddRange(LargeBank.Items(300));
        server.Bank.AddRange(Awkward(800, 9000));
        await _session.Game.Session.LoadBankAsync();
        IScriptInterface bot = _session.Bot;
        IFlashUtil flash = _session.Flash;
        Assert.IsAssignableFrom<IItemListSnapshots>(flash);

        // Temporary and house items arrive as the game sends them.
        _session.Game.InjectInbound(Inbound(new JsonObject
        {
            ["cmd"] = "addItems",
            ["items"] = new JsonObject
            {
                ["10"] = new JsonObject { ["ItemID"] = 10, ["sName"] = "Pixie Dust", ["iQty"] = 3, ["iStk"] = 10, ["bTemp"] = 1, ["sType"] = "Quest Item" },
                ["13"] = new JsonObject { ["ItemID"] = 13, ["sName"] = "LOST RING", ["iQty"] = 1, ["iStk"] = 1, ["bTemp"] = 1, ["sType"] = "Quest Item" },
            },
        }));
        _session.Game.InjectInbound(Inbound(new JsonObject
        {
            ["cmd"] = "loadHouseInventory",
            ["items"] = new JsonArray(Awkward(900, 9500).Select(i => { i["sType"] = "Floor Item"; return (JsonNode)i; }).ToArray()),
        }));

        int[] bankIds = Enumerable.Range(800, 7).Concat([100000, 100150]).ToArray();
        AssertSame(bot.Bank, new FreshParse<InventoryItem>(flash, HeadlessFlashUtil.BankItemsPath), bankIds, "Bank");
        AssertSame(bot.Inventory, new FreshParse<InventoryItem>(flash, HeadlessFlashUtil.InventoryItemsPath), [1, 2, 30, 700, 701, 705, 706], "Inventory");
        AssertSame(bot.House, new FreshParse<InventoryItem>(flash, HeadlessFlashUtil.HouseItemsPath), [900, 901, 906], "House");
        AssertSame(bot.TempInv, new FreshParse<ItemBase>(flash, HeadlessFlashUtil.TempItemsPath), [10, 13], "TempInv");

        // Caseless: names ignore case on both paths; the first match in list order is the one returned.
        Assert.True(bot.Bank.Contains("PIXIE WING"));
        Assert.Equal(800, bot.Bank.GetItem("DUP NAME")!.ID);
        Assert.Equal(800, bot.Bank.GetItem("dup name")!.ID);
        Assert.Equal(2, bot.Bank.GetQuantity("DUP NAME"));
        Assert.True(bot.Bank.Contains("dup name", 7));
        Assert.False(bot.Bank.Contains("dup name", 8));
        // A class counts as any quantity (the bank keeps none); the equipped class's quantity is the class points.
        Assert.False(bot.Bank.Contains("Banked Class"));
        Assert.True(bot.Inventory.Contains("healer", 999999));
        Assert.Equal(((ICheckInventory<InventoryItem>)new FreshParse<InventoryItem>(flash, HeadlessFlashUtil.InventoryItemsPath)).GetQuantity("Healer"), bot.Inventory.GetQuantity("Healer"));

        // What a lookup hands out is the caller's own: changing it changes nothing for the next lookup.
        InventoryItem got = bot.Bank.GetItem(800)!;
        got.Quantity = 12345;
        got.Name = "Changed";
        Assert.Equal(2, bot.Bank.GetItem(800)!.Quantity);
        Assert.True(bot.Bank.Contains("Dup Name"));
        Assert.NotSame(bot.Bank.GetItem(800), bot.Bank.GetItem(800));
        List<InventoryItem> items = bot.Bank.Items;
        items[0].Quantity = 777;
        items.Clear();
        Assert.Equal(new FreshParse<InventoryItem>(flash, HeadlessFlashUtil.BankItemsPath).Items.Count, bot.Bank.Items.Count);
    }

    [Fact]
    public async Task LookupsFollowEveryChange()
    {
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"));
        FakeAqwServer.Session server = _world.Server.LastSession!;
        server.Bank.AddRange(LargeBank.Items(500));
        await _session.Game.Session.LoadBankAsync();
        IScriptInterface bot = _session.Bot;
        IFlashUtil flash = _session.Flash;
        ICheckInventory<InventoryItem> freshBank = new FreshParse<InventoryItem>(flash, HeadlessFlashUtil.BankItemsPath);
        ICheckInventory<InventoryItem> freshInventory = new FreshParse<InventoryItem>(flash, HeadlessFlashUtil.InventoryItemsPath);
        Assert.True(bot.Bank.Contains("Pixie Wing", 2));
        long parsed = _session.Flash.BankItemParses;

        // A stack change in the bank (a drop banked with its new total): one item parsed again, the rest kept.
        _session.Game.InjectInbound(Inbound(new JsonObject
        {
            ["cmd"] = "addItems",
            ["items"] = new JsonObject { ["11"] = new JsonObject { ["ItemID"] = 11, ["iQty"] = 1, ["iQtyNow"] = 40, ["bBank"] = 1 } },
        }));
        Assert.Equal(40, bot.Bank.GetQuantity("Pixie Wing"));
        Assert.True(bot.Bank.Contains(11, 40));
        Assert.Equal(parsed + 1, _session.Flash.BankItemParses);
        Assert.Equal(freshBank.GetQuantity(11), bot.Bank.GetQuantity(11));
        Assert.Equal(JsonConvert.SerializeObject(freshBank.Items), JsonConvert.SerializeObject(bot.Bank.Items));

        // Bank to backpack and back.
        Assert.True(bot.Bank.EnsureToInventory("Pixie Wing", loadBank: false));
        Assert.False(bot.Bank.Contains("Pixie Wing"));
        Assert.True(bot.Inventory.Contains("pixie wing", 40));
        Assert.True(bot.Inventory.EnsureToBank("Pixie Wing"));
        Assert.True(await FakeWorld.Until(() => bot.Bank.Contains("Pixie Wing")));
        Assert.False(bot.Inventory.Contains("Pixie Wing"));
        Assert.Equal(JsonConvert.SerializeObject(freshBank.Items), JsonConvert.SerializeObject(bot.Bank.Items));
        Assert.Equal(JsonConvert.SerializeObject(freshInventory.Items), JsonConvert.SerializeObject(bot.Inventory.Items));

        // Class points (a kill): the equipped class's quantity follows, as on a fresh parse.
        int before = bot.Inventory.GetQuantity("Healer");
        _session.Game.InjectInbound(Inbound(new JsonObject { ["cmd"] = "addGoldExp", ["id"] = 1, ["typ"] = "m", ["intGold"] = 1, ["intExp"] = 1, ["iCP"] = 25 }));
        Assert.Equal(freshInventory.GetQuantity("Healer"), bot.Inventory.GetQuantity("Healer"));
        Assert.NotEqual(before, bot.Inventory.GetQuantity("Healer"));
        Assert.Equal(JsonConvert.SerializeObject(freshInventory.Items), JsonConvert.SerializeObject(bot.Inventory.Items));

        // A script writing the bank path itself: everything is parsed again.
        long rebuilt = _session.Flash.BankItemParses;
        flash.SetGameObject("world.bankinfo.items.0.iQty", 4242);
        Assert.Equal(JsonConvert.SerializeObject(freshBank.Items), JsonConvert.SerializeObject(bot.Bank.Items));
        Assert.Equal(4242, bot.Bank.Items[0].Quantity);
        Assert.True(_session.Flash.BankItemParses >= rebuilt + 500);

        // Logged out: no snapshot; the lookups read the (empty) list as before.
        await _session.Game.Session.DisconnectAsync();
        Assert.True(await FakeWorld.Until(() => !_session.Game.IsInGame));
        Assert.Null(((IItemListSnapshots)flash).GetItemListSnapshot<InventoryItem>(HeadlessFlashUtil.BankItemsPath));
        Assert.False(bot.Bank.Contains("Pixie Wing"));
    }

    [Fact]
    public async Task ALookupAllocatesAlmostNothing()
    {
        _world.Server.ExtraInventoryItems.AddRange(LargeBank.Items(250, firstId: 300000, seed: 7));
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"));
        FakeAqwServer.Session server = _world.Server.LastSession!;
        List<JsonObject> bank = LargeBank.Items(3000);
        server.Bank.AddRange(bank);
        await _session.Game.Session.LoadBankAsync();
        IScriptInterface bot = _session.Bot;
        string name = bank[1500]["sName"]!.GetValue<string>();
        int id = bank[1500]["ItemID"]!.GetValue<int>();
        Action[] lookups =
        [
            () => Assert.True(bot.Bank.Contains(name)),
            () => Assert.False(bot.Bank.Contains("No Such Item")),
            () => Assert.True(bot.Bank.Contains(id)),
            () => Assert.True(bot.Bank.TryGetItem(name, out InventoryItem? _)),
            () => bot.Bank.GetQuantity(id),
            () => bot.Inventory.Contains("Healer"),
            () => bot.Inventory.Contains("No Such Item", 5),
            () => bot.Inventory.IsEquipped("Healer"),
            () => bot.TempInv.Contains("Pixie Dust", 3),
            () => bot.House.Contains("No Such Item"),
        ];
        foreach (Action lookup in lookups)
        {
            lookup();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                lookup();
            long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
            // Before: 0.4 MB per bank lookup on 3000 items, 2.7 MB per backpack lookup on 250. Now a few hundred bytes
            // (the copy of the one item a TryGetItem returns, the lock-free version checks).
            Assert.True(perCall < 2048, $"a lookup allocated {perCall} bytes");
        }
    }
}
