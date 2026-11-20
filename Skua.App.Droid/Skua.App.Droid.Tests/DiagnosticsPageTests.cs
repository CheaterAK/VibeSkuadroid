using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Memory;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.Scripting;
using Skua.App.Droid.UI.Views;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Models.Diagnostics;

namespace Skua.App.Droid.Tests;

/// <summary>Settings > Tools > Diagnostics: what it reads, the packet rate, the process exits and the scrubbed bundle.</summary>
public class DiagnosticsPageTests
{
    private static ConnectionDetails Connection(long packetsIn, long packetsOut) =>
        new("Sandbox", "127.0.0.1:5588", "Ready", "1.0", packetsIn, packetsOut, packetsIn * 100, packetsOut * 50, 0, 0, 0, 0, null,
            TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(95), TimeSpan.FromMilliseconds(220), TimeSpan.FromMilliseconds(500));

    [Fact]
    public void PacketsPerMinuteAreMeasuredOverTheLastMinuteAndRestartWithANewConnection()
    {
        PacketRateMeter meter = new();
        DateTimeOffset t = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.Null(meter.Add(t, Connection(100, 50)));
        Assert.Null(meter.Add(t.AddSeconds(1), Connection(110, 55))); // under the minimum span
        PacketRate rate = meter.Add(t.AddSeconds(30), Connection(400, 200))!;
        Assert.Equal(600, rate.InPerMinute, 3);
        Assert.Equal(300, rate.OutPerMinute, 3);
        // Older than a minute: dropped from the window.
        rate = meter.Add(t.AddSeconds(90), Connection(1000, 500))!;
        Assert.InRange(rate.Window.TotalSeconds, 55, 61);
        // A new socket starts its counters at zero: the window starts over.
        Assert.Null(meter.Add(t.AddSeconds(91), Connection(3, 2)));
        Assert.Null(meter.Add(t.AddSeconds(92), null));
    }

    [Fact]
    public void ProcessExitsComeFromTheEventsLogNewestFirst()
    {
        DiagnosticsStore store = new(Path.Combine(Sandbox.NewDir(), "diagnostics"));
        store.Log("startup step 3");
        store.Log("previous exit (Report): 2026-11-19 10:00 pid 123 CRASH_NATIVE; last state: script Running");
        store.Log("previous exit (Ignore): 2026-11-19 11:00 pid 124 USER_REQUESTED token=abcdef123; last state: (not recorded)");
        IReadOnlyList<ExitRow> exits = DiagnosticsReport.Exits(store.ReadLogTail(100));
        Assert.Equal(2, exits.Count);
        Assert.StartsWith("previous exit (Ignore)", exits[0].Text);
        Assert.DoesNotContain("abcdef123", exits[0].Text);
        Assert.Contains("CRASH_NATIVE", exits[1].Text);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \[\d+\]$", exits[1].When);
    }

    [Fact]
    public void TheBundleCarriesEverySectionAndNoCredential()
    {
        SecretScrubber engineSecrets = new();
        engineSecrets.Add("hunter2-secret");
        engineSecrets.Add("fake-token-777");
        EngineSnapshot snapshot = EngineSnapshot.Initial with
        {
            Connection = ConnectionPhase.Online,
            AccountName = "HeroAccount",
            ServerName = "Sandbox",
            Character = new CharacterState("HeroChar", 30, "Healer", 1, 1, 1, 1, 0, "battleon", "Enter", "Spawn", 1, true, false, false),
        };
        DiagnosticsReading reading = new(
            DateTimeOffset.UtcNow,
            snapshot,
            Connection(1000, 400),
            new PacketRate(120, 60, TimeSpan.FromSeconds(60)),
            DiagnosticsReport.ReadMemory(MemoryStatus.Unknown with { RssBytes = 300 << 20 }, 1024, true, "normal", () => 250L << 20),
            [new FlashGapRow("Missing", "path:ui.mcPopup", "()", 3, DateTimeOffset.UtcNow, "object path not modelled; answered undefined")],
            [new ExitRow("2026-11-19", "previous exit (Report): CRASH")],
            true,
            new RuntimeReading([1234], true, ["Gold Coin"], false, [], true, [new DiagnosticEvent(DateTimeOffset.UtcNow, "Script", "Started", null, null, null)]));
        LogEntry[] log =
        [
            new(DateTimeOffset.UtcNow, EngineLogLevel.Info, "engine", "Logging in as HeroAccount with hunter2-secret"),
            new(DateTimeOffset.UtcNow, EngineLogLevel.Info, "session", "sent %xt%zm%login%fake-token-777% to the server"),
            new(DateTimeOffset.UtcNow, EngineLogLevel.Info, "notify", "posting to https://discord.com/api/webhooks/123456/abcDEF_secret-part?thread_id=9"),
            new(DateTimeOffset.UtcNow, EngineLogLevel.Info, "script", "mail me at someone@example.com, HeroChar says hi; password=letmein"),
        ];
        string bundle = DiagnosticsReport.Bundle(reading, "1.2.3", "Pixel test", log, ["2026-11-20 events line for heroaccount"], packetCapture: false,
            interception: "off", engineSecrets, ["HeroAccount", "HeroChar", null]);

        foreach (string section in new[] { "== Engine ==", "== Connection ==", "== Memory ==", "== Runtime helpers ==", "== Flash gaps ==", "== Process exits ==", "== Skua events ==", "== Events log", "== Engine log" })
            Assert.Contains(section, bundle);
        Assert.Contains("Latency: 80 ms last, 95 ms average", bundle);
        Assert.Contains("Packets per minute: 120 in, 60 out (last 60 s)", bundle);
        Assert.Contains("PSS: 250.0 MB", bundle);
        Assert.Contains("Watchdog: restarts above 1024 MB RSS; now normal", bundle);
        Assert.Contains("path:ui.mcPopup()", bundle);
        Assert.Contains("Registered quests: 1234", bundle);
        Assert.Contains("Packet interceptor: off", bundle);
        foreach (string secret in new[] { "hunter2-secret", "fake-token-777", "abcDEF_secret-part", "123456", "someone@example.com", "letmein", "HeroAccount", "heroaccount", "HeroChar" })
            Assert.DoesNotContain(secret, bundle);
        Assert.Contains("<webhook>", bundle);
        Assert.Contains("<email>", bundle);
        Assert.Contains("<account>", bundle);
        Assert.Contains(SecretScrubber.Marker, bundle);
    }

    [AvaloniaFact]
    public async Task TheDiagnosticsPageRendersRefreshesOnlyWhileShownAndShares()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        await using TestApp app = new(scripts: new PluginOnlyScriptHost(new FakePluginBackends()));
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths));
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();
        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
        async Task Until(Func<bool> condition)
        {
            for (int i = 0; i < 500 && !condition(); i++)
            {
                await Task.Delay(10);
                Render();
            }
            Assert.True(condition());
        }

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        shell.NavigateCommand.Execute("Settings");
        shell.Settings.OpenToolsCommand.Execute(null);
        ToolsViewModel tools = shell.Settings.Tools;
        DiagnosticsViewModel page = shell.Settings.DiagnosticPages.Diagnostics;
        Assert.False(page.Visible);
        tools.OpenCommand.Execute(tools.Entries.Single(e => e.Title == "Diagnostics"));
        Assert.True(page.Visible);
        await Until(() => page.Refreshes >= 2 && page.ConnectionRows.Any(r => r.Label == "Server"));
        Render();
        Control view = window.GetVisualDescendantOfType(nameof(DiagnosticsView))!;
        Assert.NotNull(view);
        Assert.Contains(page.ConnectionRows, r => r.Label == "Server" && r.Value.StartsWith("Sandbox (127.0.0.1:", StringComparison.Ordinal));
        Assert.Contains(page.MemoryRows, r => r.Label == "Watchdog" && r.Value.Contains("1024 MB", StringComparison.Ordinal));
        Assert.Equal("Not available: no Skua host in this build.", page.FlashGapsText);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "CONNECTION");

        // Leaving the page, the tab, or the app stops the reads.
        Assert.True(shell.GoBack());
        Assert.False(page.Visible);
        tools.OpenCommand.Execute(tools.Entries.Single(e => e.Title == "Diagnostics"));
        Assert.True(page.Visible);
        shell.SetUiVisible(false);
        Assert.False(page.Visible);
        shell.SetUiVisible(true);
        Assert.True(page.Visible);
        shell.NavigateCommand.Execute("Home");
        Assert.False(page.Visible);
        int reads = page.Refreshes;
        await Task.Delay(DiagnosticsViewModel.RefreshInterval * 2);
        Assert.InRange(page.Refreshes - reads, 0, 1);

        // Share: scrubbed, with the account name removed.
        page.ShareCommand.Execute(null);
        await Until(() => app.Platform.Shared is not null);
        Assert.Contains("== Connection ==", app.Platform.Shared);
        Assert.DoesNotContain(FakeAqw.Password, app.Platform.Shared);
        Assert.DoesNotContain("hero", app.Platform.Shared, StringComparison.OrdinalIgnoreCase);

        window.Close();
        List<string> problems;
        lock (sink.Messages)
            problems = sink.Messages.Where(m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase) || m.Contains("Error", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}

public partial class SessionIntegrationTests
{
    /// <summary>
    /// Diagnostics on the real Skua host over the fake server: the connection's server, latency and counters, a gap
    /// of the Flash bridge, Skua's runtime helpers and events, and a share bundle without the password or the token.
    /// </summary>
    [Fact]
    public async Task DiagnosticsReadTheRealSessionAndTheBundleIsScrubbed()
    {
        await using FakeAqw aqw = new();
        (SkuaScriptHostAdapter host, HeadlessBotEngine engine) = ToolsHost(aqw);
        try
        {
            RecordingPlatform platform = new();
            AppServices services = AppOver(engine, platform: platform);
            DiagnosticsViewModel page = new(services);
            await page.RefreshAsync();
            Assert.Equal([new InfoRow("Connection", "none (not logged in)")], page.ConnectionRows);

            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => engine.Snapshot.Character is not null && host.Skua!.Bot.Map.Loaded);
            // The login's character data request was answered: the round trip is measured.
            await Sandbox.Until(() => engine.GetConnectionDetails()?.RoundTrip is not null, 15000);
            ConnectionDetails details = engine.GetConnectionDetails()!;
            Assert.Equal(("Sandbox", "Ready"), (details.ServerName, details.State));
            Assert.True(details.PacketsIn > 0 && details.PacketsOut > 0);

            // A path the bridge does not model is a gap; Skua's drop list shows in the runtime helpers.
            _ = host.Skua!.Bot.Flash.GetGameObject("ui.droidDiagnosticsProbe.value");
            host.Skua.Bot.Drops.Add("Pixie Wing");
            page.RecordSkuaEvents = true;
            await page.RefreshAsync();
            Assert.Contains(page.FlashGaps, g => g.Name == "path:ui.droidDiagnosticsProbe.value" && g.Kind == "Missing" && g.Count >= 1);
            Assert.Contains(page.ConnectionRows, r => r.Label == "Latency" && r.Value.EndsWith(" ms average", StringComparison.Ordinal));
            Assert.Contains(page.RuntimeRows, r => r.Label == "Drop grabber" && r.Value.Contains("Pixie Wing", StringComparison.Ordinal));
            Assert.True(page.RecordSkuaEvents);

            engine.WriteLog(EngineLogLevel.Info, "test", "write to someone@example.com");
            string bundle = await page.BuildBundleAsync();
            Assert.Contains("path:ui.droidDiagnosticsProbe.value", bundle);
            Assert.DoesNotContain(FakeAqw.Password, bundle);
            Assert.DoesNotContain(FakeAqwServer.TokenPrefix, bundle);
            Assert.DoesNotContain("someone@example.com", bundle);
            Assert.DoesNotContain("hero", bundle, StringComparison.OrdinalIgnoreCase);
            host.Skua.Bot.Drops.Clear();
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }
}
