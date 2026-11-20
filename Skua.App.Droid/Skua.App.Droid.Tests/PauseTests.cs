using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Memory;
using Skua.App.Droid.Scripting;
using Skua.Core.Headless;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Models;

namespace Skua.App.Droid.Tests;

public partial class SessionIntegrationTests
{
    private const string TickScript = """
        using Skua.Core.Interfaces;

        public class DroidPauseTicks
        {
            public void ScriptMain(IScriptInterface bot)
            {
                int n = 0;
                while (!bot.ShouldExit)
                {
                    n++;
                    bot.Log("tick " + n);
                    bot.Sleep(100);
                }
            }
        }
        """;

    /// <summary>The app engine on the real Skua host (the adapter over HeadlessSkuaSession) against the fake server.</summary>
    internal static (HeadlessBotEngine Engine, SkuaScriptHostAdapter Host) NewSkuaEngine(FakeAqw aqw, ScriptAssemblyStore? store = null, bool autoReconnect = true)
    {
        SkuaScriptHostAdapter host = new(null, store ?? new ScriptAssemblyStore(), ClientFileSources.SkuaScriptsDIR, o =>
        {
            o.WebApi = new AqwWebApi(new AqwWebApiOptions { BaseUri = FakeAqw.BaseUri }, aqw.Http);
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
            AutoReconnect = autoReconnect,
            PublishInterval = TimeSpan.FromMilliseconds(20),
            MemorySampleInterval = TimeSpan.FromMilliseconds(100),
            ScriptStopTimeout = TimeSpan.FromSeconds(30),
        }, host);
        return (engine, host);
    }

    /// <summary>
    /// Skua's own pause on the headless host: a real script stops at its next Sleep, the status says paused (still
    /// busy, so the memory watchdog keeps waiting), the session stays online, resume continues the same run, and a
    /// paused script stops normally.
    /// </summary>
    [Fact]
    public async Task ARealScriptPausesAndResumesInPlace()
    {
        await using FakeAqw aqw = new();
        (HeadlessBotEngine engine, SkuaScriptHostAdapter host) = NewSkuaEngine(aqw);
        List<LogEntry> log = new();
        engine.LogAdded += (_, e) =>
        {
            lock (log)
                log.Add(e);
        };
        int Ticks()
        {
            lock (log)
                return log.Count(l => l.Source == "script" && l.Message.StartsWith("tick ", StringComparison.Ordinal));
        }

        try
        {
            Assert.True(engine.CanPauseScript);
            await Assert.ThrowsAsync<EngineException>(() => engine.PauseScriptAsync()); // nothing runs
            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            string folder = Path.Combine(ClientFileSources.SkuaScriptsDIR, "DroidPause");
            Directory.CreateDirectory(folder);
            string script = Path.Combine(folder, "DroidPauseTicks.cs");
            await File.WriteAllTextAsync(script, TickScript);

            await engine.StartScriptAsync(script);
            await Sandbox.Until(() => Ticks() >= 3, 60000);
            DateTimeOffset? startedAt = engine.Snapshot.Script.StartedAt;

            await engine.PauseScriptAsync();
            Assert.True(engine.Snapshot.Script.IsPaused);
            Assert.True(engine.Snapshot.Script.IsBusy);
            Assert.True(host.Skua!.Scripts.ScriptPaused);
            await Task.Delay(300); // the tick in flight finishes its Sleep and blocks at the next one
            int frozen = Ticks();
            await Task.Delay(1500);
            Assert.InRange(Ticks(), frozen, frozen + 1);
            Assert.Equal(ConnectionPhase.Online, engine.Snapshot.Connection);
            Assert.True(host.Skua.Game.IsInGame);
            Assert.StartsWith("Paused DroidPauseTicks", Skua.App.Droid.Core.ViewModels.Formatting.ScriptLine(engine.Snapshot.Script));

            // Paused is busy for the memory watchdog: over the threshold it waits, it never restarts.
            MemoryWatchdog watchdog = new(new MemoryWatchdogOptions { ThresholdBytes = 100, MinimumUptime = TimeSpan.Zero, IdleGrace = TimeSpan.Zero });
            Assert.Equal(MemoryVerdict.Normal, watchdog.Evaluate(50, engine.Snapshot.Script.State)); // baseline under the threshold
            Assert.Equal(MemoryVerdict.RestartPending, watchdog.Evaluate(1000, engine.Snapshot.Script.State));

            // Pausing twice is a no-op; resume continues the same run.
            await engine.PauseScriptAsync();
            await engine.ResumeScriptAsync();
            Assert.False(engine.Snapshot.Script.Paused);
            Assert.Equal(startedAt, engine.Snapshot.Script.StartedAt);
            int resumedAt = Ticks();
            await Sandbox.Until(() => Ticks() >= resumedAt + 3, 10000);
            lock (log)
            {
                Assert.Contains(log, l => l.Message == "DroidPauseTicks.cs paused.");
                Assert.Contains(log, l => l.Message == "DroidPauseTicks.cs resumed.");
            }

            // A paused script stops (Skua cancels the wait).
            await engine.PauseScriptAsync();
            await engine.StopScriptAsync();
            await Sandbox.Until(() => engine.Snapshot.Script.State == ScriptRunState.Idle, 30000);
            Assert.False(engine.Snapshot.Script.Paused);
            Assert.False(host.Skua.Scripts.ScriptPaused);
            await Assert.ThrowsAsync<EngineException>(() => engine.ResumeScriptAsync());
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }
}
