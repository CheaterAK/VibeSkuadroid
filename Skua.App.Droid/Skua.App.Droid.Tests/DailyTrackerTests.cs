using System.Text.Json.Nodes;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Plugins;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.Plugins.DailyTracker;
using Skua.App.Droid.Scripting;
using Skua.Core.Headless;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Quests;
using Skua.Plugins.Shared.DailyTracker;

namespace Skua.App.Droid.Tests;

/// <summary>The Daily &amp; Weekly Tracker port: the shared logic, its backend on a real Skua session, the page.</summary>
public class DailyTrackerTests
{
    [Fact]
    public void TheCatalogIsTheDesktopTrackersTable()
    {
        Assert.Equal(33, DailyQuestCatalog.TrackedQuests.Length);
        Assert.Equal(DailyQuestCatalog.TrackedQuests.Length, DailyQuestCatalog.QuestNames.Length);
        Assert.Equal(DailyQuestCatalog.TrackedQuests.Length, DailyQuestCatalog.TrackedQuests.Distinct().Count());
        // Categories are contiguous and cover every quest once (the window's Take/Skip split).
        int next = 0;
        foreach ((string _, int start, int count) in DailyQuestCatalog.Categories)
        {
            Assert.Equal(next, start);
            next += count;
        }
        Assert.Equal(DailyQuestCatalog.TrackedQuests.Length, next);
        Assert.All(DailyQuestCatalog.TrackedQuests.Where(DailyQuestCatalog.IsLordOfOrder), id => Assert.Equal(DailyQuestCatalog.LordOfOrderScript, DailyQuestCatalog.ScriptFor(id)));
        Assert.Equal(10, DailyQuestCatalog.TrackedQuests.Count(DailyQuestCatalog.IsLordOfOrder));
        Assert.Equal(["Lord of Order"], DailyQuestCatalog.Categories.Select(c => c.Name).Where(DailyQuestCatalog.HasCategoryScript));
        Assert.True(DailyQuestCatalog.ShowsStart(2091, isDone: false));
        Assert.False(DailyQuestCatalog.ShowsStart(2091, isDone: true));
        Assert.False(DailyQuestCatalog.ShowsStart(7156, isDone: false)); // started from its category
        Assert.False(DailyQuestCatalog.ShowsStart(3596 + 1, isDone: false)); // no script

        // The app's catalog: same quests, portable script paths.
        DailyTrackerBackend backend = new(() => null);
        Assert.Equal(DailyQuestCatalog.TrackedQuests, backend.Catalog.SelectMany(g => g.Quests).Select(q => q.Id));
        Assert.Equal("Dailies/LordOfOrder.cs", backend.Catalog.Single(g => g.Name == "Lord of Order").Script);
        Assert.Equal("Ultras/UltraGramiel.cs", backend.Catalog.SelectMany(g => g.Quests).Single(q => q.Id == 10301).Script);
        Assert.Throws<EngineException>(() => backend.ReadStates());
    }

    [Fact]
    public void TheStatusReaderBatchLoadsAndReadsLordOfOrderFromTheChainValue()
    {
        List<Quest> tree = new();
        List<int[]> loads = new();
        IScriptInterface bot = ScriptFake.Create<IScriptInterface>((name, args) =>
        {
            switch (name)
            {
                case "get_Tree":
                    lock (tree)
                        return tree.ToList();
                case "Load":
                    loads.Add((int[])args[0]!);
                    lock (tree)
                        tree.AddRange(DailyQuestCatalog.TrackedQuests.Select(id => new Quest { ID = id }));
                    return null;
                case "ForTrue":
                    return ((Func<bool>)args[0]!)();
                case "IsDailyComplete" when args[0] is int id:
                    return id switch
                    {
                        2091 => true,
                        802 => throw new InvalidOperationException("flash error"),
                        _ => false,
                    };
                case "CallGameFunction" when args[0] is "world.getQuestValue":
                    return "3";
                default:
                    return null;
            }
        });

        DailyQuestStatusReader.EnsureAllQuestsLoaded(bot);
        Assert.Single(loads);
        Assert.Equal(DailyQuestCatalog.TrackedQuests, loads[0]);
        DailyQuestStatusReader.EnsureAllQuestsLoaded(bot); // all known now: no second request
        Assert.Single(loads);

        Assert.True(DailyQuestStatusReader.TryReadState(bot, 2091, out bool done, out Exception? error) && done && error is null);
        Assert.False(DailyQuestStatusReader.TryReadState(bot, 802, out done, out error));
        Assert.False(done);
        Assert.IsType<InvalidOperationException>(error);
        // The chain value is 3: the first three steps are done, the fourth is not.
        foreach (int id in new[] { 7156, 7157, 7158 })
            Assert.True(DailyQuestStatusReader.TryReadState(bot, id, out done, out _) && done, $"{id}");
        Assert.True(DailyQuestStatusReader.TryReadState(bot, 7159, out done, out _));
        Assert.False(done);
        Assert.True(DailyQuestStatusReader.TryReadState(bot, 8152, out done, out _));
        Assert.False(done);
    }

    [Fact]
    public async Task ThePageRefreshesKeepsFailedChecksFollowsTurnInsAndStartsScripts()
    {
        FakePluginBackends plugins = new();
        FakeDailyTracker tracker = plugins.FakeDailyTracker;
        await using TestApp app = new(scripts: new PluginOnlyScriptHost(plugins));
        using DailyTrackerViewModel page = new(app.Services, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
        Assert.True(page.IsAvailable);
        Assert.Equal(["Resources", "Lord of Order"], page.Groups.Select(g => g.Name));
        Assert.True(page.Groups[1].HasScript);
        Assert.False(page.RefreshCommand.CanExecute(null)); // offline
        DailyQuestRow first = page.Groups[0].Quests[0];
        DailyQuestRow second = page.Groups[0].Quests[1];
        DailyQuestRow lordOfOrder = page.Groups[1].Quests[0];
        Assert.Null(first.IsDone);
        Assert.Equal("Unknown", first.StatusText);

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        await Sandbox.Until(() => app.Engine.Snapshot.IsOnline);
        page.Apply(app.Engine.Snapshot);
        Assert.True(page.RefreshCommand.CanExecute(null));

        await page.RefreshCoreAsync();
        Assert.False(first.IsDone);
        Assert.True(first.ShowStart);
        Assert.True(second.IsDone);
        Assert.False(second.ShowStart); // done (and no script)
        Assert.False(lordOfOrder.ShowStart); // started from the category
        Assert.Equal("Incomplete", first.StatusText);
        Assert.StartsWith("1 of 3 done", page.Summary);

        // A failed check keeps what was shown.
        lock (tracker.States)
            tracker.States[2] = null;
        await page.RefreshCoreAsync();
        Assert.True(second.IsDone);

        // A turn-in marks the quest done at once and checks again shortly after.
        int reads = tracker.Reads;
        lock (tracker.States)
            tracker.States[1] = true;
        tracker.TurnIn(1);
        Assert.True(first.IsDone);
        await Sandbox.Until(() => Volatile.Read(ref tracker.Reads) > reads);

        // The backend failing shows its message.
        tracker.Failure = () => new EngineException("Log in first.");
        await page.RefreshCoreAsync();
        Assert.Equal("Log in first.", page.Message);
        tracker.Failure = () => null;

        // Start: a script that was not synced, then one that exists (this engine has no script host: its reason).
        await page.StartScriptCommand.ExecuteAsync("Dailies/First.cs");
        Assert.Contains("Sync the community scripts", page.Message);
        Directory.CreateDirectory(Path.Combine(app.Paths.ScriptsDir, "Dailies"));
        File.WriteAllText(Path.Combine(app.Paths.ScriptsDir, "Dailies", "First.cs"), "class A {}");
        await page.StartScriptCommand.ExecuteAsync("Dailies\\First.cs");
        Assert.Equal(UnavailableScriptHost.DefaultReason, page.Message);

        // Logging out forgets the states (the next login may be another character).
        await app.Host.LogoutAsync();
        await Sandbox.Until(() => !app.Engine.Snapshot.IsOnline);
        page.Apply(app.Engine.Snapshot);
        Assert.Null(first.IsDone);
    }

    [Fact]
    public async Task WithoutAPluginHostThePageSaysWhy()
    {
        await using TestApp app = new();
        using DailyTrackerViewModel page = new(app.Services);
        Assert.False(page.IsAvailable);
        Assert.Empty(page.Groups);
        Assert.NotNull(page.Message);
    }
}

public partial class SessionIntegrationTests
{
    /// <summary>
    /// The tracker's backend on the real Skua host over the fake server: the missing quests are batch-loaded with one
    /// request, daily completion is read through Skua (achievement bits), Lord of Order from the chain's quest value,
    /// and a quest turn-in reaches the page.
    /// </summary>
    [Fact]
    public async Task TheDailyTrackerReadsTheCharacterThroughSkua()
    {
        await using FakeAqw aqw = new();
        SkuaScriptHostAdapter host = new(null, new ScriptAssemblyStore(), ClientFileSources.SkuaScriptsDIR, o =>
        {
            o.WebApi = new AqwWebApi(new AqwWebApiOptions { BaseUri = new Uri("https://example.test/game/") }, aqw.Http);
            o.Game = new HeadlessGameOptions
            {
                Session = aqw.NewSession().Options,
                Client = new GameClientOptions { MapLoadTime = TimeSpan.FromMilliseconds(200) },
                DownloadMapCells = false,
            };
        });
        HeadlessBotEngine engine = new(new HeadlessEngineOptions
        {
            SessionFactory = () => throw new InvalidOperationException("the host owns the session"),
            PublishInterval = TimeSpan.FromMilliseconds(20),
            MemorySampleInterval = TimeSpan.FromMilliseconds(100),
        }, host);
        try
        {
            IDailyTrackerBackend tracker = Assert.IsAssignableFrom<IDailyTrackerBackend>(engine.Plugins?.DailyTracker);
            await Assert.ThrowsAsync<EngineException>(() => Task.Run(() => tracker.ReadStates()));

            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => engine.Snapshot.Character is not null);
            FakeAqwServer.Session game = aqw.Server.LastSession!;

            // Every tracked quest but the last is known already; the fake character has no "ia*" achievement fields, so
            // Mine Crafting points at a numeric field whose bit 0 is set (CharID 777) to exercise the same bit test.
            JsonObject Quests(IEnumerable<int> ids)
            {
                JsonObject quests = new();
                int index = 0;
                foreach (int id in ids)
                {
                    quests[id.ToString()] = new JsonObject
                    {
                        ["QuestID"] = id, ["sName"] = $"Quest {id}", ["iLvl"] = 1, ["bUpg"] = 0, ["bOnce"] = 0, ["iSlot"] = -1, ["iValue"] = 0,
                        ["sField"] = id == 2091 ? "CharID" : "ia1", ["iIndex"] = id == 2091 ? 0 : index++ % 30,
                    };
                }
                return new JsonObject { ["cmd"] = "getQuests", ["quests"] = quests };
            }
            int[] tracked = DailyQuestCatalog.TrackedQuests;
            game.Json(Quests(tracked[..^1]));
            game.Json(new JsonObject { ["cmd"] = "updateQuest", ["iValue"] = 3, ["iIndex"] = DailyQuestCatalog.LordOfOrderSlot });
            await Sandbox.Until(() => host.Skua!.Bot.Quests.Tree.Count >= tracked.Length - 1);

            Task<IReadOnlyDictionary<int, bool?>> read = Task.Run(() => tracker.ReadStates());
            // One batch request for all tracked quests (Skua sends up to 30 per packet).
            await Sandbox.Until(() => game.Received.Any(r => r.Text.Contains("%getQuests%", StringComparison.Ordinal) && r.Text.Contains("%10301%", StringComparison.Ordinal)));
            game.Json(Quests([tracked[^1]]));
            IReadOnlyDictionary<int, bool?> states = await read.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(tracked.Length, states.Count);
            Assert.True(states[2091]);
            Assert.True(states[7156]);
            Assert.True(states[7158]);
            Assert.False(states[7159]);
            Assert.False(states[8152]);
            Assert.False(states[10301]);
            Assert.All(states.Values, v => Assert.NotNull(v));

            // A turn-in (the server's ccqr) reaches the backend's listeners.
            List<int> turnedIn = new();
            tracker.QuestTurnedIn += (_, id) =>
            {
                lock (turnedIn)
                    turnedIn.Add(id);
            };
            game.Json(new JsonObject { ["cmd"] = "ccqr", ["QuestID"] = 2091, ["bSuccess"] = 1, ["sName"] = "Mine Crafting", ["rewardObj"] = new JsonObject() });
            await Sandbox.Until(() =>
            {
                lock (turnedIn)
                    return turnedIn.Contains(2091);
            });
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }
}
