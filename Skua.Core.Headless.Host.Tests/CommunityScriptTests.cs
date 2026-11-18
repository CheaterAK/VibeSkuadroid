using Skua.Core.Headless.Flash;
using System.Text;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>
/// Runs community scripts (auqw/Scripts, not in this repository) against the fake server and writes their logs and
/// the Flash gap log to a file. Runs only when SKUA_COMMUNITY_DATA (a Skua data folder whose VibeSkua/Scripts holds
/// the scripts) and SKUA_COMMUNITY_OUT are set; run it alone (Skua's data folder is fixed once per process).
/// SKUA_COMMUNITY_SCRIPTS: ';'-separated script paths relative to the Scripts folder; "@init" is a CoreBots wrapper.
/// </summary>
public sealed class CommunityScriptTests
{
    private const string CoreBotsWrapper = """
        //cs_include Scripts/CoreBots.cs
        using Skua.Core.Interfaces;

        public class HeadlessCoreBotsInit
        {
            public CoreBots Core => CoreBots.Instance;

            public void ScriptMain(IScriptInterface bot)
            {
                Core.SetOptions();
                Core.Logger("CoreBots initialised");
                Core.KillMonster("testmap", "r2", "Left", "Pixie", "Pixie Wing", 3, isTemp: false);
                Core.Logger("Pixie Wing: " + bot.Inventory.GetQuantity("Pixie Wing"));
                Core.SetOptions(false);
            }
        }
        """;

    [Fact]
    public async Task RunCommunityScripts()
    {
        string? data = Environment.GetEnvironmentVariable("SKUA_COMMUNITY_DATA");
        string? output = Environment.GetEnvironmentVariable("SKUA_COMMUNITY_OUT");
        if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(output))
            return;
        string[] scripts = (Environment.GetEnvironmentVariable("SKUA_COMMUNITY_SCRIPTS") ?? "@init").Split(';', StringSplitOptions.RemoveEmptyEntries);
        int timeout = int.TryParse(Environment.GetEnvironmentVariable("SKUA_COMMUNITY_TIMEOUT"), out int t) ? t : 120;
        StringBuilder report = new();
        List<string> log = new();
        await using FakeWorld world = new();
        await using HeadlessSkuaSession session = HeadlessSkuaSession.Create(new HeadlessSkuaOptions
        {
            DataDirectory = data,
            ApplicationVersionOverride = Environment.GetEnvironmentVariable("SKUA_COMMUNITY_VERSION"),
            WebApi = world.Api,
            Game = new HeadlessGameOptions { Session = world.SessionOptions },
            Log = e =>
            {
                if (e.Source is "script" or "game" or "dialog")
                {
                    lock (log)
                        log.Add($"{e.At:HH:mm:ss.fff} [{e.Source}] {e.Message}");
                }
            },
        });
        Assert.True(await session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"));
        session.Game.PacketWritten += (_, e) =>
        {
            lock (log)
                log.Add($"{e.At:HH:mm:ss.fff} [sent] {(e.Text.Length > 160 ? e.Text[..160] : e.Text)}");
        };
        foreach (string script in scripts)
        {
            string file = script == "@init"
                ? Path.Combine(data, "VibeSkua", "HeadlessCoreBotsInit.cs")
                : Path.Combine(data, "VibeSkua", "Scripts", script);
            if (script == "@init")
                await File.WriteAllTextAsync(file, CoreBotsWrapper);
            session.Gaps.Clear();
            lock (log)
                log.Clear();
            TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<string> errors = new();
            void OnStop(object? s, bool running)
            {
                if (!running)
                    stopped.TrySetResult();
            }
            void OnError(object? s, Exception e) => errors.Add(e.GetType().Name + ": " + e.Message);
            session.ScriptRunningChanged += OnStop;
            session.ScriptError += OnError;
            await FakeWorld.Until(() => !session.ScriptRunning, 10000);
            Exception? start = await session.StartScriptAsync(file);
            string ending = "ended";
            if (start is not null)
            {
                ending = "did not start: " + start.Message;
            }
            else
            {
                try
                {
                    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(timeout));
                }
                catch (TimeoutException)
                {
                    ending = $"stopped after {timeout} s";
                    await session.StopScriptAsync();
                }
            }
            session.ScriptRunningChanged -= OnStop;
            session.ScriptError -= OnError;
            report.AppendLine($"### {script}: {ending}");
            foreach (string e in errors)
                report.AppendLine("ERROR " + e);
            lock (log)
                report.AppendLine(string.Join(Environment.NewLine, log.TakeLast(80)));
            report.AppendLine(session.Gaps.Format());
            if (!session.Game.IsInGame)
                Assert.True(await session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"));
        }
        await File.WriteAllTextAsync(output, report.ToString());
    }
}
