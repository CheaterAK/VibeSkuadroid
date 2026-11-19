using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Scripting;
using Skua.Core.Headless;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Models;

namespace Skua.App.Droid.Tests;

/// <summary>
/// The app engine on the real Skua host (<see cref="SkuaScriptHostAdapter"/> over HeadlessSkuaSession) against the
/// fake AQW server: login through Skua's own code, a real script with an include started, run, stopped and started
/// again without loading the include a second time, script options, dialogs, and a logout that keeps the session but
/// drops every credential. One HeadlessSkuaSession per process: everything is in this one test.
/// </summary>
public class SessionIntegrationTests
{
    private const string Include = """
        using Skua.Core.Interfaces;

        public class DroidInclude
        {
            public static int Runs;

            public string Hello(IScriptInterface bot)
            {
                Runs++;
                return "hello " + bot.Player.Username;
            }
        }
        """;

    private const string Script = """
        //cs_include Scripts/DroidSession/DroidInclude.cs
        using System.Collections.Generic;
        using Skua.Core.Interfaces;
        using Skua.Core.Options;

        public class DroidSessionRun
        {
            public string OptionsStorage = "DroidSessionRun";
            public bool DontPreconfigure = true;
            public List<IOption> Options = new()
            {
                new Option<int>("loops", "Loops", "How many loops", 3),
                new Option<bool>("fast", "Fast", "Go fast", false),
                new Option<DroidMode>("mode", "Mode", "Which mode", DroidMode.Calm),
                new Option<string>("note", "Note", "Free text", "none", true),
            };

            public void ScriptMain(IScriptInterface bot)
            {
                bot.Log(new DroidInclude().Hello(bot) + " runs=" + DroidInclude.Runs + " loops=" + bot.Config!.Get<int>("loops") + " mode=" + bot.Config.Get<DroidMode>("mode"));
                bool? answer = bot.ShowMessageBox("Proceed with the optional step?", "Question", true);
                bot.Log("answer=" + answer);
                while (!bot.ShouldExit)
                    bot.Sleep(50);
            }
        }

        public enum DroidMode
        {
            Calm,
            Fast_Mode,
        }
        """;

    [Fact]
    public async Task ScriptsRunThroughTheEngineAndUnchangedIncludesLoadOnce()
    {
        await using FakeAqw aqw = new();
        ScriptAssemblyStore store = new();
        List<string> loads = new();
        store.Loaded += (_, a) =>
        {
            lock (loads)
                loads.Add(a.GetName().Name!);
        };
        SkuaScriptHostAdapter host = new(null, store, ClientFileSources.SkuaScriptsDIR, o =>
        {
            o.WebApi = new AqwWebApi(new AqwWebApiOptions { BaseUri = FakeAqw.BaseUri }, aqw.Http);
            o.Game = new HeadlessGameOptions
            {
                Session = aqw.NewSession().Options,
                Client = new GameClientOptions { MapLoadTime = TimeSpan.FromMilliseconds(200) },
                DownloadMapCells = false,
            };
        });
        List<LogEntry> log = new();
        HeadlessBotEngine engine = new(new HeadlessEngineOptions
        {
            SessionFactory = () => throw new InvalidOperationException("the host owns the session"),
            PublishInterval = TimeSpan.FromMilliseconds(20),
            MemorySampleInterval = TimeSpan.FromMilliseconds(100),
            ScriptStopTimeout = TimeSpan.FromSeconds(30),
        }, host);
        engine.LogAdded += (_, e) =>
        {
            lock (log)
                log.Add(e);
        };
        bool Logged(string text)
        {
            lock (log)
                return log.Any(l => l.Message.Contains(text, StringComparison.Ordinal));
        }

        try
        {
            // One session: the engine's, Skua's game and the Flash bridge's are the same object.
            HeadlessSkuaSessionView skua = new(host);
            Assert.True(engine.ScriptSupport.Available, engine.ScriptSupport.Reason);
            Assert.Same(skua.Game.Session, engine.Session);
            Assert.Same(skua.Game, skua.FlashGame);

            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            Assert.Equal(ConnectionPhase.Online, engine.Snapshot.Connection);
            Assert.Equal("Sandbox", engine.Snapshot.ServerName);
            Assert.True(skua.Game.IsInGame);
            await Sandbox.Until(() => engine.Snapshot.Character is not null && engine.Snapshot.ScriptCounters is not null);

            string folder = Path.Combine(ClientFileSources.SkuaScriptsDIR, "DroidSession");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "DroidInclude.cs"), Include);
            string script = Path.Combine(folder, "DroidSessionRun.cs");
            await File.WriteAllTextAsync(script, Script);

            // Options: compiled through the store, edited, saved where Skua reads them.
            ScriptOptionSet options = await engine.GetScriptOptionsAsync(script);
            Assert.Equal("DroidSessionRun", options.Storage);
            Assert.Equal(["loops", "fast", "mode", "note"], options.Items.Select(i => i.Name));
            Assert.Equal(ScriptOptionKind.Number, options.Items[0].Kind);
            Assert.Equal(ScriptOptionKind.Toggle, options.Items[1].Kind);
            Assert.Equal(["Calm", "Fast_Mode"], options.Items[2].Choices);
            Assert.True(options.Items[3].Transient);
            await Assert.ThrowsAsync<EngineException>(() => engine.SaveScriptOptionsAsync(options with
            {
                Items = options.Items.Select(i => i.Name == "loops" ? i with { Value = "many" } : i).ToList(),
            }));
            await engine.SaveScriptOptionsAsync(options with
            {
                Items = options.Items.Select(i => i.Name switch
                {
                    "loops" => i with { Value = "7" },
                    "mode" => i with { Value = "Fast_Mode" },
                    _ => i,
                }).ToList(),
            });
            Assert.Contains("Options:loops=7", await File.ReadAllLinesAsync(Path.Combine(ClientFileSources.SkuaOptionsDIR, "DroidSessionRun.cfg")));
            Assert.Equal("7", (await engine.GetScriptOptionsAsync(script)).Items[0].Value);
            int afterOptions = store.Count;
            Assert.Equal(2, afterOptions); // the include and the script, loaded once for both option reads

            // First run.
            await engine.StartScriptAsync(script);
            await Sandbox.Until(() => Logged("runs=1"), 60000);
            await Sandbox.Until(() => engine.Snapshot.Script.State == ScriptRunState.Running);
            Assert.True(Logged("hello hero runs=1 loops=7 mode=Fast_Mode"), Dump(log));
            await Sandbox.Until(() => Logged("answer=False"));
            Assert.True(Logged("Question: Proceed with the optional step? -> No"), Dump(log));
            await engine.StopScriptAsync();
            await Sandbox.Until(() => engine.Snapshot.Script.State == ScriptRunState.Idle, 30000);
            Assert.Equal(afterOptions, store.Count);

            // Second run: nothing is loaded again, and the include's statics survived (documented consequence).
            int loadsBefore;
            lock (loads)
                loadsBefore = loads.Count;
            await engine.StartScriptAsync(script);
            await Sandbox.Until(() => engine.Snapshot.Script.State == ScriptRunState.Running);
            Assert.Equal(ScriptRunState.Running, engine.Snapshot.Script.State);
            await engine.StopScriptAsync();
            await Sandbox.Until(() => engine.Snapshot.Script.State == ScriptRunState.Idle, 30000);
            Assert.Equal(afterOptions, store.Count);
            lock (loads)
            {
                Assert.Equal(loadsBefore, loads.Count);
                Assert.Single(loads, n => n == "DroidInclude");
            }
            await Sandbox.Until(() => engine.Snapshot.Memory.ScriptAssembliesLoaded == store.Count);

            // Logout keeps the one session but forgets every credential in it.
            AqwSession session = engine.Session;
            await engine.LogoutAsync();
            Assert.Same(session, engine.Session);
            Assert.Same(skua.Game.Session, engine.Session);
            Assert.Equal(ConnectionPhase.Offline, engine.Snapshot.Connection);
            Assert.Equal(string.Empty, skua.Game.Password);
            Assert.Equal(string.Empty, skua.Game.Username);
            Assert.Null(session.Account);
            Assert.Empty(skua.Game.Servers);
            Assert.Null(engine.Snapshot.ScriptCounters);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReconnectAsync());

            // And logs in again on it.
            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, "Sandbox"));
            Assert.Equal(ConnectionPhase.Online, engine.Snapshot.Connection);
            Assert.Same(session, engine.Session);

            // Wrong password: a message, no session change.
            await engine.LogoutAsync();
            EngineException wrong = await Assert.ThrowsAsync<EngineException>(() => engine.LoginAsync(new LoginRequest("hero", "nope-nope", null)));
            Assert.Contains("Wrong password", wrong.Message);
            Assert.Equal(ConnectionPhase.Failed, engine.Snapshot.Connection);
            lock (log)
                Assert.DoesNotContain(log, l => l.Message.Contains(FakeAqw.Password, StringComparison.Ordinal));
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    private static string Dump(List<LogEntry> log)
    {
        lock (log)
            return string.Join("\n", log.Select(l => l.Display));
    }

    /// <summary>Reads the adapter's Skua session without the test project referencing the host assembly types directly.</summary>
    private sealed class HeadlessSkuaSessionView
    {
        public HeadlessSkuaSessionView(SkuaScriptHostAdapter host)
        {
            Game = host.Skua!.Game;
            FlashGame = host.Skua.Flash.Game;
        }

        public HeadlessGame Game { get; }

        public HeadlessGame FlashGame { get; }
    }
}
