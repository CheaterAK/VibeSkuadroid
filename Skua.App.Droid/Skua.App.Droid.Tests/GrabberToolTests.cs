using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Tools;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.Scripting;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Models.Items;
using Skua.Core.Models.Shops;
using Skua.Core.Services;

namespace Skua.App.Droid.Tests;

public partial class ToolsTests
{
    [Fact]
    public void TheGrabberRulesAndTextAreTheDesktops()
    {
        ShopItem paidAc = new() { ID = 1, Name = "AC Hat", Coins = true, Cost = 100, MaxStack = 1 };
        ShopItem freeAc = new() { ID = 2, Name = "Free", Coins = true, Cost = 0, MaxStack = 1 };
        ShopItem potion = new() { ID = 3, Name = "Potion", Cost = 10, MaxStack = 50 };
        Assert.True(GrabberService.IsPaidAcItem(paidAc));
        Assert.False(GrabberService.IsPaidAcItem(freeAc));
        Assert.Equal(50, GrabberService.ClampBuyQuantity(potion, 999));
        Assert.Equal(1, GrabberService.ClampBuyQuantity(potion, 0));
        Assert.Null(GrabberService.NotEnoughGold(potion, 5, 50));
        Assert.Equal("Not enough gold to buy 6 Potion.\r\nTotal: 60\r\nNeeded: 10", GrabberService.NotEnoughGold(potion, 6, 50));
        Assert.Contains("the 2 items", GrabberService.NotEnoughGold([potion, potion], 15));
        InventoryItem cls = new() { ID = 4, Name = "Healer", Quantity = 3000, CategoryString = "Class" };
        Assert.Equal(1, GrabberService.MaxSellQuantity(cls));
        Assert.Equal("[3] Potion | Cost: 10 Gold", GrabberText.FormatGrabbedItem(potion));
        Assert.StartsWith("=== Shop (1 items) ===", GrabberText.BuildExportText("Shop", [potion]));
        Assert.Contains("MaxStack: 50", GrabberText.BuildDetailedExportText("Shop", [potion]));
    }

    [Fact]
    public async Task TheGrabberGrabsSearchesCopiesAndAsksBeforeSpending()
    {
        (TestApp app, FakeGameTools tools) = NewApp();
        await using TestApp _ = app;
        FakeGrabber backend = tools.FakeGrabber;
        GrabberViewModel page = new(app.Services);
        Assert.Equal(["Shop Items", "Shop IDs", "Quests", "GetMap Item IDs", "Inventory", "House Inventory", "Temp Inventory", "Bank Items", "Cell Monsters", "Map Monsters"],
            page.Tabs.Select(t => t.Title));
        Assert.Equal(["Buy"], page.Actions.Select(a => a.Title));

        await page.GrabCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal("Health Potion | Cost: 10 Gold", page.Rows[0].Text);
        Assert.Equal("2 of 2", page.ListText);
        page.SearchText = "1,000";
        Assert.Equal(["Fancy Hat"], page.Rows.Select(r => r.Item.Name));
        page.SearchText = string.Empty;

        // Copy: the selection, or everything shown.
        await page.CopyIdsCommand.ExecuteAsync(null);
        Assert.Equal("40,41", app.Picker.Copied);
        page.Rows[1].IsSelected = true;
        Assert.Equal(1, page.SelectedCount);
        await page.CopyNamesCommand.ExecuteAsync(null);
        Assert.Equal("Fancy Hat", app.Picker.Copied);
        await page.CopyLinesCommand.ExecuteAsync(null);
        Assert.StartsWith("=== Shop Items (1 items) ===", app.Picker.Copied);
        await page.Rows[0].CopyIdCommand.ExecuteAsync(null);
        Assert.Equal("40", app.Picker.Copied);
        page.ShareDetailsCommand.Execute(null);
        Assert.Equal("details of 41", app.Platform.Shared);

        // Buy asks with a quantity; cancel sends nothing.
        page.ClearSelectionCommand.Execute(null);
        page.Rows[0].IsSelected = true;
        Task buy = page.Actions[0].Command.ExecuteAsync(null);
        await Sandbox.Until(() => page.Confirm.IsOpen);
        Assert.True(page.Confirm.HasQuantity);
        Assert.Equal(99m, page.Confirm.QuantityMax);
        page.Confirm.CancelCommand.Execute(null);
        await buy;
        Assert.DoesNotContain(backend.Calls, c => c.StartsWith("Buy", StringComparison.Ordinal));
        buy = page.Actions[0].Command.ExecuteAsync(null);
        await Sandbox.Until(() => page.Confirm.IsOpen);
        page.Confirm.Quantity = 7;
        page.Confirm.ConfirmCommand.Execute(null);
        await buy;
        Assert.Equal("Buy 40 x7", backend.Calls[^1]);
        Assert.Equal("Buy done", page.Status);

        // While a script runs: the script question first, then the purchase question.
        page.Apply(ScriptRunning);
        buy = page.Actions[0].Command.ExecuteAsync(null);
        await AnswerAsync(page.Confirm, yes: true, ConfirmHost.ScriptRunningTitle);
        await AnswerAsync(page.Confirm, yes: true, "Buy");
        await buy;
        Assert.Equal("Buy 40 x1", backend.Calls[^1]);
        page.Apply(EngineSnapshot.Initial);

        // Inventory: refusals are shown and nothing runs.
        page.SelectedTab = page.Tabs.First(t => t.Category == GrabberCategory.Inventory);
        Assert.Equal(["Equip", "Sell", "Sell All", "To Bank"], page.Actions.Select(a => a.Title));
        Assert.True(page.Actions[1].Destructive);
        Assert.Empty(page.Rows);
        await page.GrabCommand.ExecuteAsync(null);
        Assert.True(page.Rows[1].IsJunk);
        page.SelectAllCommand.Execute(null);
        int calls = backend.Calls.Count;
        await page.Actions[1].Command.ExecuteAsync(null);
        Assert.Contains("2 items selected", page.Message);
        Assert.Equal(calls, backend.Calls.Count);

        // Actions that spend or move nothing run at once; a long one can be cancelled.
        page.SelectedTab = page.Tabs.First(t => t.Category == GrabberCategory.MapMonsters);
        await page.GrabCommand.ExecuteAsync(null);
        page.Rows[0].IsSelected = true;
        backend.Gate = new ManualResetEventSlim(false);
        Task kill = page.Actions[0].Command.ExecuteAsync(null);
        await Sandbox.Until(() => page.CanCancel);
        Assert.False(page.Confirm.IsOpen);
        await page.Actions[1].Command.ExecuteAsync(null);
        Assert.Contains("still running", page.Message);
        page.CancelCommand.Execute(null);
        await kill;
        Assert.Equal("Task cancelled.", page.Status);
        Assert.Equal("Kill 5 x1", backend.Calls[^1]);
    }

    [Fact]
    public async Task TheLoadersViewInGrabberShowsTheShop()
    {
        (TestApp app, FakeGameTools tools) = NewApp();
        await using TestApp _ = app;
        using ShellViewModel shell = new(app.Services);
        ToolsViewModel hub = shell.Settings.Tools;
        GameToolPages pages = shell.Settings.GamePages;
        hub.Show(pages.Loader, "Loader");
        pages.Loader.InputIds = "77";
        await pages.Loader.LoadCommand.ExecuteAsync(null);
        pages.Loader.ViewInGrabberCommand.Execute(null);
        Assert.Same(pages.Grabber, hub.CurrentPage);
        Assert.Equal("Grabber", hub.CurrentTitle);
        await Sandbox.Until(() => pages.Grabber.Rows.Count == 2);
        Assert.Equal(GrabberCategory.ShopItems, pages.Grabber.SelectedTab.Category);
        Assert.Contains("grab ShopItems", tools.FakeGrabber.Calls);
    }
}

public partial class SessionIntegrationTests
{
    /// <summary>
    /// The Grabber on the real Skua host over the fake server: Skua's grabber service lists the loaded shop, quests,
    /// inventory, bank and monsters; buying and selling (the Control page's calls) with the desktop's refusals; accept,
    /// bank to backpack, teleport to a monster; map items (none without the desktop's decompiler).
    /// </summary>
    [Fact]
    public async Task TheGrabberGrabsAndActsThroughSkua()
    {
        await using FakeAqw aqw = new();
        (SkuaScriptHostAdapter host, HeadlessBotEngine engine) = ToolsHost(aqw);
        IGameTools tools = Assert.IsAssignableFrom<IGameTools>(engine.Plugins?.GameTools);
        IGrabberBackend grabber = tools.Grabber;
        Progress<string> none = new(_ => { });
        try
        {
            Assert.Contains("Log in", Assert.Throws<EngineException>(() => grabber.Grab(GrabberCategory.Inventory)).Message);
            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => engine.Snapshot.Character is not null && host.Skua!.Bot.Map.Loaded);
            FakeAqwServer.Session game = aqw.Server.LastSession!;

            // Shop: load it, grab it, buy with a quantity; the desktop's gold refusal for several items.
            Assert.Empty(grabber.Grab(GrabberCategory.ShopItems));
            tools.Loader.Load(LoaderKind.Shop, "77");
            IReadOnlyList<GrabbedItem> shop = grabber.Grab(GrabberCategory.ShopItems);
            Assert.Equal(["Health Potion", "Fancy Hat", "Member Cape", "Wing Charm"], shop.Select(i => i.Name));
            Assert.StartsWith("[40] Health Potion | Cost: 10 Gold", shop[0].Text);
            Assert.Contains(grabber.Grab(GrabberCategory.ShopIds), s => s.Id == 77 && s.Name == "Pixie Shop");
            GrabberPlan plan = grabber.Plan(GrabberAction.Buy, [shop[0]]);
            Assert.Equal(99, plan.Quantity!.Max);
            Assert.Contains("Health Potion", await grabber.RunAsync(GrabberAction.Buy, [shop[0]], 3, none, CancellationToken.None));
            Assert.Contains((40, 3), game.Purchases);
            Assert.Contains("Not enough gold to buy the 2 items", grabber.Plan(GrabberAction.Buy, [shop[0], shop[1]]).Refusal);

            // Inventory: the desktop's refusals, then a sale.
            IReadOnlyList<GrabbedItem> inventory = grabber.Grab(GrabberCategory.Inventory);
            GrabbedItem sword = inventory.Single(i => i.Name == "Default Sword");
            GrabbedItem boots = inventory.Single(i => i.Name == "Old Boots");
            Assert.Equal("Cannot sell equipped item.", grabber.Plan(GrabberAction.Sell, [sword]).Refusal);
            Assert.Contains("2 items selected", grabber.Plan(GrabberAction.SellAll, [sword, boots]).Refusal);
            Assert.NotNull(grabber.Plan(GrabberAction.SellAll, [boots]).Question);
            Assert.Contains("Old Boots", await grabber.RunAsync(GrabberAction.SellAll, [boots], 1, none, CancellationToken.None));
            Assert.Contains((30, 1), game.Sales);

            // Quests: load, grab, accept.
            tools.Loader.Load(LoaderKind.Quest, "1000");
            GrabbedItem quest = Assert.Single(grabber.Grab(GrabberCategory.Quests), q => q.Id == 1000);
            Assert.Contains("Accepted", await grabber.RunAsync(GrabberAction.AcceptQuest, [quest], 1, none, CancellationToken.None));
            Assert.Contains(1000, game.Accepted);

            // Bank: load it (the Control page's call), grab, move to the backpack.
            engine.Plugins!.Control!.LoadBank();
            GrabbedItem wing = Assert.Single(grabber.Grab(GrabberCategory.Bank), b => b.Name == "Pixie Wing");
            Assert.Contains("backpack", await grabber.RunAsync(GrabberAction.ToInventory, [wing], 1, none, CancellationToken.None));

            // Monsters: the map's three, teleport to the queen's cell.
            IReadOnlyList<GrabbedItem> monsters = grabber.Grab(GrabberCategory.MapMonsters);
            Assert.Equal(3, monsters.Count);
            GrabbedItem queen = monsters.Single(m => m.Name == "Pixie Queen");
            Assert.Equal("Jumped to r3.", await grabber.RunAsync(GrabberAction.TeleportTo, [queen], 1, none, CancellationToken.None));
            await Sandbox.Until(() => host.Skua!.Bot.Player.Cell == "r3");
            Assert.Contains(grabber.Grab(GrabberCategory.CellMonsters), m => m.Name == "Pixie Queen");

            // Map items need the desktop's map decompiler (Windows): nothing here, and no error.
            Assert.Empty(grabber.Grab(GrabberCategory.MapItemIds));
            Assert.StartsWith("=== Shop Items (4 items) ===", grabber.CopyText("Shop Items", shop));
            Assert.Contains("ShopItemID: 9001", grabber.DetailedText("Shop Items", [shop[0]]));
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }
}
