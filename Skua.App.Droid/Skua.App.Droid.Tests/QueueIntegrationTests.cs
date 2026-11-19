using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Scheduler;
using Skua.App.Droid.Scripting;
using Skua.Core.Models;

namespace Skua.App.Droid.Tests;

public partial class SessionIntegrationTests
{
    private const string QueuedScript = """
        using System.Collections.Generic;
        using Skua.Core.Interfaces;
        using Skua.Core.Options;

        public class DroidQueued
        {
            public string OptionsStorage = "DroidQueued";
            public bool DontPreconfigure = true;
            public List<IOption> Options = new()
            {
                new Option<int>("loops", "Loops", "How many loops", 3),
            };

            public void ScriptMain(IScriptInterface bot)
            {
                int loops = bot.Config!.Get<int>("loops");
                bot.Log("queued start loops=" + loops);
                for (int i = 0; i < loops * 10 && !bot.ShouldExit; i++)
                    bot.Sleep(100);
                bot.Log("queued done");
            }
        }
        """;

    private const string NextScript = """
        using Skua.Core.Interfaces;

        public class DroidQueuedNext
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Log("next ran");
            }
        }
        """;

    /// <summary>
    /// The queue on real Skua: an entry runs with its own option profile (Skua's OverrideStorage), the connection drops
    /// mid-entry, Skua's own relogin logs back in and restarts the script with the same profile, and the queue adopts
    /// that run (no second start, nothing skipped) before going on with the next entry.
    /// </summary>
    [Fact]
    public async Task TheQueueRunsRealScriptsWithProfilesAndSurvivesSkuasRelogin()
    {
        await using FakeAqw aqw = new();
        (HeadlessBotEngine engine, SkuaScriptHostAdapter host) = NewSkuaEngine(aqw);
        List<LogEntry> log = new();
        engine.LogAdded += (_, e) =>
        {
            lock (log)
                log.Add(e);
        };
        int Count(string text)
        {
            lock (log)
                return log.Count(l => l.Message.Contains(text, StringComparison.Ordinal));
        }

        string root = Sandbox.NewDir();
        ScriptScheduler? queue = null;
        try
        {
            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            host.Skua!.Bot.Options.SafeRelogin = false; // Skua's 800 ms relogin delay instead of 10 s
            host.Skua.Bot.Options.ReloginServer = "Sandbox"; // Skua's default (Twilly) is not on the fake server
            string folder = Path.Combine(ClientFileSources.SkuaScriptsDIR, "DroidQueue");
            Directory.CreateDirectory(folder);
            string first = Path.Combine(folder, "DroidQueued.cs");
            string second = Path.Combine(folder, "DroidQueuedNext.cs");
            await File.WriteAllTextAsync(first, QueuedScript);
            await File.WriteAllTextAsync(second, NextScript);

            queue = new ScriptScheduler(engine, new QueueStore(Path.Combine(root, "queue.json"), Path.Combine(root, "playlists")),
                ClientFileSources.SkuaScriptsDIR, ClientFileSources.SkuaOptionsDIR, QueueTesting.Fast with { ResumeGrace = TimeSpan.FromSeconds(20) });
            QueueEntry entry = queue.Add(first);
            queue.Update(entry with { DelayAfter = TimeSpan.FromMilliseconds(100) });
            QueueEntry next = queue.Add(second);
            queue.Update(next with { DelayAfter = TimeSpan.Zero });

            // The entry's own options (the desktop's "edit options" of a queue item), not the script's.
            ScriptOptionSet profile = await engine.GetScriptOptionsAsync(first, entry.OptionStorage);
            Assert.Equal(entry.OptionStorage, profile.Storage);
            Assert.Equal("3", profile.Items[0].Value);
            await engine.SaveScriptOptionsAsync(profile with { Items = [profile.Items[0] with { Value = "4" }] });
            Assert.Contains("Options:loops=4", await File.ReadAllLinesAsync(Path.Combine(ClientFileSources.SkuaOptionsDIR, entry.OptionStorage + ".cfg")));
            Assert.Equal("3", (await engine.GetScriptOptionsAsync(first)).Items[0].Value); // the script's own storage is untouched

            queue.Start();
            await Sandbox.Until(() => Count("queued start loops=4") == 1, 60000);
            await Sandbox.Until(() => queue.Status.Entries[0].Result.Status == "Running", 10000);

            // The connection drops: Skua's relogin stops the script, logs back in and restarts it with the same profile.
            aqw.Server.LastSession!.Close();
            await Sandbox.Until(() => Count("queued start loops=4") == 2, 60000);
            await Sandbox.Until(() => Count("next ran") == 1, 60000);
            await Sandbox.Until(() => !queue.IsRunning, 30000);

            Assert.Equal(1, Count("Starting DroidQueued.cs")); // the queue started it once; Skua restarted it once
            Assert.Equal(1, Count("Starting DroidQueuedNext.cs"));
            Assert.Equal(1, Count("queued done"));
            Assert.True(Count("resumed after the reconnect (restarted by Skua") == 1, Dump(log));
            Assert.All(queue.Status.Entries, e => Assert.Equal("Completed", e.Result.Status));
            Assert.Equal(1, queue.Status.Entries[0].Result.RunsDone);
            Assert.True(engine.Snapshot.ScriptCounters?.Relogins >= 1 || Count("relogin") > 0, Dump(log));
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(e.Message + "\n" + queue?.Status.Text + "\n" + Dump(log), e);
        }
        finally
        {
            queue?.Dispose();
            await engine.DisposeAsync();
        }
    }
}
