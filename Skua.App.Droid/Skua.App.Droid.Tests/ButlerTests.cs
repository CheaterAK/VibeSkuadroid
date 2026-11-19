using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Plugins;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.Plugins.Butler;
using Skua.App.Droid.Scripting;
using Skua.Core.Headless;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Models;

namespace Skua.App.Droid.Tests;

/// <summary>The ButlerV5 port: its page and options, and the single-account follower on a real Skua session.</summary>
public class ButlerTests
{
    [Fact]
    public void TheLogCountsRepeatsAndKeepsDebugLinesOnlyWhenAsked()
    {
        ButlerLog log = new();
        List<string> events = new();
        log.EventSink = events.Add;
        int changes = 0;
        log.Changed += (_, _) => changes++;
        log.Event("Following x.");
        log.Event("Following x.");
        log.Debug("Follower", "goto x");
        bool debug = false;
        log.DebugEnabled = () => debug;
        log.Debug("Follower", "hidden");
        IReadOnlyList<ButlerLogLine> lines = log.Snapshot();
        Assert.Equal(2, lines.Count);
        Assert.Equal(2, lines[0].Count);
        Assert.EndsWith("[ButlerV5] Following x. (x2)", lines[0].Display);
        Assert.Equal(["Following x.", "Following x."], events);
        Assert.Equal(3, changes);
        log.Clear();
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public async Task ThePageSavesItsOptionsAndTargetsAndDrivesTheButler()
    {
        FakePluginBackends plugins = new();
        FakeButler butler = plugins.FakeButler;
        await using TestApp app = new(scripts: new PluginOnlyScriptHost(plugins));
        PluginStore store = new(app.Paths.PluginsFile);
        using ButlerViewModel page = new(app.Services, store);
        Assert.True(page.IsAvailable);
        Assert.Equal(new ButlerOptions(), butler.Options); // desktop defaults
        Assert.False(page.FollowCommand.CanExecute(null)); // offline, no name

        // Options go to the butler at once and are saved.
        page.PassiveAttack = true;
        page.GotoDelayMs = 750;
        page.Park = nameof(ButlerParkSpot.Whitemap);
        page.ClassType = nameof(ButlerClassChoice.Solo);
        page.RescueThreshold = 50; // clamped like the desktop plugin
        Assert.True(butler.Options.PassiveAttack);
        Assert.Equal(750, butler.Options.GotoDelayMs);
        Assert.Equal(ButlerParkSpot.Whitemap, butler.Options.Park);
        Assert.Equal(ButlerClassChoice.Solo, butler.Options.ClassType);
        Assert.Equal(10, butler.Options.RescueThreshold);

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        await Sandbox.Until(() => app.Engine.Snapshot.IsOnline);
        page.Apply(app.Engine.Snapshot);

        page.PlayerName = " Alpha ";
        Assert.True(page.FollowCommand.CanExecute(null));
        page.FollowCommand.Execute(null);
        Assert.Null(page.Message);
        Assert.True(page.IsFollowing);
        Assert.Contains("Alpha", page.StateText);
        Assert.True(page.StopCommand.CanExecute(null));
        await page.StopAndParkCommand.ExecuteAsync(null);
        Assert.False(page.IsFollowing);
        page.PlayerName = "Beta";
        page.GotoCommand.Execute(null);
        page.JumpCommand.Execute(null);
        Assert.Contains("not in this room", page.Message);
        page.PlayerName = "me";
        page.FollowCommand.Execute(null);
        Assert.Contains("this account", page.Message);
        Assert.Equal(["follow  Alpha ", "stop park", "goto Beta", "jump Beta", "follow me"], butler.Calls);
        // Recent targets: newest first, no duplicates; jump and refused follows are not remembered.
        Assert.Equal(["Beta", "Alpha"], page.RecentTargets.Select(r => r.Name));

        page.RefreshPlayersCommand.Execute(null);
        Assert.Equal(["Alpha", "Beta"], page.RoomPlayers.Select(r => r.Name));
        page.RoomPlayers[0].UseCommand.Execute(page.RoomPlayers[0].Name);
        Assert.Equal("Alpha", page.PlayerName);

        // The log shows newest first while the page is open; share hands it to the platform.
        page.IsActive = true;
        butler.AddLog("first");
        butler.AddLog("second");
        Assert.EndsWith("second", page.LogLines[0]);
        page.ShareLogCommand.Execute(null);
        Assert.Contains("first", app.Platform.Shared);
        page.ClearLogCommand.Execute(null);
        Assert.Empty(page.LogLines);

        // A new page (next app start) reads the saved options and targets back.
        using ButlerViewModel again = new(app.Services, new PluginStore(app.Paths.PluginsFile));
        Assert.True(again.PassiveAttack);
        Assert.Equal(750m, again.GotoDelayMs);
        Assert.Equal(nameof(ButlerParkSpot.Whitemap), again.Park);
        Assert.Equal(nameof(ButlerClassChoice.Solo), again.ClassType);
        Assert.Equal(10m, again.RescueThreshold);
        Assert.Equal(["Beta", "Alpha"], again.RecentTargets.Select(r => r.Name));
    }

    [Fact]
    public void ADamagedPluginFileStartsFromDefaults()
    {
        string path = Path.Combine(Sandbox.NewDir(), "plugins.json");
        File.WriteAllText(path, "{ not json");
        PluginStore store = new(path);
        Assert.True(store.IsEnabled("butler"));
        store.SetOption("butler", "gotoDelay", "900");
        store.SetEnabled("dailytracker", false);
        PluginStore reread = new(path);
        Assert.Equal("900", reread.GetOption("butler", "gotoDelay"));
        Assert.False(reread.IsEnabled("dailytracker"));
        Assert.True(reread.IsEnabled("butler"));
    }
}

public partial class SessionIntegrationTests
{
    /// <summary>
    /// ButlerV5 for one account on the real Skua host over the fake server: players in the room, go to and jump to a
    /// player, following a player into their cell and fighting there, giving up (parking) on a player who cannot be
    /// reached, and the "ignoring goto" warning arriving through the headless packet stream ("pext").
    /// </summary>
    [Fact]
    public async Task TheButlerFollowsAPlayerThroughSkua()
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
        IButlerBackend butler = Assert.IsAssignableFrom<IButlerBackend>(engine.Plugins?.Butler);
        string Log() => string.Join("\n", butler.GetLog().Select(l => l.Display));
        try
        {
            Assert.Throws<EngineException>(() => butler.StartFollow("otherplayer")); // offline
            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => engine.Snapshot.Character is not null && host.Skua!.Bot.Map.Loaded);
            FakeAqwServer.Session game = aqw.Server.LastSession!;
            int Count(string command) => game.Received.Count(r => r.Text.Contains($"%{command}%", StringComparison.Ordinal));

            Assert.Equal(["otherplayer"], butler.PlayersInRoom());
            Assert.Throws<EngineException>(() => butler.StartFollow("Hero")); // this account
            EngineException away = Assert.Throws<EngineException>(() => butler.JumpToPlayer("Nobody"));
            Assert.Contains("not in this room", away.Message);

            // Go to: the game's /goto.
            butler.GotoPlayer("SomeFriend");
            await Sandbox.Until(() => game.Received.Any(r => r.Text.Contains("%goto%somefriend%", StringComparison.Ordinal)));

            // The other player walks to r2 (the room's monsters are there); jump follows them.
            game.Str("uotls", "otherplayer", "strFrame:r2,strPad:Left,tx:100,ty:200");
            await Sandbox.Until(() => host.Skua!.Bot.Map.TryGetPlayer("otherplayer", out var p) && p?.Cell == "r2");
            butler.JumpToPlayer("OtherPlayer");
            await Sandbox.Until(() => host.Skua!.Bot.Player.Cell == "r2");
            game.Str("uotls", "otherplayer", "strFrame:r3,strPad:Right,tx:100,ty:200");
            await Sandbox.Until(() => host.Skua!.Bot.Map.TryGetPlayer("otherplayer", out var p) && p?.Cell == "r3");

            // Follow: into their cell (r3, the queen) and fighting there.
            butler.Options = new ButlerOptions { GotoDelayMs = 250, RescueThreshold = 1, Park = ButlerParkSpot.Stay };
            butler.StartFollow("otherplayer");
            await Sandbox.Until(() => butler.Status.State == ButlerFollowState.WithPlayer, 15000);
            await Sandbox.Until(() => host.Skua!.Bot.Player.Cell == "r3", 15000);
            await Sandbox.Until(() => Count("gar") > 0, 15000);
            Assert.Equal("otherplayer", butler.Status.Player);
            butler.StopFollow();
            Assert.Equal(ButlerFollowState.Idle, butler.Status.State);
            Assert.False(host.Skua!.Bot.Skills.TimerRunning);

            // A player who is not here and never shows up: gotos, then parked (Stay: no move), retrying slowly.
            // Out of combat again (the fake server only says so after a kill); the game sends /goto only when idle.
            game.Str("uotls", "hero", "intState:1");
            await Sandbox.Until(() => host.Skua!.Bot.Player.State == 1, 15000);
            int gotosBefore = Count("goto");
            butler.StartFollow("Ghost");
            await Sandbox.Until(() => butler.Status.State == ButlerFollowState.Parked, 20000);
            Assert.True(Count("goto") - gotosBefore >= 3, Log());
            Assert.Contains("Can't reach Ghost", butler.Status.Detail);
            butler.StopFollow();

            // "Ignoring goto": the warning arrives as a server packet and parks at once.
            butler.StartFollow("Shy");
            await Sandbox.Until(() => butler.Status.State == ButlerFollowState.Traveling);
            game.Str("server", "Shy is ignoring goto requests.");
            await Sandbox.Until(() => butler.Status.State == ButlerFollowState.Parked, 10000);
            Assert.Contains("ignoring goto", butler.Status.Detail);
            butler.StopFollow();
            Assert.Contains(butler.GetLog(), l => l.Text == "Stopped following Shy.");
        }
        finally
        {
            butler.StopFollow();
            await engine.DisposeAsync();
        }
    }
}
