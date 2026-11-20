using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Packets;
using Skua.App.Droid.Core.Plugins;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.UI.Views;
using Skua.Core.Headless.Tests.Fakes;

namespace Skua.App.Droid.Tests;

/// <summary>The Packet Spammer: its limits, its automatic stops, and the page.</summary>
public class PacketSpammerTests
{
    private const string Spam = "%xt%zm%spamTest%1%ping%";

    private static int Count(FakeAqwServer.Session game) => game.Received.Count(r => r.Text.Contains("%spamTest%", StringComparison.Ordinal));

    [Fact]
    public void OnlyOneLineGamePacketsAreAccepted()
    {
        Assert.Null(PacketSpammer.Validate(Spam));
        Assert.Null(PacketSpammer.Validate("  %xt%zm%cmd%1%tfer%hero%battleon%  "));
        Assert.Equal("The packet is empty.", PacketSpammer.Validate(" "));
        Assert.Contains("game packets", PacketSpammer.Validate("<msg t='sys'><body action='verChk' r='0'></body></msg>"));
        Assert.Contains("game packets", PacketSpammer.Validate("{\"t\":\"xt\"}"));
        Assert.Contains("game packets", PacketSpammer.Validate("%xt%moveToArea%-1%"));
        Assert.Equal("A packet is one line.", PacketSpammer.Validate("%xt%zm%a%1%\n%xt%zm%b%1%"));
        Assert.Contains("no command", PacketSpammer.Validate("%xt%zm%%1%"));
        Assert.Contains("Login", PacketSpammer.Validate("%xt%zm%firstJoin%1%"));
        Assert.Contains("longer", PacketSpammer.Validate("%xt%zm%a%1%" + new string('x', PacketSpammer.MaxPacketLength)));
    }

    [Fact]
    public void TheMinimumIntervalFollowsTheGamesPacing()
    {
        ConnectionDetails live = new("S", "h:1", "Ready", "1", 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, TimeSpan.FromMilliseconds(220), TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromSeconds(1), SpammerLimits.Default.MinimumFor(live));
        Assert.Equal(TimeSpan.FromSeconds(1), SpammerLimits.Default.MinimumFor(null));
        Assert.Equal(TimeSpan.FromMilliseconds(1600), SpammerLimits.Default.MinimumFor(live with { SlowSendGap = TimeSpan.FromMilliseconds(800) }));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), SpammerLimits.Default.MinimumFor(live with { SendGap = TimeSpan.FromMilliseconds(500) }));
        Assert.Equal(500, SpammerLimits.Default.MaxSendsPerRun);
    }

    [Fact]
    public async Task ARunIsPacedCappedAndNeedsALogin()
    {
        await using FakeAqw aqw = new();
        await using HeadlessBotEngine engine = aqw.NewEngine();
        // The fake's pacer: 15 ms gap, 60 ms after a warning. The floor here is 50 ms: the pacing wins (120 ms).
        PacketSpammer spammer = new(engine, new SpammerLimits(TimeSpan.FromMilliseconds(50), 5));
        Assert.Equal(SpamStopReason.Disconnected, (await spammer.RunAsync([Spam], TimeSpan.Zero, 3, null, CancellationToken.None)).Reason);

        await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        Assert.Equal(TimeSpan.FromMilliseconds(120), spammer.MinimumInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(120), spammer.Clamp(TimeSpan.FromMilliseconds(1)));
        Assert.Equal(SpamStopReason.Failed, (await spammer.RunAsync(["<msg/>"], TimeSpan.Zero, 3, null, CancellationToken.None)).Reason);
        FakeAqwServer.Session game = aqw.Server.LastSession!;

        List<SpamProgress> progress = new();
        Stopwatch watch = Stopwatch.StartNew();
        SpamResult result = await spammer.RunAsync([Spam, "%xt%zm%spamTest%1%pong%"], TimeSpan.FromMilliseconds(1), 1000, new SyncProgress(progress.Add), CancellationToken.None);
        Assert.Equal(SpamStopReason.LimitReached, result.Reason);
        Assert.Equal(5, result.Sent);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(4 * 120), $"took {watch.ElapsedMilliseconds} ms");
        await Sandbox.Until(() => Count(game) == 5);
        Assert.Equal([0, 1, 0, 1, 0], progress.Select(p => p.Index));
        Assert.All(progress, p => Assert.Equal(5, p.MaxSends));
        List<long> times = game.Received.Where(r => r.Text.Contains("%spamTest%", StringComparison.Ordinal)).Select(r => r.Timestamp).ToList();
        for (int i = 1; i < times.Count; i++)
            Assert.True(Stopwatch.GetElapsedTime(times[i - 1], times[i]) >= TimeSpan.FromMilliseconds(100), "packets closer than the minimum interval");
        Assert.Contains(engine.GetRecentLog(), l => l.Source == "spammer" && l.Message.Contains("at most 5 sends", StringComparison.Ordinal));
        Assert.DoesNotContain(engine.GetRecentLog(), l => l.Message == "Raw packet sent.");

        // Stop.
        using CancellationTokenSource cts = new();
        Task<SpamResult> run = spammer.RunAsync([Spam], TimeSpan.FromMilliseconds(200), 5, null, cts.Token);
        await Sandbox.Until(() => Count(game) >= 6);
        cts.Cancel();
        Assert.Equal(SpamStopReason.Stopped, (await run).Reason);
    }

    [Fact]
    public async Task ARunStopsOnASlowDownWarningAndOnADisconnect()
    {
        await using FakeAqw aqw = new();
        await using HeadlessBotEngine engine = aqw.NewEngine(autoReconnect: true);
        await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        FakeAqwServer.Session game = aqw.Server.LastSession!;
        PacketSpammer spammer = new(engine, new SpammerLimits(TimeSpan.FromMilliseconds(50), 500));

        // The server answers "slow down" to packets closer than 2 s: the run ends at the warning.
        aqw.Server.SlowDownBelow = TimeSpan.FromSeconds(2);
        SpamResult slow = await spammer.RunAsync([Spam], TimeSpan.Zero, 500, null, CancellationToken.None);
        Assert.Equal(SpamStopReason.SlowDown, slow.Reason);
        Assert.InRange(slow.Sent, 1, 4);
        Assert.Contains("slow down", slow.Message, StringComparison.Ordinal);
        aqw.Server.SlowDownBelow = null;
        await Task.Delay(200);

        // The connection drops in the middle of a run: it ends, even though the engine reconnects by itself.
        Task<SpamResult> run = spammer.RunAsync([Spam], TimeSpan.FromMilliseconds(150), 500, null, CancellationToken.None);
        int before = Count(game);
        await Sandbox.Until(() => Count(game) >= before + 2);
        game.Close();
        SpamResult dropped = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SpamStopReason.Disconnected, dropped.Reason);
        await Sandbox.Until(() => engine.Snapshot.IsOnline, 10000);
        int afterReconnect = aqw.Server.LastSession!.Received.Count(r => r.Text.Contains("%spamTest%", StringComparison.Ordinal));
        await Task.Delay(400);
        Assert.Equal(afterReconnect, aqw.Server.LastSession!.Received.Count(r => r.Text.Contains("%spamTest%", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ThePageAsksWithTheWarningKeepsItsListAndRaisesAShortInterval()
    {
        await using TestApp app = new();
        PluginStore store = new(Path.Combine(app.Paths.AppDataDir, "tools.json"));
        PacketSpammerViewModel page = new(app.Services, store, new SpammerLimits(TimeSpan.FromMilliseconds(50), 3));

        page.PacketText = "<msg/>";
        page.AddPacketCommand.Execute(null);
        Assert.Empty(page.Packets);
        Assert.Contains("game packets", page.Message);
        page.PacketText = Spam;
        page.AddPacketCommand.Execute(null);
        Assert.Equal([Spam], page.Packets);
        Assert.Equal(string.Empty, page.PacketText);

        // Offline: refused before any question.
        await page.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("Log in first.", page.Message);
        Assert.False(page.Confirm.IsOpen);

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        page.Apply(app.Engine.Snapshot);
        FakeAqwServer.Session game = app.Aqw.Server.LastSession!;
        page.IntervalMs = 10;
        page.MaxSends = 99;

        // The question carries the warning; Cancel sends nothing.
        Task toggle = page.ToggleCommand.ExecuteAsync(null);
        await Sandbox.Until(() => page.Confirm.IsOpen);
        Assert.Contains("flag or ban", page.Confirm.Text, StringComparison.Ordinal);
        Assert.Contains("raised from 10 ms", page.Confirm.Text, StringComparison.Ordinal);
        page.Confirm.CancelCommand.Execute(null);
        await toggle;
        Assert.False(page.IsRunning);
        Assert.Equal(0, Count(game));

        toggle = page.ToggleCommand.ExecuteAsync(null);
        await Sandbox.Until(() => page.Confirm.IsOpen);
        page.Confirm.ConfirmCommand.Execute(null);
        await Sandbox.Until(() => page.IsRunning || page.LastResult is not null);
        Assert.Equal(120m, page.IntervalMs);
        Assert.Equal(3m, page.MaxSends);
        await toggle;
        Assert.Equal(SpamStopReason.LimitReached, page.LastResult!.Reason);
        Assert.False(page.IsRunning);
        await Sandbox.Until(() => Count(game) == 3);

        // The list and the options are kept.
        PacketSpammerViewModel again = new(app.Services, new PluginStore(Path.Combine(app.Paths.AppDataDir, "tools.json")));
        Assert.Equal([Spam], again.Packets);
        Assert.Equal(120m, again.IntervalMs);

        // Clear asks first.
        Task clear = page.ClearPacketsCommand.ExecuteAsync(null);
        await Sandbox.Until(() => page.Confirm.IsOpen);
        page.Confirm.ConfirmCommand.Execute(null);
        await clear;
        Assert.Empty(page.Packets);
        Assert.False(page.HasPackets);
    }

    [AvaloniaFact]
    public async Task ThePacketSpammerPageRendersRunsAndStops()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        await using TestApp app = new();
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths));
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();
        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        shell.NavigateCommand.Execute("Settings");
        shell.Settings.OpenToolsCommand.Execute(null);
        ToolsViewModel tools = shell.Settings.Tools;
        tools.OpenCommand.Execute(tools.Entries.Single(e => e.Title == "Packet Spammer"));
        Render();
        PacketSpammerViewModel page = shell.Settings.PacketPages.Spammer;
        Control view = window.GetVisualDescendantOfType(nameof(PacketSpammerView))!;
        Assert.NotNull(view);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == PacketSpammerViewModel.WarningText);

        page.PacketText = Spam;
        page.AddPacketCommand.Execute(null);
        page.MaxSends = 500;
        Render();
        Task run = page.ToggleCommand.ExecuteAsync(null);
        for (int i = 0; i < 300 && !page.Confirm.IsOpen; i++)
        {
            await Task.Delay(10);
            Render();
        }
        Assert.True(page.Confirm.IsOpen);
        Render();
        Assert.True(window.GetVisualDescendantOfType(nameof(ConfirmOverlay))!.IsEffectivelyVisible);
        page.Confirm.ConfirmCommand.Execute(null);
        for (int i = 0; i < 300 && !page.RunStatus.StartsWith("Sending", StringComparison.Ordinal); i++)
        {
            await Task.Delay(10);
            Render();
        }
        Assert.True(page.IsRunning);
        Assert.Equal("Stop", page.ToggleText);
        // Back cancels nothing here (no question open), the hub keeps the run; Stop ends it.
        await page.ToggleCommand.ExecuteAsync(null);
        await run;
        Render();
        Assert.False(page.IsRunning);
        Assert.Equal(SpamStopReason.Stopped, page.LastResult!.Reason);

        window.Close();
        List<string> problems;
        lock (sink.Messages)
            problems = sink.Messages.Where(m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase) || m.Contains("Error", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private sealed class SyncProgress(Action<SpamProgress> add) : IProgress<SpamProgress>
    {
        public void Report(SpamProgress value)
        {
            lock (this)
                add(value);
        }
    }
}
