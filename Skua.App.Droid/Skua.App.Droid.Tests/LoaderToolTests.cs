using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Tools;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.Plugins.Tools;
using Skua.App.Droid.Scripting;
using Skua.Core.Models;
using Skua.Core.Models.Quests;

namespace Skua.App.Droid.Tests;

public partial class ToolsTests
{
    [Fact]
    public void TheLoaderRulesAreTheDesktops()
    {
        Assert.True(LoaderText.IsIdList("1, 2 3"));
        Assert.False(LoaderText.IsIdList("1;2"));
        Assert.False(LoaderText.IsIdList(" , "));
        Assert.Equal([1, 2, 3], LoaderText.ParseIds("1, 2 3"));
        Assert.True(LoaderText.QuestMatches(1000, "Pixie Problem", "pixie"));
        Assert.True(LoaderText.QuestMatches(1000, "Pixie Problem", "100"));
        Assert.False(LoaderText.QuestMatches(1000, "Pixie Problem", "queen"));
        Assert.Equal("// A | 1" + Environment.NewLine + "// B | 2", LoaderText.NamesAndIds([("A", 1), ("B", 2)]));
        Assert.Equal("Invalid format. Use: startID, endID", LoaderText.ParseRange("5", out _, out _));
        Assert.Equal("Start ID must be less than or equal to End ID", LoaderText.ParseRange("9, 5", out _, out _));
        Assert.Null(LoaderText.ParseRange("5, 9", out int a, out int b));
        Assert.Equal((5, 9), (a, b));
        Assert.True(FastTravelEntry.TryParse("Nulgath,tercessuinotlim,Boss2,Right", out FastTravelEntry? e));
        Assert.Equal("Nulgath,tercessuinotlim,Boss2,Right", e!.ToString());
        Assert.False(FastTravelEntry.TryParse("a,b,c", out _));
    }

    [Fact]
    public async Task TheLoaderLoadsByIdSearchesCopiesAndUpdatesTheList()
    {
        (TestApp app, FakeGameTools tools) = NewApp();
        await using TestApp _ = app;
        FakeLoader backend = tools.FakeLoader;
        LoaderViewModel page = new(app.Services);
        page.Refresh();
        await Sandbox.Until(() => page.Quests.Count == 3);
        Assert.Equal("3 of 3 quests", page.ListText);

        // Load by ID: the desktop's check, one tap.
        Assert.False(page.LoadCommand.CanExecute(null));
        page.InputIds = "77";
        Assert.True(page.LoadCommand.CanExecute(null));
        await page.LoadCommand.ExecuteAsync(null);
        Assert.Equal("load Shop 77", backend.Calls[^1]);
        Assert.Equal(LoaderKind.Shop, page.LastLoaded);
        Assert.Equal("loaded Shop 77", page.Status);
        page.SelectedKind = nameof(LoaderKind.Quest);
        page.InputIds = "1000, 1001";
        await page.LoadCommand.ExecuteAsync(null);
        Assert.Equal("load Quest 1000, 1001", backend.Calls[^1]);
        page.SelectedKind = nameof(LoaderKind.Shop);
        page.InputIds = "404";
        await page.LoadCommand.ExecuteAsync(null);
        Assert.Equal("Shop 404 did not load.", page.Message);

        LoaderKind? viewed = null;
        page.ViewInGrabberRequested += (_, k) => viewed = k;
        page.ViewInGrabberCommand.Execute(null);
        Assert.Equal(LoaderKind.Quest, viewed);

        // Search, select, copy in the desktop's formats.
        page.SearchText = "pixie";
        Assert.Single(page.Quests);
        page.SearchText = "100";
        Assert.Equal(2, page.Quests.Count);
        await page.CopyIdsCommand.ExecuteAsync(null);
        Assert.Equal("Select quests first.", page.Message);
        page.Quests[0].IsSelected = true;
        page.Quests[1].IsSelected = true;
        Assert.Equal(2, page.SelectedCount);
        await page.CopyIdsCommand.ExecuteAsync(null);
        Assert.Equal("1000,1001", app.Picker.Copied);
        await page.CopyNamesCommand.ExecuteAsync(null);
        Assert.Equal("Pixie Problem,Map item", app.Picker.Copied);
        await page.CopyNamesAndIdsCommand.ExecuteAsync(null);
        Assert.Equal("// Pixie Problem | 1000" + Environment.NewLine + "// Map item | 1001", app.Picker.Copied);
        await page.LoadSelectedCommand.ExecuteAsync(null);
        Assert.Equal("quests 1000,1001", backend.Calls[^1]);
        await page.Quests[0].FakeCompleteCommand.ExecuteAsync(null);
        Assert.Equal("fake 1000", backend.Calls[^1]);
        page.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, page.SelectedCount);

        // While a script runs, loading asks first.
        page.Apply(ScriptRunning);
        Task load = page.Quests[0].LoadCommand.ExecuteAsync(null);
        await AnswerAsync(page.Confirm, yes: false, ConfirmHost.ScriptRunningTitle);
        await load;
        Assert.Equal("fake 1000", backend.Calls[^1]);
        page.Apply(EngineSnapshot.Initial);

        // The list update asks, reports progress, can be cancelled.
        page.RangeText = "9, 5";
        await page.UpdateRangeCommand.ExecuteAsync(null);
        Assert.Equal("Start ID must be less than or equal to End ID", page.Message);
        backend.Gate = new ManualResetEventSlim(false);
        page.RangeText = "1000, 1001";
        Task range = page.UpdateRangeCommand.ExecuteAsync(null);
        await AnswerAsync(page.Confirm, yes: true, "Update quests 1000-1001");
        await Sandbox.Until(() => page.IsUpdating && page.UpdateProgress == "Loading Quests 1000-1001...");
        page.CancelUpdateCommand.Execute(null);
        await range;
        Assert.False(page.IsUpdating);
        Assert.Contains("cancelled", page.Status);
        Task update = page.UpdateCommand.ExecuteAsync(null);
        await AnswerAsync(page.Confirm, yes: true, "Update the quest list");
        await update;
        Assert.Equal("update", backend.Calls[^1]);
        page.SearchText = string.Empty;
        Assert.Equal(4, page.Quests.Count);
        Assert.Equal("Quest list updated: 4 quests.", page.Status);
    }
}

public partial class SessionIntegrationTests
{
    /// <summary>
    /// The Loader on the real Skua host over the fake server: a shop by ID (the Control page's load), quests by ID,
    /// one that does not exist, Skua's quest list file, its range update from the game, and fake complete.
    /// </summary>
    [Fact]
    public async Task TheLoaderLoadsShopsAndQuestsThroughSkua()
    {
        await using FakeAqw aqw = new();
        (SkuaScriptHostAdapter host, HeadlessBotEngine engine) = ToolsHost(aqw);
        ILoaderBackend loader = Assert.IsAssignableFrom<IGameTools>(engine.Plugins?.GameTools).Loader;
        TimeSpan wait = LoaderBackend.QuestWait;
        LoaderBackend.QuestWait = TimeSpan.FromSeconds(1.5);
        string file = Path.Combine(ClientFileSources.SkuaDIR, LoaderBackend.QuestFile);
        File.WriteAllText(file, """[{"ID":5,"Name":"Old Quest","Level":3},{"ID":1000,"Name":"Stale name"}]""");
        try
        {
            Assert.Contains("Log in", Assert.Throws<EngineException>(() => loader.Load(LoaderKind.Quest, "1000")).Message);
            Assert.Equal(["Old Quest", "Stale name"], (await loader.GetQuestListAsync()).Select(q => q.Name));

            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => engine.Snapshot.Character is not null && host.Skua!.Bot.Map.Loaded);

            Assert.Contains("Pixie Shop", loader.Load(LoaderKind.Shop, "77"));
            Assert.Equal(77, host.Skua!.Bot.Shops.ID);
            Assert.Throws<EngineException>(() => loader.Load(LoaderKind.Shop, "77, 78"));
            Assert.Equal("Loaded 2 quests.", loader.Load(LoaderKind.Quest, "1000, 1001"));
            Assert.True(host.Skua.Bot.Quests.TryGetQuest(1001, out _));
            Assert.Contains("did not load", Assert.Throws<EngineException>(() => loader.Load(LoaderKind.Quest, "4242")).Message);
            Assert.Contains("1 of 2", loader.LoadQuests([1000, 4242]));
            Assert.Contains("this client only", loader.FakeComplete(1000));

            List<string> lines = new();
            IReadOnlyList<QuestListEntry> list = await loader.UpdateRangeAsync(1000, 1001, new Progress<string>(lines.Add), CancellationToken.None);
            Assert.Equal([5, 1000, 1001], list.Select(q => q.Id));
            Assert.Equal("Pixie Problem", list[1].Name);
            Assert.Contains("Pixie Problem", File.ReadAllText(file));
        }
        finally
        {
            LoaderBackend.QuestWait = wait;
            File.Delete(file);
            await engine.DisposeAsync();
        }
    }
}
