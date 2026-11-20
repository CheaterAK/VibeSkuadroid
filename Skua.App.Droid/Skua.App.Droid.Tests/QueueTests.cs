using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Memory;
using Skua.App.Droid.Core.Scheduler;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;

namespace Skua.App.Droid.Tests;

/// <summary>
/// The script queue's logic on the real engine (fake AQW server) with a behaviour-driven script host: order, runs,
/// option profiles, error rules, stop-after, relogin resumption, stops, persistence and resume after a restart.
/// </summary>
public class QueueTests
{
    private static async Task<(TestApp App, QueueScriptHost Host)> OnlineApp()
    {
        QueueScriptHost host = new();
        TestApp app = new(scripts: host);
        await app.Engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        return (app, host);
    }

    [Fact]
    public async Task EntriesRunInOrderWithTheirRunsAndOptionProfiles()
    {
        (TestApp app, QueueScriptHost host) = await OnlineApp();
        await using TestApp _ = app;
        using ScriptScheduler q = QueueTesting.Scheduler(app);
        QueueEntry a = q.Add(QueueTesting.Script(app, "Farm/Alpha.cs"));
        QueueEntry b = q.Add(QueueTesting.Script(app, "Beta.cs"));
        QueueEntry a2 = q.Add(QueueTesting.Script(app, "Farm/Alpha.cs"));
        Assert.Equal(["Alpha.cs", "Beta.cs", "Alpha (2).cs"], q.Entries.Select(e => e.Name));
        Assert.Equal("Farm/Alpha.cs", a.Script);
        Assert.NotEqual(a.OptionStorage, a2.OptionStorage);
        Assert.Equal($"Queue_Alpha_{a.Id}", a.OptionStorage);
        q.Update(a with { Runs = 2, Name = "Gold farm", DelayAfter = TimeSpan.FromMilliseconds(30) });
        q.Update(b with { DelayAfter = TimeSpan.FromMilliseconds(30) });
        q.Update(a2 with { DelayAfter = TimeSpan.Zero });

        List<QueueStatus> seen = new();
        q.StatusChanged += (_, s) =>
        {
            lock (seen)
                seen.Add(s);
        };
        q.Start();
        Assert.Throws<EngineException>(() => q.Add(QueueTesting.Script(app, "Beta.cs"))); // no edits while running
        await QueueTesting.UntilIdle(q);

        Assert.Equal(["Alpha.cs", "Alpha.cs", "Beta.cs", "Alpha.cs"], host.StartNames);
        Assert.Equal([a.OptionStorage, a.OptionStorage, b.OptionStorage, a2.OptionStorage], host.Starts.Select(s => s.Storage));
        Assert.All(host.Starts, s => Assert.True(s.Silent));
        QueueStatus done = q.Status;
        Assert.Equal(QueuePhase.Idle, done.Phase);
        Assert.StartsWith("Finished: 3 entries done", done.Text);
        Assert.All(done.Entries, e => Assert.Equal("Completed", e.Result.Status));
        Assert.Equal(2, done.Entries[0].Result.RunsDone);
        lock (seen)
        {
            Assert.Contains(seen, s => s.CurrentName == "Gold farm" && s.Run == 2 && s.Runs == 2 && s.NextName == "Beta.cs");
            Assert.Contains(seen, s => s.NotificationLine == "Queue 1/3: Gold farm (run 2/2)");
        }
        Assert.Contains(app.Engine.GetRecentLog(), l => l.Source == "queue" && l.Message.StartsWith("Queue: starting Gold farm", StringComparison.Ordinal));

        // Saved: not running, past the end.
        QueueState saved = QueueTesting.Store(app).LoadState()!;
        Assert.False(saved.Running);
        Assert.Equal(3, saved.Index);
        Assert.Equal(3, saved.Entries.Count);
    }

    [Fact]
    public async Task ErrorRulesSkipRetryAndStop()
    {
        (TestApp app, QueueScriptHost host) = await OnlineApp();
        await using TestApp _ = app;
        host.Scripts["Crash.cs"] = new FakeScript(Fault: "NullReferenceException: Object reference not set to an instance of an object.");
        host.Scripts["Broken.cs"] = new FakeScript(StartError: "Compile error: Broken.cs(3,1): error CS1002: ; expected");
        host.Scripts["Fatal.cs"] = new FakeScript(Fault: "InvalidOperationException: Sequence contains no elements");
        using ScriptScheduler q = QueueTesting.Scheduler(app);
        TimeSpan noWait = TimeSpan.FromMilliseconds(10);
        QueueEntry crash = q.Add(QueueTesting.Script(app, "Crash.cs"));
        q.Update(crash with { DelayAfter = noWait, OnError = QueueErrorAction.Skip });
        QueueEntry broken = q.Add(QueueTesting.Script(app, "Broken.cs"));
        q.Update(broken with { DelayAfter = noWait, OnError = QueueErrorAction.Retry, MaxRetries = 2 });
        QueueEntry ok = q.Add(QueueTesting.Script(app, "Fine.cs"));
        q.Update(ok with { DelayAfter = noWait });
        QueueEntry fatal = q.Add(QueueTesting.Script(app, "Fatal.cs"));
        q.Update(fatal with { DelayAfter = noWait, OnError = QueueErrorAction.Stop });
        q.Add(QueueTesting.Script(app, "Never.cs"));
        string missing = QueueTesting.Script(app, "Gone.cs");

        q.Start();
        await QueueTesting.UntilIdle(q);

        Assert.Equal(["Crash.cs", "Broken.cs", "Broken.cs", "Broken.cs", "Fine.cs", "Fatal.cs"], host.StartNames);
        QueueStatus s = q.Status;
        Assert.Equal("Crashed", s.Entries[0].Result.Status);
        Assert.StartsWith("The script crashed: it used something that was not there", s.Entries[0].Result.Error);
        Assert.Equal("Failed", s.Entries[1].Result.Status);
        Assert.Equal("The script does not compile: Broken.cs(3,1): error CS1002: ; expected", s.Entries[1].Result.Error);
        Assert.Equal("Completed", s.Entries[2].Result.Status);
        Assert.Equal("Crashed", s.Entries[3].Result.Status);
        Assert.Equal("Queued", s.Entries[4].Result.Status);
        Assert.StartsWith("Stopped: Fatal.cs failed.", s.Text);
        Assert.Contains("did something the current game state does not allow", s.Text);
        Assert.Equal(3, s.Index); // Continue would run Fatal.cs again

        // A script deleted after it was queued: "File Not Found", skipped.
        q.Clear();
        QueueEntry gone = q.Add(missing);
        q.Update(gone with { DelayAfter = noWait });
        q.Add(QueueTesting.Script(app, "Fine.cs"));
        File.Delete(missing);
        q.Start();
        await QueueTesting.UntilIdle(q);
        Assert.Equal("File Not Found", q.Status.Entries[0].Result.Status);
        Assert.Equal("The script file is missing (deleted or renamed?).", q.Status.Entries[0].Result.Error);
        Assert.Equal("Completed", q.Status.Entries[1].Result.Status);
    }

    [Fact]
    public async Task StopAfterRepeatAndLoop()
    {
        (TestApp app, QueueScriptHost host) = await OnlineApp();
        await using TestApp _ = app;
        host.Scripts["Endless.cs"] = new FakeScript(Forever: true);
        host.Scripts["Short.cs"] = new FakeScript(RunMs: 60);
        using ScriptScheduler q = QueueTesting.Scheduler(app);
        QueueEntry endless = q.Add(QueueTesting.Script(app, "Endless.cs"));
        q.Update(endless with { Runs = 1, StopAfter = TimeSpan.FromMilliseconds(400), DelayAfter = TimeSpan.FromMilliseconds(10) });
        QueueEntry repeat = q.Add(QueueTesting.Script(app, "Short.cs"));
        q.Update(repeat with { Runs = 0, StopAfter = TimeSpan.FromMilliseconds(500), DelayAfter = TimeSpan.FromMilliseconds(10) });

        q.Start();
        await QueueTesting.UntilIdle(q);
        List<string> starts = host.StartNames;
        Assert.Equal("Endless.cs", starts[0]);
        Assert.True(starts.Count(n => n == "Short.cs") >= 3, string.Join(",", starts)); // repeated until its time was up
        Assert.Equal("Completed", q.Status.Entries[0].Result.Status);
        Assert.InRange(q.Status.Entries[0].Result.Elapsed, TimeSpan.FromMilliseconds(380), TimeSpan.FromSeconds(3));
        Assert.Equal(ScriptRunState.Idle, app.Engine.Snapshot.Script.State); // the endless one was stopped

        // Loop: starts over until stopped; the stop keeps the position.
        q.Clear();
        QueueEntry one = q.Add(QueueTesting.Script(app, "Short.cs"));
        q.Update(one with { DelayAfter = TimeSpan.FromMilliseconds(10) });
        q.Loop = true;
        int before = host.Starts.Count;
        q.Start();
        await Sandbox.Until(() => host.Starts.Count >= before + 3);
        Assert.True(q.Status.Loop);
        Assert.Equal("Short.cs", q.Status.NextName);
        await q.StopAsync();
        Assert.False(q.IsRunning);
        Assert.Equal("Stopped.", q.Status.Text);
        Assert.False(app.Engine.Snapshot.Script.IsBusy);
    }

    [Fact]
    public async Task PausingTheScriptHoldsTheQueueAndItsClock()
    {
        (TestApp app, QueueScriptHost host) = await OnlineApp();
        await using TestApp _ = app;
        host.Scripts["Endless.cs"] = new FakeScript(Forever: true);
        using ScriptScheduler q = QueueTesting.Scheduler(app);
        QueueEntry e = q.Add(QueueTesting.Script(app, "Endless.cs"));
        q.Update(e with { StopAfter = TimeSpan.FromMilliseconds(600), DelayAfter = TimeSpan.Zero });
        q.Start();
        await Sandbox.Until(() => q.Status.Entries[0].Result.Status == "Running");
        await app.Engine.PauseScriptAsync();
        await Sandbox.Until(() => q.Status.Entries[0].Result.Status == "Paused");
        Assert.Contains("paused; the queue waits", q.Status.Text);
        TimeSpan held = q.Status.EntryElapsed;
        await Task.Delay(900); // longer than the stop-after: paused time does not count
        Assert.True(q.IsRunning);
        Assert.InRange(q.Status.EntryElapsed, held, held + TimeSpan.FromMilliseconds(100));
        await app.Engine.ResumeScriptAsync();
        await QueueTesting.UntilIdle(q);
        Assert.Equal("Completed", q.Status.Entries[0].Result.Status);
    }

    /// <summary>
    /// Pause pressed while a start is still in progress (compiling), by hand or right after the queue marked its entry
    /// running: the pause waits for the start and pauses the script it started; when the start fails, the pause says
    /// that no script is running.
    /// </summary>
    [Fact]
    public async Task APauseDuringAStartWaitsForTheScriptToRun()
    {
        (TestApp app, QueueScriptHost host) = await OnlineApp();
        await using TestApp _ = app;
        host.Scripts["Endless.cs"] = new FakeScript(Forever: true);
        host.Scripts["Broken.cs"] = new FakeScript(StartError: "Compile error: CS1002");
        string endless = QueueTesting.Script(app, "Endless.cs");
        string broken = QueueTesting.Script(app, "Broken.cs");

        host.HoldStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task start = app.Engine.StartScriptAsync(endless);
        Assert.Equal(ScriptRunState.Compiling, host.Status.State);
        Task pause = app.Engine.PauseScriptAsync();
        Assert.False(pause.IsCompleted); // waits for the start
        host.HoldStart.SetResult();
        await start;
        await pause;
        Assert.True(host.Status.IsPaused);
        Assert.True(app.Engine.Snapshot.Script.IsPaused);
        await app.Engine.StopScriptAsync();

        host.HoldStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task failing = app.Engine.StartScriptAsync(broken);
        Task pauseFailing = app.Engine.PauseScriptAsync();
        Assert.False(pauseFailing.IsCompleted);
        host.HoldStart.SetResult();
        await Assert.ThrowsAsync<EngineException>(() => failing);
        EngineException e = await Assert.ThrowsAsync<EngineException>(() => pauseFailing);
        Assert.Equal("No script is running.", e.Message);

        // The queue: its entry reads "Running" once the engine has the start, so a pause then always finds it.
        host.HoldStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptScheduler q = QueueTesting.Scheduler(app);
        QueueEntry entry = q.Add(endless);
        q.Update(entry with { StopAfter = TimeSpan.FromMilliseconds(300), DelayAfter = TimeSpan.Zero });
        q.Start();
        await Sandbox.Until(() => q.Status.Entries[0].Result.Status == "Running");
        Task queuedPause = app.Engine.PauseScriptAsync();
        host.HoldStart.SetResult();
        await queuedPause;
        await Sandbox.Until(() => q.Status.Entries[0].Result.Status == "Paused");
        await app.Engine.ResumeScriptAsync();
        await QueueTesting.UntilIdle(q);
        Assert.Equal("Completed", q.Status.Entries[0].Result.Status);
    }

    /// <summary>
    /// The desktop's relogin collision protection: a run that Skua's relogin stopped is not counted; when Skua restarts
    /// the same script the queue adopts it (one start, nothing skipped); when it does not, the queue starts the entry
    /// again once the account is back.
    /// </summary>
    [Fact]
    public async Task ADropMidEntryResumesWithoutSkippingOrDoubleRunning()
    {
        (TestApp app, QueueScriptHost host) = await OnlineApp();
        await using TestApp _ = app;
        host.Scripts["Long.cs"] = new FakeScript(RunMs: 1500);
        using ScriptScheduler q = QueueTesting.Scheduler(app);
        QueueEntry first = q.Add(QueueTesting.Script(app, "Long.cs"));
        q.Update(first with { DelayAfter = TimeSpan.FromMilliseconds(10) });
        QueueEntry second = q.Add(QueueTesting.Script(app, "Next.cs"));
        q.Update(second with { DelayAfter = TimeSpan.Zero });

        // 1. Skua's relogin restarts the script: adopted.
        q.Start();
        await Sandbox.Until(() => q.Status.Entries[0].Result.Status == "Running");
        app.Aqw.Server.LastSession!.Close();
        host.StopForRelogin();
        await Sandbox.Until(() => q.Status.Phase == QueuePhase.WaitingForConnection);
        Assert.Equal("Disconnected", q.Status.Entries[0].Result.Status);
        Assert.Equal(0, q.Status.Index);
        await Sandbox.Until(() => app.Engine.Snapshot.Connection == ConnectionPhase.Online);
        host.RestartBySkua();
        await QueueTesting.UntilIdle(q);
        Assert.Equal(["Long.cs", "Next.cs"], host.StartNames);
        Assert.Equal(["Long.cs"], host.Restarts);
        Assert.Equal("Completed", q.Status.Entries[0].Result.Status);
        Assert.Equal(1, q.Status.Entries[0].Result.RunsDone);
        Assert.Contains(app.Engine.GetRecentLog(), l => l.Message.Contains("resumed after the reconnect", StringComparison.Ordinal));

        // 2. Back online but Skua does not restart it (relogin off, or it gave up): the queue runs the entry again.
        host.Starts.Clear();
        q.Start();
        await Sandbox.Until(() => q.Status.Entries[0].Result.Status == "Running");
        app.Aqw.Server.LastSession!.Close();
        host.StopForRelogin();
        await QueueTesting.UntilIdle(q);
        Assert.Equal(["Long.cs", "Long.cs", "Next.cs"], host.StartNames);
        Assert.Equal(1, q.Status.Entries[0].Result.RunsDone);
        Assert.Equal("Completed", q.Status.Entries[1].Result.Status);

        // 3. The drop and the reconnect both happen between two polls: still a disconnect, not a completed run.
        host.Starts.Clear();
        using ScriptScheduler slow = new(app.Engine, QueueTesting.Store(app), app.Paths.ScriptsDir, app.Paths.SkuaOptionsDir,
            QueueTesting.Fast with { PollInterval = TimeSpan.FromMilliseconds(400), SettleAfterRun = TimeSpan.FromMilliseconds(10) });
        slow.Start();
        await Sandbox.Until(() => host.Status.State == ScriptRunState.Running && slow.Status.Entries[0].Result.Status == "Running");
        app.Aqw.Server.LastSession!.Close();
        await Sandbox.Until(() => app.Engine.Snapshot.Connection != ConnectionPhase.Online);
        host.StopForRelogin();
        await Sandbox.Until(() => app.Engine.Snapshot.Connection == ConnectionPhase.Online);
        await QueueTesting.UntilIdle(slow);
        Assert.Equal(["Long.cs", "Long.cs", "Next.cs"], host.StartNames);
    }

    [Fact]
    public async Task StopAndLogoutStopTheQueue()
    {
        (TestApp app, QueueScriptHost host) = await OnlineApp();
        await using TestApp _ = app;
        host.Scripts["Endless.cs"] = new FakeScript(Forever: true);
        using ScriptScheduler q = QueueTesting.Scheduler(app);
        q.Add(QueueTesting.Script(app, "Endless.cs"));
        q.Add(QueueTesting.Script(app, "Next.cs"));

        // The script's Stop (Scripts page, Dashboard, notification) stops the queue too; the position is kept.
        q.Start();
        await Sandbox.Until(() => host.Status.State == ScriptRunState.Running);
        await app.Engine.StopScriptAsync();
        await QueueTesting.UntilIdle(q);
        Assert.Equal("Stopped with the script.", q.Status.Text);
        Assert.Equal("Stopped", q.Status.Entries[0].Result.Status);
        Assert.Equal(["Endless.cs"], host.StartNames);
        await Sandbox.Until(() => !app.Engine.Snapshot.Script.IsBusy);

        // Continue resumes at the stopped entry; logging out stops the queue.
        q.Continue();
        await Sandbox.Until(() => host.Starts.Count == 2 && host.Status.State == ScriptRunState.Running);
        Assert.Equal(["Endless.cs", "Endless.cs"], host.StartNames);
        await app.Host.LogoutAsync();
        await QueueTesting.UntilIdle(q);
        Assert.Equal("Stopped: logged out.", q.Status.Text);
        Assert.False(QueueTesting.Store(app).LoadState()!.Running);

        // A manual script blocks a start (the queue never takes over a script the user runs).
        await app.Engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        await app.Engine.StartScriptAsync(QueueTesting.Script(app, "Endless.cs"));
        EngineException busy = Assert.Throws<EngineException>(() => q.Start());
        Assert.Equal("Stop the running script first.", busy.Message);
        await app.Engine.StopScriptAsync();
    }

    /// <summary>
    /// Between runs, memory over the threshold restarts the engine; the queue saved where it was and the next process
    /// resumes at the next run (nothing re-run, nothing skipped). A queue the process lost without a planned restart
    /// (crash, killed) is never resumed by itself.
    /// </summary>
    [Fact]
    public async Task ARestartBetweenRunsResumesAtTheNextRun()
    {
        (TestApp app, QueueScriptHost host) = await OnlineApp();
        await using TestApp _ = app;
        app.Settings.Update(s => s with { AccountName = "hero" });
        ScriptScheduler q = QueueTesting.Scheduler(app);
        app.Host.AttachScheduler(q);
        foreach (string name in new[] { "One.cs", "Two.cs", "Three.cs" })
        {
            QueueEntry e = q.Add(QueueTesting.Script(app, name));
            q.Update(e with { DelayAfter = TimeSpan.FromMilliseconds(10) });
        }
        // The host's own check needs real memory over the threshold; play it: restart once, before the second run.
        int asked = 0;
        q.RestartBetweenRuns = reason => Interlocked.Increment(ref asked) == 1 && app.Host.RequestRestart(reason);

        q.Start();
        await Sandbox.Until(() => app.Platform.Restarts.Count == 1);
        await Sandbox.Until(() => q.Status.Phase == QueuePhase.Restarting);
        await Task.Delay(200);
        Assert.Equal(["One.cs"], host.StartNames); // stopped before the second run
        QueueState saved = QueueTesting.Store(app).LoadState()!;
        Assert.True(saved.Running);
        Assert.True(saved.ResumeAfterRestart);
        Assert.Equal(1, saved.Index);
        Assert.Equal("Completed", saved.Results[saved.Entries[0].Id].Status);
        // The restart logs out (ProcessRestarter): that must not turn the saved resume into a stop.
        await app.Engine.LogoutAsync();
        Assert.True(QueueTesting.Store(app).LoadState()!.ResumeAfterRestart);
        q.Dispose();

        // The next process: a new queue on the same files, a host that resumes after the restart.
        await app.Engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        ScriptScheduler next = QueueTesting.Scheduler(app);
        Assert.False(next.IsRunning);
        Assert.Contains("resumes after the login", next.Status.Text);
        EngineHost host2 = new(app.Engine, app.Settings, new CredentialVault(app.Store), new ResumeStore(app.Paths.ResumeFile), app.Platform);
        host2.AttachScheduler(next);
        ResumeResult? resumed = await host2.ResumeAfterRestartAsync();
        Assert.NotNull(resumed);
        Assert.Contains("The script queue resumes.", resumed!.Message);
        await QueueTesting.UntilIdle(next);
        Assert.Equal(["One.cs", "Two.cs", "Three.cs"], host.StartNames);
        Assert.All(next.Status.Entries, e => Assert.Equal("Completed", e.Result.Status));
        next.Dispose();

        // Lost without a planned restart: shown as interrupted, not resumed.
        QueueTesting.Store(app).SaveState(QueueTesting.Store(app).LoadState()! with { Running = true, Index = 1, ResumeAfterRestart = false });
        using ScriptScheduler afterCrash = QueueTesting.Scheduler(app);
        Assert.False(afterCrash.IsRunning);
        Assert.False(afterCrash.ResumeAfterRestart());
        Assert.Equal("Interrupted", afterCrash.Status.Entries[1].Result.Status);
        Assert.StartsWith("Interrupted (the app was closed) at entry 2", afterCrash.Status.Text);
        Assert.False(QueueTesting.Store(app).LoadState()!.Running);
    }

    [Fact]
    public void TheWatchdogAllowsARestartBetweenQueueRunsWithinItsLimits()
    {
        ManualTime time = new();
        MemoryWatchdog w = new(new MemoryWatchdogOptions { ThresholdBytes = 1000 }, null, time);
        Assert.False(w.TryClaimRestartBetweenRuns(500));     // under the threshold (and the baseline)
        Assert.False(w.TryClaimRestartBetweenRuns(2000));    // minimum uptime
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(w.TryClaimRestartBetweenRuns(2000));     // no idle grace needed between runs
        Assert.False(w.TryClaimRestartBetweenRuns(2000));    // once
        MemoryWatchdog recent = new(new MemoryWatchdogOptions { ThresholdBytes = 1000 }, time.GetUtcNow() - TimeSpan.FromMinutes(5), time);
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(recent.TryClaimRestartBetweenRuns(2000)); // 15 min between automatic restarts
        MemoryWatchdog off = new(new MemoryWatchdogOptions { ThresholdBytes = 1000, AutoRestart = false, MinimumUptime = TimeSpan.Zero }, null, time);
        Assert.False(off.TryClaimRestartBetweenRuns(500));
        Assert.False(off.TryClaimRestartBetweenRuns(2000));
        MemoryWatchdog tooLow = new(new MemoryWatchdogOptions { ThresholdBytes = 1000, MinimumUptime = TimeSpan.Zero }, null, time);
        Assert.False(tooLow.TryClaimRestartBetweenRuns(950)); // the process starts near the threshold: never
        Assert.False(tooLow.TryClaimRestartBetweenRuns(2000));
    }

    [Fact]
    public async Task PlaylistsSaveAndLoadWithTheirSettings()
    {
        QueueScriptHost host = new();
        await using TestApp app = new(scripts: host);
        using ScriptScheduler q = QueueTesting.Scheduler(app);
        QueueEntry a = q.Add(QueueTesting.Script(app, "Farm/A.cs"));
        QueueEntry b = q.Add(QueueTesting.Script(app, "B.cs"));
        QueueEntry edited = a with
        {
            Name = "Daily gold",
            Runs = 0,
            StopAfter = TimeSpan.FromMinutes(90),
            DelayAfter = TimeSpan.FromSeconds(12),
            OnError = QueueErrorAction.Retry,
            MaxRetries = 4,
            CoreBots = [new SavedOption("PrivateRooms", Core.Engine.ScriptOptionKind.Toggle, "True"), new SavedOption("SoloClassSelect", Core.Engine.ScriptOptionKind.Text, "Void Highlord")],
        };
        q.Update(edited);
        q.Loop = true;
        Assert.Throws<EngineException>(() => q.SavePlaylist("../escape"));
        Assert.Throws<EngineException>(() => q.SavePlaylist(""));
        q.SavePlaylist("Nightly (main)");
        Assert.Equal(["Nightly (main)"], q.ListPlaylists());
        Assert.Equal("Nightly (main)", q.Status.PlaylistName);
        Assert.Equal("repeat · stop after 1 h 30 min · wait 12 s · on error retry 4x · CoreBots profile", q.Entries[0].Summary);

        q.Clear();
        Assert.Empty(q.Entries);
        File.Delete(Path.Combine(app.Paths.ScriptsDir, "B.cs"));
        Assert.Equal(1, q.LoadPlaylist("Nightly (main)"));
        Assert.Contains("1 missing script left out", q.Status.Text);
        QueueEntry loaded = Assert.Single(q.Entries);
        Assert.Equal(edited, loaded with { CoreBots = edited.CoreBots });
        Assert.Equal(edited.CoreBots, loaded.CoreBots);
        Assert.Equal(edited.OptionStorage, loaded.OptionStorage); // same id: the same option profile
        Assert.True(q.Loop);
        Assert.Throws<EngineException>(() => q.LoadPlaylist("Nope"));

        // The live queue survives a new process (activity or process recreated) as it was.
        using ScriptScheduler again = QueueTesting.Scheduler(app);
        Assert.Equal(edited.Id, Assert.Single(again.Entries).Id);
        Assert.True(again.DeletePlaylist("Nightly (main)"));
        Assert.Empty(again.ListPlaylists());
        _ = b;
    }

    /// <summary>An entry's CoreBots profile: edited over the account's current settings, written before each run.</summary>
    [Fact]
    public async Task ACoreBotsProfileIsWrittenBeforeTheEntryRuns()
    {
        (TestApp app, QueueScriptHost host) = await OnlineApp();
        await using TestApp _ = app;
        File.WriteAllText(Path.Combine(app.Paths.ScriptsDir, "CoreBots.cs"), """
            public bool PrivateRooms => CBOBool("PrivateRooms", out bool b) && b;
            public int Delay => CBOInt("ActionDelay", out int d) ? d : 400;
            public string Solo => CBOString("SoloClassSelect", out string s) ? s : "";
            """);
        string optionsDir = app.Paths.SkuaOptionsDir;
        Directory.CreateDirectory(optionsDir);
        string cbo = Path.Combine(optionsDir, "CBO_Storage(hero).txt");
        File.WriteAllLines(cbo, ["PrivateRooms: False", "Unknown: kept"]);
        using ScriptScheduler q = QueueTesting.Scheduler(app);
        QueueEntry e = q.Add(QueueTesting.Script(app, "Farm.cs"));
        q.Update(e with { DelayAfter = TimeSpan.Zero });

        Core.Engine.ScriptOptionSet profile = q.LoadCoreBotsProfile(q.Entries[0], "Hero");
        Assert.Equal("CBO_Storage(hero)", profile.Storage);
        Assert.Equal("False", profile.Items.Single(i => i.Name == "PrivateRooms").Value);
        Assert.Throws<EngineException>(() => q.SetCoreBotsProfile(e.Id, profile with
        {
            Items = profile.Items.Select(i => i.Name == "ActionDelay" ? i with { Value = "fast" } : i).ToList(),
        }));
        q.SetCoreBotsProfile(e.Id, profile with
        {
            Items = profile.Items.Select(i => i.Name switch
            {
                "PrivateRooms" => i with { Value = "True" },
                "ActionDelay" => i with { Value = "700" },
                _ => i,
            }).ToList(),
        });
        Assert.Equal("True", q.LoadCoreBotsProfile(q.Entries[0], "Hero").Items.Single(i => i.Name == "PrivateRooms").Value);
        Assert.Equal("False", File.ReadAllLines(cbo)[0]["PrivateRooms: ".Length..]); // not written yet

        q.Start();
        await QueueTesting.UntilIdle(q);
        string[] lines = File.ReadAllLines(cbo);
        Assert.Contains("PrivateRooms: True", lines);
        Assert.Contains("ActionDelay: 700", lines);
        Assert.Contains("Unknown: kept", lines);
        q.SetCoreBotsProfile(e.Id, null);
        Assert.Null(q.Entries[0].CoreBots);
    }

    [Theory]
    [InlineData("Compile error: X.cs(1,1): error CS0103: The name 'y' does not exist", "The script does not compile: X.cs(1,1): error CS0103: The name 'y' does not exist")]
    [InlineData("NullReferenceException: Object reference not set to an instance of an object.", "The script crashed: it used something that was not there (often an item, monster or map that did not load) (NullReferenceException: Object reference not set to an instance of an object.).")]
    [InlineData("System.TimeoutException: Join timed out", "The script crashed: it waited too long for the game (TimeoutException: Join timed out).")]
    [InlineData("WeirdException: odd", "The script crashed (WeirdException): odd")]
    [InlineData("No script at /x/y.cs.", "The script file is missing (deleted or renamed?).")]
    [InlineData("A script is already running.", "Another script was already running.")]
    [InlineData(null, "The script stopped with an error.")]
    [InlineData("Something odd happened", "Something odd happened.")]
    public void ErrorsAreSummarizedInPlainEnglish(string? message, string expected) => Assert.Equal(expected, ErrorText.Summarize(message));
}
