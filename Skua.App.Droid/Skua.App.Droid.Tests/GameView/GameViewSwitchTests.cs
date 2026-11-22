using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.GameView;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Scheduler;

namespace Skua.App.Droid.Tests.GameView;

/// <summary>A launcher that records what the switch asked for (no game view: tests play its part through the switch's callbacks).</summary>
internal sealed class FakeLauncher : IGameViewLauncher
{
    public List<string> Launched { get; } = new();

    public List<(string Session, string Reason)> CloseRequests { get; } = new();

    public bool Fail { get; set; }

    public bool Launch(string session)
    {
        if (Fail)
            return false;
        lock (Launched)
            Launched.Add(session);
        return true;
    }

    public void RequestClose(string session, string reason)
    {
        lock (CloseRequests)
            CloseRequests.Add((session, reason));
    }
}

/// <summary>
/// The game view switch against the engine on the fake AQW server: the engine stops reconnecting while the view owns
/// the account (its own loop and the hold itself), logs back in afterwards, the queue continues at its entry, a stopped
/// script is offered again, and a view that dies or never connects returns the app to headless.
/// </summary>
public sealed class GameViewSwitchTests
{
    internal static readonly GameViewSwitchOptions Fast = new()
    {
        ConnectTimeout = TimeSpan.FromMilliseconds(600),
        ReleaseDelayConfirmed = TimeSpan.FromMilliseconds(30),
        ReleaseDelayUnconfirmed = TimeSpan.FromMilliseconds(60),
        CloseTimeout = TimeSpan.FromMilliseconds(600),
    };

    [Fact]
    public async Task TheHoldStopsEveryReconnectUntilItEnds()
    {
        await using FakeAqw aqw = new();
        // Long reconnect delay: a drop leaves the engine waiting in its loop, which the hold must cancel.
        await using HeadlessBotEngine engine = new(new HeadlessEngineOptions
        {
            SessionFactory = aqw.NewSession,
            AutoReconnect = true,
            ReconnectDelays = [TimeSpan.FromMilliseconds(700)],
            MaxReconnectAttempts = 5,
            PublishInterval = TimeSpan.FromMilliseconds(20),
        });
        await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        aqw.Server.LastSession!.Close();
        await Sandbox.Until(() => engine.Snapshot.Connection == ConnectionPhase.Reconnecting);

        await engine.HoldForGameViewAsync();
        Assert.True(engine.GameViewHold);
        Assert.Equal(ConnectionPhase.GameView, engine.Snapshot.Connection);
        int sessions = aqw.Server.Sessions.Count;
        await Task.Delay(1200);
        Assert.Equal(sessions, aqw.Server.Sessions.Count); // the pending reconnect never ran
        Assert.Equal(ConnectionPhase.GameView, engine.Snapshot.Connection);
        EngineException login = await Assert.ThrowsAsync<EngineException>(() => engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null)));
        Assert.Contains("game view", login.Message);

        await engine.EndGameViewHoldAsync(reconnect: true);
        Assert.False(engine.GameViewHold);
        Assert.Equal(ConnectionPhase.Online, engine.Snapshot.Connection);
        Assert.Equal(sessions + 1, aqw.Server.Sessions.Count);
        Assert.Contains(engine.GetRecentLog(), l => l.Message.Contains("after the game view"));
    }

    [Fact]
    public async Task TheHoldLogsOutAndKeepsTheSocketClosed()
    {
        await using FakeAqw aqw = new();
        await using HeadlessBotEngine engine = aqw.NewEngine();
        await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        int sessions = aqw.Server.Sessions.Count;

        await engine.HoldForGameViewAsync();
        Assert.Null(engine.Snapshot.Character);
        Assert.True(engine.Session.Connection is null || engine.Session.Connection.State == Skua.Core.Headless.Net.ConnectionState.Closed);
        await Task.Delay(400); // NewEngine reconnects after 50 ms: nothing may happen
        Assert.Equal(sessions, aqw.Server.Sessions.Count);

        await engine.EndGameViewHoldAsync(reconnect: false);
        Assert.Equal(ConnectionPhase.Offline, engine.Snapshot.Connection);
        Assert.Equal(sessions, aqw.Server.Sessions.Count);
    }

    [Fact]
    public async Task OpenAndCloseHandsTheAccountOverAndBack()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        FakeLauncher launcher = new();
        using GameViewSwitch sw = new(app.Host, launcher, Fast);
        List<GameViewState> states = new();
        sw.Changed += (_, s) => { lock (states) states.Add(s.State); };

        await sw.OpenAsync();
        string session = Assert.Single(launcher.Launched);
        Assert.Equal(GameViewState.Opening, sw.State);
        Assert.Equal(ConnectionPhase.GameView, app.Engine.Snapshot.Connection);
        int sessions = app.Aqw.Server.Sessions.Count;

        Assert.Null(sw.Connect("0000"));                     // another session id: ignored
        GameViewHello? hello = sw.Connect(session);
        Assert.NotNull(hello);
        Assert.Equal(GameViewOptions.FromJson(hello!.OptionsJson), new GameViewOptions());
        Assert.Equal(GameViewState.Open, sw.State);
        sw.ReportStage(session, "in_game");
        Assert.Contains("in the game", sw.Status.Text);
        await EngineException(() => sw.OpenAsync());         // only one at a time

        sw.ViewClosing("0000", "nope", true, null);           // another session: ignored
        Assert.Equal(GameViewState.Open, sw.State);
        sw.ViewClosing(session, "closed by the user", socketsClosed: true, "in game after 12 s, 5.2 fps");
        await sw.Returning;
        Assert.Equal(GameViewState.Headless, sw.State);
        Assert.Equal(ConnectionPhase.Online, app.Engine.Snapshot.Connection);
        Assert.Equal(sessions + 1, app.Aqw.Server.Sessions.Count);
        Assert.Contains(app.Engine.GetRecentLog(), l => l.Source == "gameview" && l.Message.Contains("5.2 fps"));
        Assert.Null(sw.Status.Notice);
        Assert.Equal([GameViewState.Preparing, GameViewState.Opening, GameViewState.Open, GameViewState.Returning, GameViewState.Headless],
            states.Distinct().ToArray());
        Assert.Null(sw.Connect(session));                     // a stale view cannot attach again
    }

    [Fact]
    public async Task AViewThatDiesReturnsTheAppToHeadless()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        FakeLauncher launcher = new();
        using GameViewSwitch sw = new(app.Host, launcher, Fast);
        await sw.OpenAsync();
        string session = launcher.Launched[0];
        sw.Connect(session);

        sw.ViewGone(session, "the game view process ended");
        await sw.Returning;
        Assert.Equal(GameViewState.Headless, sw.State);
        Assert.Contains("ended unexpectedly", sw.Status.Notice);
        Assert.Equal(ConnectionPhase.Online, app.Engine.Snapshot.Connection);
    }

    [Fact]
    public async Task AViewThatNeverConnectsReturnsTheAppToHeadless()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        using GameViewSwitch sw = new(app.Host, new FakeLauncher(), Fast);
        await sw.OpenAsync();
        await Sandbox.Until(() => sw.State == GameViewState.Headless, 5000);
        Assert.Contains("did not open", sw.Status.Notice);
        await Sandbox.Until(() => app.Engine.Snapshot.Connection == ConnectionPhase.Online);
    }

    [Fact]
    public async Task ALaunchFailureGivesTheAccountBack()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        using GameViewSwitch sw = new(app.Host, new FakeLauncher { Fail = true }, Fast);
        await EngineException(() => sw.OpenAsync());
        await sw.Returning;
        Assert.Equal(GameViewState.Headless, sw.State);
        Assert.Equal(ConnectionPhase.Online, app.Engine.Snapshot.Connection);
    }

    [Fact]
    public async Task CloseFromTheAppAsksTheViewAndTakesTheAccountBackIfItNeverAnswers()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        FakeLauncher launcher = new();
        using GameViewSwitch sw = new(app.Host, launcher, Fast);
        await sw.OpenAsync();
        string session = launcher.Launched[0];
        sw.Connect(session);
        sw.RequestClose("closed from the app");
        Assert.Equal(session, Assert.Single(launcher.CloseRequests).Session);
        await Sandbox.Until(() => sw.State == GameViewState.Headless, 5000);
        await Sandbox.Until(() => app.Engine.Snapshot.Connection == ConnectionPhase.Online);
    }

    [Fact]
    public async Task AStoppedScriptIsOfferedAgainAfterwards()
    {
        QueueScriptHost scripts = new();
        scripts.Scripts["farm.cs"] = new FakeScript(Forever: true);
        await using TestApp app = new(scripts: scripts);
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        string path = QueueTesting.Script(app, "farm.cs");
        await app.Engine.StartScriptAsync(path);
        FakeLauncher launcher = new();
        using GameViewSwitch sw = new(app.Host, launcher, Fast);

        GameViewPlan plan = sw.Plan();
        Assert.True(plan.NeedsConfirmation);
        Assert.Contains("farm", plan.Question);

        await sw.OpenAsync();
        Assert.False(app.Engine.Snapshot.Script.IsBusy);
        await EngineException(() => app.Engine.StartScriptAsync(path)); // refused while the view has the account
        string session = launcher.Launched[0];
        sw.Connect(session);
        sw.ViewClosing(session, "closed by the user", true, null);
        await sw.Returning;
        Assert.Equal(path, sw.Status.RestartScript);
        await sw.RestartScriptAsync();
        await Sandbox.Until(() => app.Engine.Snapshot.Script.State == ScriptRunState.Running);
        Assert.Null(sw.Status.RestartScript);
        Assert.Equal(2, scripts.Starts.Count);
    }

    [Fact]
    public async Task TheQueueContinuesAtItsEntry()
    {
        QueueScriptHost scripts = new();
        scripts.Scripts["one.cs"] = new FakeScript(RunMs: 50);
        scripts.Scripts["two.cs"] = new FakeScript(Forever: true);
        scripts.Scripts["three.cs"] = new FakeScript(RunMs: 50);
        await using TestApp app = new(scripts: scripts, withQueue: true);
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        ScriptScheduler queue = app.Host.Scheduler!;
        foreach (string name in new[] { "one.cs", "two.cs", "three.cs" })
            queue.Add(QueueTesting.Script(app, name));
        queue.Start();
        await Sandbox.Until(() => scripts.StartNames.Contains("two.cs") && app.Engine.Snapshot.Script.State == ScriptRunState.Running);

        FakeLauncher launcher = new();
        using GameViewSwitch sw = new(app.Host, launcher, Fast);
        Assert.True(sw.Plan().QueueRunning);
        await sw.OpenAsync();
        Assert.False(queue.IsRunning);
        Assert.False(app.Engine.Snapshot.Script.IsBusy);
        Assert.Equal(1, queue.Status.Index);
        await Task.Delay(300);
        Assert.Equal(["one.cs", "two.cs"], scripts.StartNames); // nothing runs while the view has the account

        string session = launcher.Launched[0];
        sw.Connect(session);
        sw.ViewClosing(session, "closed by the user", true, null);
        await sw.Returning;
        Assert.Null(sw.Status.RestartScript); // the queue continues instead
        await Sandbox.Until(() => scripts.StartNames.Count(n => n == "two.cs") == 2);
        Assert.Equal(["one.cs", "two.cs", "two.cs"], scripts.StartNames);
        await queue.StopAsync();
    }

    private static async Task EngineException(Func<Task> action) => await Assert.ThrowsAsync<EngineException>(action);
}
