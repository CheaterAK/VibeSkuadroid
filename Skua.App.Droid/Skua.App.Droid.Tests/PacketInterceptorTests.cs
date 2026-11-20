using System.Text.Json.Nodes;
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
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Tests.Fakes;

namespace Skua.App.Droid.Tests;

/// <summary>The Packet Interceptor: rules on real frames of the fake server, its safety limits, and the page.</summary>
public class PacketInterceptorTests
{
    private static InterceptRule Rule(string name, InterceptDirection direction, string command, string find, InterceptAction action, string replace = "", bool enabled = true) =>
        new(Guid.NewGuid().ToString("N"), name, enabled, direction, command, find, action, replace);

    [Fact]
    public void RulesValidateMatchChainAndLog()
    {
        Assert.Null(Rule("a", InterceptDirection.Outbound, "ct", "x", InterceptAction.Replace, "y").Validate());
        Assert.Contains("needs the text", Rule("a", InterceptDirection.Outbound, "ct", "", InterceptAction.Replace, "y").Validate());
        Assert.Contains("drop everything", Rule("a", InterceptDirection.Both, "", "", InterceptAction.Block).Validate());
        Assert.Contains("letters", Rule("a", InterceptDirection.Both, "c t", "", InterceptAction.Block).Validate());
        Assert.Contains("one line", Rule("a", InterceptDirection.Both, "", "a\nb", InterceptAction.Block).Validate());
        Assert.Contains("same", Rule("a", InterceptDirection.Both, "", "a", InterceptAction.Replace, "a").Validate());

        PacketInterception ic = new(s => s.Replace("secret-42", "<redacted>", StringComparison.Ordinal));
        Assert.False(ic.IsActive);
        ic.SetRules([
            Rule("cell", InterceptDirection.Outbound, "moveToCell", "r2", InterceptAction.Replace, "r3"),
            Rule("pad", InterceptDirection.Outbound, "moveToCell", "Left", InterceptAction.Replace, "Right"),
            Rule("no chat", InterceptDirection.Inbound, "chatm", "", InterceptAction.Block),
            Rule("off", InterceptDirection.Both, "", "%", InterceptAction.Block, enabled: false),
        ]);
        Assert.False(ic.IsActive); // the master switch is off
        ic.Enabled = true;
        Assert.True(ic.IsActive);
        Assert.Throws<ArgumentException>(() => ic.SetRules([Rule("bad", InterceptDirection.Both, "", "", InterceptAction.Block)]));
        Assert.Throws<ArgumentException>(() => ic.SetRules(Enumerable.Range(0, PacketInterception.MaxRules + 1).Select(i => Rule($"r{i}", InterceptDirection.Both, "ct", "x", InterceptAction.Block))));

        InterceptedFrame move = new(outbound: true, "%xt%zm%moveToCell%900%r2%Left%secret-42%", "moveToCell");
        ic.Intercept(move);
        Assert.Equal("%xt%zm%moveToCell%900%r3%Right%secret-42%", move.Text);
        Assert.False(move.Blocked);
        InterceptedFrame chat = new(outbound: false, "%xt%chatm%-1%zone~hello%Other%", "chatm");
        ic.Intercept(chat);
        Assert.True(chat.Blocked);
        InterceptedFrame inbound = new(outbound: false, "%xt%moveToCell%-1%r2%", "moveToCell");
        ic.Intercept(inbound); // an outbound rule does not touch inbound messages
        Assert.False(inbound.Changed);

        IReadOnlyList<InterceptLogEntry> log = ic.GetLog();
        Assert.Equal(2, log.Count);
        Assert.Equal(("OUT", "cell, pad", "Jump"), (log[0].Direction, log[0].Rules, log[0].Categories));
        Assert.Contains("<redacted>", log[0].Before);
        Assert.DoesNotContain("secret-42", log[0].After);
        Assert.Equal(("BLOCK", "Chat"), (log[1].Direction, log[1].Categories));
        Assert.Equal((3, 1, 1), (ic.Seen, ic.Changed, ic.Blocked));
        Assert.StartsWith("on: 3 of 4 rules enabled", ic.StatusText);

        string json = PacketInterception.Serialize(ic.Rules);
        Assert.Equal(ic.Rules, PacketInterception.Deserialize(json));
        Assert.Contains("\"Outbound\"", json);
        Assert.Empty(PacketInterception.Deserialize("not json"));
        ic.ClearLog();
        Assert.Empty(ic.GetLog());
    }

    [Fact]
    public async Task RulesChangeAndDropRealFramesButNeverTheLogin()
    {
        await using FakeAqw aqw = new();
        await using HeadlessBotEngine engine = aqw.NewEngine(autoReconnect: false);
        PacketInterception ic = engine.Interception;
        List<LogEntry> lines = new();
        engine.LogAdded += (_, e) =>
        {
            lock (lines)
                lines.Add(e);
        };

        // On before the login, with a rule that would drop every game message both ways and one that would
        // rewrite the login: the login, the character load and the token are out of its reach.
        ic.SetRules([
            Rule("drop all", InterceptDirection.Both, "", "%", InterceptAction.Block),
            Rule("login", InterceptDirection.Outbound, "login", "hero", InterceptAction.Replace, "villain"),
        ]);
        ic.Enabled = true;
        await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        Assert.Equal(ConnectionPhase.Online, engine.Snapshot.Connection);
        FakeAqwServer.Session game = aqw.Server.LastSession!;
        Assert.Contains(game.Received, r => r.Text.Contains("%retrieveInventory%", StringComparison.Ordinal));
        Assert.DoesNotContain(ic.GetLog(), e => e.Before.Contains("loginResponse", StringComparison.Ordinal) || e.Before.Contains("moveToArea", StringComparison.Ordinal)
            || e.Before.Contains("retrieveInventory", StringComparison.Ordinal) || e.Before.Contains("loadInventoryBig", StringComparison.Ordinal) || e.Before.Contains("firstJoin", StringComparison.Ordinal));

        // Ready: now game messages are offered. The liveness probe (retrieveUserData) is never dropped.
        Assert.False(await engine.SendPacketAsync("%xt%zm%spamTest%1%dropped%"));
        await Task.Delay(200);
        Assert.DoesNotContain(game.Received, r => r.Text.Contains("%dropped%", StringComparison.Ordinal));
        int probes = game.Received.Count(r => r.Text.Contains("%retrieveUserData%", StringComparison.Ordinal));
        Assert.True(await engine.Session.Connection!.ProbeAsync(TimeSpan.FromSeconds(5)));
        Assert.True(game.Received.Count(r => r.Text.Contains("%retrieveUserData%", StringComparison.Ordinal)) > probes);
        Assert.Contains(ic.GetLog(), e => e.Blocked && e.Outbound && e.Before.Contains("%dropped%", StringComparison.Ordinal));
        Assert.All(ic.GetLog(), e => Assert.DoesNotContain(FakeAqwServer.TokenPrefix, e.Before + e.After));

        // Outbound replace on a real moveToCell: the server receives the changed cell.
        ic.SetRules([
            Rule("cell", InterceptDirection.Outbound, "moveToCell", "%r2%", InterceptAction.Replace, "%r3%"),
            Rule("gold", InterceptDirection.Inbound, "addGoldExp", "\"intGold\":100", InterceptAction.Replace, "\"intGold\":5"),
            Rule("no exp", InterceptDirection.Inbound, "addGoldExp", "\"intGold\":7", InterceptAction.Block),
            Rule("slow", InterceptDirection.Inbound, "warning", "", InterceptAction.Block),
        ]);
        Assert.True(await engine.SendPacketAsync($"%xt%zm%moveToCell%{game.Room}%r2%Left%"));
        await Sandbox.Until(() => game.Cell == "r3");

        // Inbound replace and block on real JSON frames: the world model applies what the rules left.
        long gold = engine.Session.World.GetPlayer()!.Gold;
        game.Json(new JsonObject { ["cmd"] = "addGoldExp", ["intGold"] = 100, ["intExp"] = 0 });
        await Sandbox.Until(() => engine.Session.World.GetPlayer()!.Gold == gold + 5);
        game.Json(new JsonObject { ["cmd"] = "addGoldExp", ["intGold"] = 7, ["intExp"] = 0 });
        game.Json(new JsonObject { ["cmd"] = "addGoldExp", ["intGold"] = 1, ["intExp"] = 0 });
        await Sandbox.Until(() => engine.Session.World.GetPlayer()!.Gold == gold + 6);
        Assert.Contains(ic.GetLog(), e => e is { Blocked: true, Outbound: false, Rules: "no exp" });

        // The "slow down" warning cannot be hidden from the pacer.
        long warnings = engine.GetConnectionDetails()!.SlowDownWarnings;
        game.Str("warning", "Please slow down. Last action was too soon!");
        await Sandbox.Until(() => engine.GetConnectionDetails()!.SlowDownWarnings == warnings + 1);
        Assert.DoesNotContain(ic.GetLog(), e => e.Rules == "slow");

        // The connection counts what was changed and dropped; the engine logged the switch.
        ConnectionStatistics stats = engine.Session.Connection!.GetStatistics();
        Assert.True(stats.InterceptedChanged >= 2 && stats.InterceptedBlocked >= 2);
        lock (lines)
            Assert.Contains(lines, l => l.Source == "interceptor" && l.Message.StartsWith("Packet interceptor on", StringComparison.Ordinal));

        // Off: messages pass untouched.
        ic.Enabled = false;
        Assert.True(await engine.SendPacketAsync($"%xt%zm%moveToCell%{game.Room}%r2%Left%"));
        await Sandbox.Until(() => game.Cell == "r2");
    }

    [Fact]
    public async Task ThePageEditsRulesAsksBeforeTurningOnAndShowsTheLog()
    {
        await using TestApp app = new();
        PluginStore store = new(Path.Combine(app.Paths.AppDataDir, "tools.json"));
        PacketInterceptorViewModel page = new(app.Services, store);
        Assert.True(page.IsAvailable);
        Assert.False(page.IsOn);
        Assert.Equal("off", page.StatusText);

        // Turning on without a rule: refused.
        await page.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("Add or enable a rule first.", page.Message);

        page.NewRuleCommand.Execute(null);
        Assert.True(page.IsEditing);
        page.EditAction = InterceptAction.Block;
        page.SaveRuleCommand.Execute(null);
        Assert.Contains("drop everything", page.EditError);
        page.EditName = "jump";
        page.EditAction = InterceptAction.Replace;
        page.EditCommand = "moveToCell";
        page.EditFind = "%r2%";
        page.EditReplace = "%r3%";
        page.SaveRuleCommand.Execute(null);
        Assert.False(page.IsEditing);
        InterceptRule rule = Assert.Single(page.Rules);
        Assert.Equal("moveToCell containing \"%r2%\": replace with \"%r3%\"", rule.Summary);
        Assert.False(page.GoBack());

        // Back closes the editor first.
        page.EditRuleCommand.Execute(rule);
        Assert.True(page.GoBack());
        Assert.False(page.IsEditing);

        // On: asks with the warning; cancel keeps it off.
        Task toggle = page.ToggleCommand.ExecuteAsync(null);
        await Sandbox.Until(() => page.Confirm.IsOpen);
        Assert.Contains("flagged", page.Confirm.Text, StringComparison.Ordinal);
        page.Confirm.CancelCommand.Execute(null);
        await toggle;
        Assert.False(app.Engine.Interception.Enabled);
        toggle = page.ToggleCommand.ExecuteAsync(null);
        await Sandbox.Until(() => page.Confirm.IsOpen);
        page.Confirm.ConfirmCommand.Execute(null);
        await toggle;
        Assert.True(page.IsOn);
        Assert.Equal("Turn off", page.ToggleText);

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        FakeAqwServer.Session game = app.Aqw.Server.LastSession!;
        await app.Engine.SendPacketAsync($"%xt%zm%moveToCell%{game.Room}%r2%Left%");
        await Sandbox.Until(() => game.Cell == "r3");
        page.RefreshLog(force: true);
        InterceptLogEntry entry = Assert.Single(page.Log);
        Assert.Equal(("OUT", "jump", "Jump"), (entry.Direction, entry.Rules, entry.Categories));
        page.LogFilter = "nothing like it";
        Assert.Empty(page.Log);
        page.LogFilter = "jump";
        Assert.Single(page.Log);
        page.ShareLogCommand.Execute(null);
        Assert.StartsWith("[OUT] %xt%zm%moveToCell%", app.Platform.Shared);

        // Rules are kept (not the switch); disabling a rule and deleting it.
        PacketInterceptorViewModel again = new(app.Services, new PluginStore(Path.Combine(app.Paths.AppDataDir, "tools.json")));
        Assert.Equal(rule with { }, Assert.Single(again.Rules));
        page.ToggleRuleCommand.Execute(page.Rules[0]);
        Assert.False(page.Rules[0].Enabled);
        Assert.False(app.Engine.Interception.IsActive);
        Task delete = page.DeleteRuleCommand.ExecuteAsync(page.Rules[0]);
        await Sandbox.Until(() => page.Confirm.IsOpen);
        page.Confirm.ConfirmCommand.Execute(null);
        await delete;
        Assert.Empty(page.Rules);
        page.Dispose();
        again.Dispose();
    }

    [AvaloniaFact]
    public async Task ThePacketInterceptorPageRendersWithRulesAndLog()
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
        tools.OpenCommand.Execute(tools.Entries.Single(e => e.Title == "Packet Interceptor"));
        Render();
        PacketInterceptorViewModel page = shell.Settings.PacketPages.Interceptor;
        Assert.True(page.Visible);
        Control view = window.GetVisualDescendantOfType(nameof(PacketInterceptorView))!;
        Assert.NotNull(view);

        page.NewRuleCommand.Execute(null);
        Render();
        Assert.Contains(view.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible);
        page.EditCommand = "moveToCell";
        page.EditFind = "%r2%";
        page.EditReplace = "%r3%";
        page.SaveRuleCommand.Execute(null);
        app.Engine.Interception.Enabled = true;
        FakeAqwServer.Session game = app.Aqw.Server.LastSession!;
        await app.Engine.SendPacketAsync($"%xt%zm%moveToCell%{game.Room}%r2%Left%");
        for (int i = 0; i < 300 && page.Log.Count == 0; i++)
        {
            await Task.Delay(10);
            Render();
        }
        Assert.Single(page.Log);
        Render();
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("· Jump", StringComparison.Ordinal) == true);
        // Back: the editor is closed, so back closes the page; the log stops refreshing.
        page.EditRuleCommand.Execute(page.Rules[0]);
        Assert.True(shell.GoBack());
        Assert.False(page.IsEditing);
        Assert.True(shell.GoBack());
        Assert.False(page.Visible);
        app.Engine.Interception.Enabled = false;

        window.Close();
        List<string> problems;
        lock (sink.Messages)
            problems = sink.Messages.Where(m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase) || m.Contains("Error", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
