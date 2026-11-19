using System.Net;
using System.Text.Json.Nodes;
using Skua.App.Droid.Core.Control;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.Plugins.Control;
using Skua.App.Droid.Scripting;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Models;

namespace Skua.App.Droid.Tests;

/// <summary>The Control page's ViewModel on a fake backend: the map and cell lists, confirmations, the script prompt, errors and the refresh throttle.</summary>
public class ControlTests
{
    private static async Task<(TestApp App, FakeControlBackend Backend, ControlViewModel Page)> OpenAsync()
    {
        FakePluginBackends plugins = new();
        TestApp app = new(scripts: new PluginOnlyScriptHost(plugins));
        ControlViewModel page = new(app.Services);
        page.IsActive = true;
        await page.RefreshNowAsync();
        return (app, plugins.FakeControl, page);
    }

    private static async Task AnswerAsync(ControlViewModel page, bool yes)
    {
        await Sandbox.Until(() => page.IsConfirming);
        if (yes)
            page.ConfirmCommand.Execute(null);
        else
            page.CancelConfirmCommand.Execute(null);
    }

    [Fact]
    public async Task ThePageShowsTheMapTheCellAndItsActions()
    {
        (TestApp app, FakeControlBackend backend, ControlViewModel page) = await OpenAsync();
        await using TestApp _ = app;
        Assert.True(page.IsAvailable);
        Assert.Equal("testmap-901", page.MapTitle);
        Assert.Equal(["Enter", "r2", "r3"], page.Tiles.Select(t => t.Name));
        Assert.Equal("you · 1 pl", page.Tiles[0].Counts);
        Assert.True(page.Tiles[0].IsCurrent);
        Assert.Equal("2/2 mon", page.Tiles[1].Counts);
        Assert.True(page.Tiles[1].HasMonsters);
        Assert.Equal("0/1 mon", page.Tiles[2].Counts);
        Assert.Contains("seen in this room", page.CellSourceText);
        Assert.Equal(["Spawn", "Left", "Right"], page.Pads.Select(p => p.Name));
        Assert.True(page.Pads[0].IsCurrent);
        Assert.Equal(2, page.Monsters.Count);
        Assert.Equal("Lv 3 · in combat · targeted", page.Monsters[0].Detail);
        Assert.Equal(50, page.Monsters[0].HpPercent);
        Assert.False(page.Monsters[1].Alive);
        Assert.Equal("Lv 12 · idle · afk · Enter", page.Players[0].Detail);
        Assert.Equal(["1 Heal", "2 Smite", "5 Potion"], page.Skills.Select(s => s.Label));
        Assert.Equal("Pixie (#1)", page.TargetText);
        Assert.True(page.HasDrops);

        // Tap a tile, a pad, Attack, Go to, a skill, cancel, rest, pick up: no confirmation (nothing is spent).
        page.Tiles[1].JumpCommand.Execute("r2");
        await page.LastAction;
        page.Pads[1].UseCommand.Execute("Left");
        await page.LastAction;
        page.Monsters[0].AttackCommand.Execute(1);
        await page.LastAction;
        page.Players[0].GotoCommand.Execute("OtherPlayer");
        await page.LastAction;
        page.Skills[1].UseCommand.Execute(2);
        await page.LastAction;
        page.CancelTargetCommand.Execute(null);
        await page.LastAction;
        page.RestCommand.Execute(null);
        await page.LastAction;
        page.Drops[0].PickupCommand.Execute(11);
        await page.LastAction;
        Assert.Equal(["jump r2 -", "jump Enter Left", "attack 1", "goto OtherPlayer", "skill 2", "cancel", "rest", "pickup 11"], backend.Calls);
        Assert.Equal("done: pickup 11", page.Status);
        Assert.Null(page.Message);

        // Every action is in the Console.
        Assert.Contains(app.Engine.GetRecentLog(), l => l.Source == "control" && l.Message == "done: jump r2 -");

        // Rejecting a drop asks first.
        backend.Calls.Clear();
        page.Drops[0].RejectCommand.Execute(11);
        await AnswerAsync(page, yes: false);
        await page.LastAction;
        Assert.Empty(backend.Calls);
        Assert.StartsWith("Cancelled", page.Status);
        page.Drops[0].RejectCommand.Execute(11);
        Assert.Contains("gone for good", page.ConfirmText);
        await AnswerAsync(page, yes: true);
        await page.LastAction;
        Assert.Equal(["reject 11"], backend.Calls);
    }

    [Fact]
    public async Task SpendingAndMovingAskWithQuantityAndPrice()
    {
        (TestApp app, FakeControlBackend backend, ControlViewModel page) = await OpenAsync();
        await using TestApp _ = app;

        // Shop: Buy shows quantity and price, which follow the quantity; refused items say why and never ask.
        page.SelectTabCommand.Execute("Shop");
        page.ShopIdInput = "77";
        page.LoadShopCommand.Execute(null);
        await page.LastAction;
        Assert.Equal("Pixie Shop (#77)", page.ShopTitle);
        Assert.Equal(3, page.ShopItems.Count);
        Assert.Equal("10 gold", page.ShopItems[0].Price);
        Assert.False(page.ShopItems[1].CanBuy);
        Assert.Equal("Not enough gold.", page.ShopItems[1].Requirement);
        Assert.Contains("Pixie Wing", page.ShopItems[2].Requirement);
        page.BuyCommand.Execute(page.ShopItems[1].Item);
        Assert.False(page.IsConfirming);
        Assert.Equal("Fancy Hat: Not enough gold.", page.Message);

        page.BuyCommand.Execute(page.ShopItems[0].Item);
        await Sandbox.Until(() => page.IsConfirming);
        Assert.True(page.ConfirmHasQuantity);
        Assert.Equal(50, page.ConfirmQuantityMax);
        Assert.Equal("Buy 1 x Health Potion for 10 gold?", page.ConfirmText);
        page.ConfirmQuantity = 5;
        Assert.Equal("Buy 5 x Health Potion for 50 gold?", page.ConfirmText);
        page.ConfirmCommand.Execute(null);
        await page.LastAction;
        // One bundle goes through the game's own buy (-1); any other quantity is sent as a quantity.
        page.BuyCommand.Execute(page.ShopItems[0].Item);
        await AnswerAsync(page, yes: true);
        await page.LastAction;
        Assert.Equal(["shop 77", "buy 40 9001 5", "buy 40 9001 -1"], backend.Calls);

        // Sell (backpack, while a shop is loaded): asks with the stack size.
        page.SelectTabCommand.Execute("Bank");
        await page.RefreshNowAsync();
        Assert.Equal("Bank 1/20 · backpack 2/40", page.BankSummary);
        ItemLine boots = page.InventoryItems.Single(i => i.Name == "Old Boots");
        Assert.True(boots.CanSell);
        Assert.False(page.InventoryItems.Single(i => i.Name == "Default Sword").CanMove); // equipped
        boots.SellCommand.Execute(30);
        await Sandbox.Until(() => page.IsConfirming);
        Assert.Equal("Sell 3 x Old Boots? A sale cannot be undone.", page.ConfirmText);
        page.ConfirmQuantity = 2;
        page.ConfirmCommand.Execute(null);
        await page.LastAction;
        // Bank moves ask too.
        page.BankItems[0].MoveCommand.Execute(11);
        await AnswerAsync(page, yes: true);
        await page.LastAction;
        boots.MoveCommand.Execute(30);
        await AnswerAsync(page, yes: false);
        await page.LastAction;
        Assert.Equal(["sell 30 2", "toinv 11"], backend.Calls.Skip(3));

        // Quest: requirements, progress and rewards; turn-in asks with what it uses and gives.
        page.SelectTabCommand.Execute("Quest");
        page.QuestIdInput = "1000";
        page.LoadQuestCommand.Execute(null);
        await page.LastAction;
        Assert.Equal("Pixie Problem (#1000)", page.QuestTitle);
        Assert.Equal(["Pixie Dust: 1/3"], page.QuestProgress);
        Assert.Equal(["100 gold", "50 XP", "1 x Pixie Crown"], page.QuestRewards);
        Assert.True(page.CanTurnInQuest);
        Assert.False(page.CanAcceptQuest);
        page.TurnInQuestCommand.Execute(null);
        await Sandbox.Until(() => page.IsConfirming);
        Assert.Contains("Uses: 3 x Pixie Dust", page.ConfirmText);
        Assert.Contains("Rewards: 100 gold, 50 XP, 1 x Pixie Crown", page.ConfirmText);
        page.ConfirmCommand.Execute(null);
        await page.LastAction;
        Assert.Equal(["quest 1000", "turnin 1000"], backend.Calls.Skip(5));

        // Back closes the confirmation as Cancel.
        page.LoadBankCommand.Execute(null);
        await page.LastAction;
        page.BankItems[0].MoveCommand.Execute(11);
        await Sandbox.Until(() => page.IsConfirming);
        Assert.True(page.CloseOverlay());
        await page.LastAction;
        Assert.False(page.IsConfirming);
        Assert.Equal("bank", backend.Calls[^1]);
    }

    [Fact]
    public async Task WhileAScriptRunsEveryActionAsksFirst()
    {
        (TestApp app, FakeControlBackend backend, ControlViewModel page) = await OpenAsync();
        await using TestApp _ = app;
        page.Apply(EngineSnapshot.Initial with { Script = new ScriptStatus(ScriptRunState.Running, "/s/Farm.cs", DateTimeOffset.UtcNow, null) });
        Assert.True(page.ScriptBusy);
        Assert.Contains("Farm", page.ScriptText);

        page.JumpToCellCommand.Execute("r2");
        await Sandbox.Until(() => page.IsConfirming);
        Assert.Equal("A script is running", page.ConfirmTitle);
        Assert.Contains("do it anyway?", page.ConfirmText);
        page.CancelConfirmCommand.Execute(null);
        await page.LastAction;
        Assert.Empty(backend.Calls);

        page.JumpToCellCommand.Execute("r2");
        await AnswerAsync(page, yes: true);
        await page.LastAction;
        Assert.Equal(["jump r2 -"], backend.Calls);

        // Spending while a script runs: the script question, then the purchase question.
        page.SelectTabCommand.Execute("Shop");
        await page.RefreshNowAsync();
        page.BuyCommand.Execute(page.ShopItems[0].Item);
        await AnswerAsync(page, yes: true);
        await Sandbox.Until(() => page.IsConfirming && page.ConfirmTitle == "Buy");
        page.ConfirmCommand.Execute(null);
        await page.LastAction;
        Assert.Equal("buy 40 9001 -1", backend.Calls[^1]);
    }

    [Fact]
    public async Task FailuresAreShownAndLoggedAndActionsRunOneAtATime()
    {
        (TestApp app, FakeControlBackend backend, ControlViewModel page) = await OpenAsync();
        await using TestApp _ = app;

        backend.Fail = new EngineException("Skill 2 is not ready.");
        page.UseSkillCommand.Execute(2);
        await page.LastAction;
        Assert.Equal("Skill 2 is not ready.", page.Message);
        Assert.Contains(app.Engine.GetRecentLog(), l => l.Source == "control" && l.Level == EngineLogLevel.Warning && l.Message.Contains("not ready"));

        // An unexpected failure is caught and reported, never thrown into the UI.
        backend.Fail = new InvalidOperationException("boom");
        page.RestCommand.Execute(null);
        await page.LastAction;
        Assert.Equal("Rest failed: boom", page.Message);
        Assert.Contains(app.Engine.GetRecentLog(), l => l.Source == "control" && l.Level == EngineLogLevel.Error && l.Message.Contains("InvalidOperationException"));
        Assert.False(page.IsWorking);

        // One action at a time.
        using ManualResetEventSlim gate = new();
        backend.Gate = gate;
        page.RestCommand.Execute(null);
        Task first = page.LastAction;
        await Sandbox.Until(() => page.IsWorking);
        page.ResurrectCommand.Execute(null);
        await page.LastAction;
        Assert.Equal("Wait: \"Rest\" is still running.", page.Message);
        gate.Set();
        await first;
        Assert.DoesNotContain("resurrect", backend.Calls);

        // Input checks stay on the page.
        page.ShopIdInput = "abc";
        page.LoadShopCommand.Execute(null);
        Assert.Equal("Enter a shop ID (a number).", page.Message);
        page.MapInput = " ";
        page.JoinMapCommand.Execute(null);
        Assert.Equal("Enter a map name.", page.Message);
    }

    [Fact]
    public async Task JoinedMapsAreRememberedNewestFirst()
    {
        (TestApp app, FakeControlBackend backend, ControlViewModel page) = await OpenAsync();
        await using TestApp _ = app;
        foreach (string map in new[] { "battleon", "doomwood", "battleon" })
        {
            page.MapInput = map;
            page.JoinMapCommand.Execute(null);
            await page.LastAction;
        }
        Assert.Equal(["doomwood"], page.RecentMaps.Skip(1).Select(m => m.Name));
        Assert.Equal(["battleon", "doomwood"], app.Settings.Current.RecentMaps);
        // A failed join is not remembered.
        backend.Fail = new EngineException("Could not join nowhere: not available.");
        page.MapInput = "nowhere";
        page.JoinMapCommand.Execute(null);
        await page.LastAction;
        Assert.Equal(["battleon", "doomwood"], app.Settings.Current.RecentMaps);
        // A recent map chip joins it again; a new page reads the list back.
        page.RecentMaps[1].UseCommand.Execute("doomwood");
        await page.LastAction;
        Assert.Equal("join doomwood", backend.Calls[^1]);
        ControlViewModel again = new(app.Services);
        Assert.Equal(["doomwood", "battleon"], again.RecentMaps.Select(m => m.Name));
    }

    [Fact]
    public async Task TheMapIsReadAtMostAFewTimesASecondAndNeverInTheBackground()
    {
        FakePluginBackends plugins = new();
        await using TestApp app = new(scripts: new PluginOnlyScriptHost(plugins));
        FakeControlBackend backend = plugins.FakeControl;
        ControlViewModel page = new(app.Services);
        long version = 100;
        EngineSnapshot Next() => EngineSnapshot.Initial with { WorldVersion = ++version };

        // Not open: nothing is read, however much the world changes.
        for (int i = 0; i < 20; i++)
            page.Apply(Next());
        await Task.Delay(500);
        Assert.Equal(0, backend.Reads);

        // Open: a busy world (a change every 10 ms for 2 s) gives at most one read per interval.
        page.IsActive = true;
        DateTime end = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < end)
        {
            page.Apply(Next());
            await Task.Delay(10);
        }
        await Task.Delay(ControlViewModel.RefreshInterval * 2);
        int open = backend.Reads;
        Assert.InRange(open, 2, 8); // 2 s / 400 ms = 5, plus the first read
        Assert.Equal(open, page.Refreshes);

        // The app goes to the background: no reads at all, even while the world keeps changing.
        page.SetUiVisible(false);
        await Task.Delay(ControlViewModel.RefreshInterval * 2);
        int hidden = backend.Reads;
        for (int i = 0; i < 50; i++)
        {
            page.Apply(Next());
            await Task.Delay(10);
        }
        await Task.Delay(ControlViewModel.RefreshInterval * 2);
        Assert.Equal(hidden, backend.Reads);

        // Back in the foreground: the latest state is read once.
        page.SetUiVisible(true);
        await Sandbox.Until(() => backend.Reads == hidden + 1);
        await Task.Delay(ControlViewModel.RefreshInterval * 2);
        Assert.Equal(hidden + 1, backend.Reads);

        // A list whose content did not change keeps its instance (no rebuild of its rows).
        IReadOnlyList<CellTile> tiles = page.Tiles;
        page.Apply(Next());
        await Sandbox.Until(() => backend.Reads == hidden + 2);
        await Task.Delay(50);
        Assert.Same(tiles, page.Tiles);
        backend.State = backend.State with { Cells = [new("Enter", 0, 0, 0, true)] };
        page.Apply(Next());
        await Sandbox.Until(() => page.Tiles.Count == 1);
    }

    [Fact]
    public async Task ControlIsInTheBottomBarAndOpensTheWorldLists()
    {
        await using TestApp app = new(scripts: new PluginOnlyScriptHost(new FakePluginBackends()));
        using ShellViewModel shell = new(app.Services);
        shell.NavigateCommand.Execute("Control");
        Assert.Same(shell.Control, shell.CurrentContent);
        Assert.True(shell.Control.IsActive);
        Assert.True(shell.IsControlPage);
        Assert.Equal("Control", shell.Title);
        shell.Control.OpenListsCommand.Execute(null);
        Assert.Equal(AppPage.World, shell.CurrentPage);
        Assert.True(shell.IsControlPage); // the World lists belong to Control in the bar
        Assert.False(shell.Control.IsActive);
        Assert.True(shell.GoBack());
        Assert.Equal(AppPage.Control, shell.CurrentPage);
        Assert.True(shell.GoBack());
        Assert.Equal(AppPage.Home, shell.CurrentPage);
    }

    [Fact]
    public async Task WithoutASkuaHostThePageSaysSo()
    {
        await using TestApp app = new();
        ControlViewModel page = new(app.Services);
        Assert.False(page.IsAvailable);
        Assert.Contains("not available", page.Message);
        page.RestCommand.Execute(null);
        await page.LastAction;
        Assert.Contains("not available", page.Message);
    }
}

public partial class SessionIntegrationTests
{
    /// <summary>Retries an action the game may refuse for a moment (a skill on cooldown, a monster respawning).</summary>
    private static async Task<string> Eventually(Func<string> action, int timeoutMs = 15000)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return await Task.Run(action);
            }
            catch (EngineException) when (sw.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(200);
            }
        }
    }

    private static HttpResponseMessage MapFile(byte[] swf) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(swf) };

    /// <summary>
    /// The Control page's backend on the real Skua host over the fake server: the map's cells from its map file (and
    /// from the room when the file cannot be read), moving to cells and pads, attack, skills, drops, /goto, a shop with
    /// its purchase checks, buying and selling, a quest from load to turn-in, the bank, and joining maps.
    /// </summary>
    [Fact]
    public async Task TheControlBackendDrivesTheGameThroughSkua()
    {
        await using FakeAqw aqw = new();
        aqw.Http.Routes["gamefiles/maps/test/testmap.swf"] = _ => MapFile(SyntheticSwf.Map(new[] { ("Enter", new[] { "Spawn" }), ("r2", new[] { "Left", "Right" }), ("r3", new[] { "Spawn" }), ("r4", new[] { "Down" }) }));
        aqw.Http.Routes["gamefiles/maps/test/shopmap.swf"] = _ => MapFile(SyntheticSwf.Map(new[] { ("Enter", new[] { "Spawn" }) }, "ZWS"));
        SkuaScriptHostAdapter host = new(null, new ScriptAssemblyStore(), ClientFileSources.SkuaScriptsDIR, o =>
        {
            o.WebApi = new AqwWebApi(new AqwWebApiOptions { BaseUri = FakeAqw.BaseUri }, aqw.Http); // the bank loads over HTTPS from game.aq.com
            o.Game = new HeadlessGameOptions
            {
                Session = aqw.NewSession().Options,
                Client = new GameClientOptions { MapLoadTime = TimeSpan.FromMilliseconds(200) },
                DownloadMapCells = true,
            };
        });
        HeadlessBotEngine engine = new(new HeadlessEngineOptions
        {
            SessionFactory = () => throw new InvalidOperationException("the host owns the session"),
            PublishInterval = TimeSpan.FromMilliseconds(20),
            MemorySampleInterval = TimeSpan.FromMilliseconds(100),
        }, host);
        IControlBackend control = Assert.IsAssignableFrom<IControlBackend>(engine.Plugins?.Control);
        try
        {
            Assert.False(control.Read().Online);
            Assert.Contains("Log in", Assert.Throws<EngineException>(() => control.Jump("r2", null)).Message);
            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => engine.Snapshot.Character is not null && host.Skua!.Bot.Map.Loaded);
            FakeAqwServer.Session game = aqw.Server.LastSession!;
            int Count(string command) => game.Received.Count(r => r.Text.Contains($"%{command}%", StringComparison.Ordinal));

            // The map: cells from the map file (r4 has nobody), counts, the character's cell and its pads.
            await Sandbox.Until(() => control.Read().Source == CellSource.MapFile);
            ControlState s = control.Read();
            Assert.True(s.Online);
            Assert.Equal("testmap", s.Map);
            Assert.Equal(["Enter", "r2", "r3", "r4"], s.Cells.Select(c => c.Name));
            Assert.Equal(new ControlCell("Enter", 0, 0, 1, true), s.Cells[0]);
            Assert.Equal(new ControlCell("r2", 2, 2, 0, false), s.Cells[1]);
            Assert.Equal(new ControlCell("r3", 1, 1, 0, false), s.Cells[2]);
            Assert.Equal(new ControlCell("r4", 0, 0, 0, false), s.Cells[3]);
            Assert.Equal(["Spawn"], s.Pads);
            Assert.Empty(s.Monsters);
            ControlPlayer other = Assert.Single(s.Players);
            Assert.Equal(("OtherPlayer", 12, "idle"), (other.Name, other.Level, other.StateText));
            Assert.Equal(["Heal", "Smite", "Ward", "Judgement"], s.Skills.Select(k => k.Name));
            Assert.Equal(["OtherPlayer"], s.RoomPlayers.Select(p => p.Name));

            // Tap a tile: the cell's default pad (r2 has no Spawn: Left); then a pad from the selector.
            Assert.Equal("Moved to r2 (Left).", control.Jump("r2", null));
            await Sandbox.Until(() => game.Cell == "r2" && game.Pad == "Left");
            s = control.Read();
            Assert.True(s.Cells[1].IsCurrent);
            Assert.Equal(["Left", "Right"], s.Pads);
            Assert.Equal([1, 2], s.Monsters.Select(m => m.MonMapId));
            Assert.Equal(("Pixie", 3, "idle"), (s.Monsters[0].Name, s.Monsters[0].Level, s.Monsters[0].StateText));
            control.Jump("r2", "Right");
            await Sandbox.Until(() => game.Pad == "Right");
            Assert.Throws<EngineException>(() => control.Jump("r2%xt%", null)); // never into a packet

            // Attack (target + auto attack through the client rules); a monster of another cell is refused.
            Assert.Contains("is in r3", Assert.Throws<EngineException>(() => control.Attack(3)).Message);
            Assert.Equal("Attacking Pixie.", control.Attack(1));
            Assert.True(control.Read().Monsters.Single(m => m.MonMapId == 1).Targeted);
            Assert.Equal("Pixie (#1)", control.Read().Target);
            await Sandbox.Until(() => Count("gar") > 0);
            Assert.Equal("Skills are 1 to 5.", Assert.Throws<EngineException>(() => control.UseSkill(9)).Message);
            Assert.Contains("no skill 5", Assert.Throws<EngineException>(() => control.UseSkill(5)).Message); // no potion slot here
            Assert.Equal("Used Smite.", await Eventually(() => control.UseSkill(2)));
            // The kill drops a Pixie Wing: pick it up; the next one is rejected.
            await Sandbox.Until(() => control.Read().Drops.Any(d => d.ItemId == 11), 20000);
            Assert.Equal("Picked up Pixie Wing.", await Task.Run(() => control.PickupDrop(11)));
            Assert.Contains(game.Inventory, i => (int)i["ItemID"]! == 11);
            await Eventually(() => control.Attack(2));
            await Sandbox.Until(() => control.Read().Drops.Any(d => d.ItemId == 11), 20000);
            Assert.Equal("Rejected Pixie Wing.", control.RejectDrop(11));
            Assert.DoesNotContain(control.Read().Drops, d => d.ItemId == 11);
            Assert.Equal("Target cleared.", control.CancelTarget());
            Assert.Null(control.Read().Target);

            // Out of combat: rest, /goto; resurrect only when dead.
            game.Str("uotls", "hero", "intState:1");
            await Sandbox.Until(() => !control.Read().InCombat);
            Assert.Equal("Resting.", await Task.Run(control.Rest));
            await Sandbox.Until(() => Count("restRequest") > 0);
            Assert.Contains("alive", Assert.Throws<EngineException>(control.Resurrect).Message);
            Assert.Equal("Sent /goto SomeFriend.", await Task.Run(() => control.GotoPlayer("SomeFriend")));
            await Sandbox.Until(() => game.Received.Any(r => r.Text.Contains("%goto%somefriend%", StringComparison.Ordinal)));
            Assert.Throws<EngineException>(() => control.GotoPlayer("x%y"));
            Assert.Throws<EngineException>(() => control.GotoPlayer("Hero")); // this character

            // Shop: the client's purchase checks per item, a purchase with a quantity, refusals, a sale.
            Assert.Null(control.ReadShop());
            Assert.Equal("Loaded Pixie Shop (4 items).", await Task.Run(() => control.LoadShop(FakeAqwServer.ShopId)));
            ControlShop shop = control.ReadShop()!;
            ControlShopItem potion = shop.Items.Single(i => i.ItemId == 40);
            Assert.True(potion.CanBuy);
            Assert.Equal("10 gold", potion.PriceText);
            Assert.True(potion.MaxBuys >= 50);
            Assert.Equal("Not enough gold.", shop.Items.Single(i => i.ItemId == 41).Refusal);
            Assert.Equal("Member only.", shop.Items.Single(i => i.ItemId == 42).Refusal);
            Assert.Equal(["Missing 1 x Pixie Wing in inventory."], shop.Items.Single(i => i.ItemId == 43).Unmet);
            Assert.Equal("Bought 3 x Health Potion.", await Task.Run(() => control.Buy(40, 9001, 3)));
            Assert.Equal([(40, 3)], game.Purchases);
            Assert.Equal("Bought 1 x Health Potion.", await Task.Run(() => control.Buy(40, 9001, -1)));
            Assert.Equal("Not enough gold.", Assert.Throws<EngineException>(() => control.Buy(41, 9002, -1)).Message);
            Assert.Contains("Pixie Wing", Assert.Throws<EngineException>(() => control.Buy(43, 9004, -1)).Message);
            Assert.Equal(2, game.Purchases.Count);
            Assert.Equal("Sold 1 x Old Boots.", await Task.Run(() => control.Sell(30, -1)));
            Assert.Equal([(30, 1)], game.Sales);
            Assert.Contains("equipped", Assert.Throws<EngineException>(() => control.Sell(1, -1)).Message);

            // Quest: load, requirements, progress and rewards, accept, turn in.
            Assert.Null(control.ReadQuest(1000));
            Assert.Equal("Loaded quest 1000: Pixie Problem.", await Task.Run(() => control.LoadQuest(1000)));
            ControlQuest quest = control.ReadQuest(1000)!;
            Assert.False(quest.Accepted);
            Assert.Null(quest.AcceptRefusal);
            Assert.Equal(["Pixie Dust: 0/3"], quest.Progress.Select(p => p.Text));
            Assert.Equal(["100 gold", "50 XP", "1 x Pixie Crown"], quest.Rewards);
            Assert.Contains("Accept", Assert.Throws<EngineException>(() => control.TurnInQuest(1000)).Message);
            Assert.Equal("Accepted Pixie Problem.", await Task.Run(() => control.AcceptQuest(1000)));
            Assert.Contains(1000, game.Accepted);
            Assert.Contains("Pixie Dust: 0/3", Assert.Throws<EngineException>(() => control.TurnInQuest(1000)).Message);
            JsonObject dust = FakeAqwServer.Copy(10);
            dust["iQty"] = 3;
            game.Temp.Add((JsonObject)dust.DeepClone());
            dust["iQtyNow"] = 3;
            dust["bTemp"] = 1;
            game.Json(new JsonObject { ["cmd"] = "addItems", ["items"] = new JsonObject { ["10"] = dust } });
            await Sandbox.Until(() => control.ReadQuest(1000)!.Completable);
            Assert.Equal("Turned in Pixie Problem.", await Task.Run(() => control.TurnInQuest(1000)));
            Assert.DoesNotContain(1000, game.Accepted);
            Assert.Contains("did not load", (await Assert.ThrowsAsync<EngineException>(() => Task.Run(() => control.LoadQuest(4242)))).Message);

            // Bank: load, then a stack to the bank and back; equipped items stay.
            Assert.Contains("Load the bank", Assert.Throws<EngineException>(() => control.InventoryToBank(40)).Message);
            Assert.Equal("Bank loaded: 1 items.", await Task.Run(control.LoadBank));
            Assert.Equal(["Pixie Wing"], control.ReadBank().Bank.Select(b => b.Name));
            Assert.Equal("Moved Health Potion to the bank.", await Task.Run(() => control.InventoryToBank(40)));
            Assert.Contains(control.ReadBank().Bank, b => b.ItemId == 40);
            Assert.Equal("Moved Health Potion to the backpack.", await Task.Run(() => control.BankToInventory(40)));
            Assert.Contains(control.ReadBank().Inventory, b => b.ItemId == 40);
            Assert.Contains("equipped", Assert.Throws<EngineException>(() => control.InventoryToBank(1)).Message);

            // Join: refusals and bad names stay on the page; a map whose file cannot be read (LZMA) shows the room's cells.
            Assert.Throws<EngineException>(() => control.JoinMap("bad%map"));
            Assert.Contains("not available", (await Assert.ThrowsAsync<EngineException>(() => Task.Run(() => control.JoinMap("nosuchmap")))).Message);
            Assert.StartsWith("Joined shopmap-", await Task.Run(() => control.JoinMap("shopmap")));
            await Sandbox.Until(() => control.Read() is { Map: "shopmap", Source: CellSource.RoomData });
            Assert.Equal(["Enter"], control.Read().Cells.Select(c => c.Name));
            Assert.Equal(ControlBackend.CommonPads, control.Read().Pads);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }
}
