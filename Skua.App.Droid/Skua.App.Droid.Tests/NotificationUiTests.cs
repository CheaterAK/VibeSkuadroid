using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.UI.Views;

namespace Skua.App.Droid.Tests;

/// <summary>The Tools hub and its notification pages, and the Drops tab, rendered headless with no binding errors.</summary>
public class NotificationUiTests
{
    [AvaloniaFact]
    public async Task TheToolsPagesRenderWithoutBindingErrors()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        FakeGameEvents events = new();
        await using TestApp app = new(scripts: new EventScriptHost(events));
        FakeDiscord discord = new();
        await using NotificationHub hub = Webhooks.Hub(app.Engine, app.Paths.AppDataDir, discord, new RecordingDropAlertSink(), app.Store);
        hub.Settings.Update(s => s with { WatchedItems = ["Pixie Wing", "Void Aura"] });
        hub.History.Add(new NotificationEntry(DateTimeOffset.UtcNow, NotificationKind.Item, "Pixie Wing (dropped)", Watched: true));
        hub.History.Add(new NotificationEntry(DateTimeOffset.UtcNow, NotificationKind.Error, "Script error (Farm): boom"));
        AppServices services = new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths) { Notifications = hub };
        using ShellViewModel shell = new(services);
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();

        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        await Sandbox.Until(() => app.Engine.GetWorldLists().Inventory.Count > 0);

        // Settings has a single entry into the hub; the hub lists the game tools, the four notification pages and the skill pages.
        shell.NavigateCommand.Execute("Settings");
        Render();
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(SettingsView)));
        Assert.False(window.GetVisualDescendantOfType(nameof(ToolsView))?.IsEffectivelyVisible ?? false);
        shell.Settings.OpenToolsCommand.Execute(null);
        Render();
        Assert.True(window.GetVisualDescendantOfType(nameof(ToolsView))!.IsEffectivelyVisible);
        Assert.Equal(["Fast Travel", "Loader", "Grabber", "Notification Center", "Notify Drop", "Junk Items", "Discord Webhook", "Advanced Skills", "CoreBots options", "Diagnostics"], shell.Settings.Tools.Entries.Select(e => e.Title));

        foreach (ToolEntry entry in shell.Settings.Tools.Entries.ToList())
        {
            shell.Settings.Tools.OpenCommand.Execute(entry);
            Render();
            object page = shell.Settings.Tools.CurrentPage!;
            Assert.NotNull(page);
            Assert.NotNull(window.GetVisualDescendantOfType(page.GetType().Name.Replace("ViewModel", "View")));
            Assert.Equal(entry.Title, shell.Settings.Tools.CurrentTitle);
            if (page is NotificationCenterViewModel center)
            {
                Assert.True(center.IsActive);
                Assert.Equal(2, center.FilteredNotifications.Count);
                hub.History.Add(new NotificationEntry(DateTimeOffset.UtcNow, NotificationKind.System, "Entered battleon"));
                Render();
                Assert.Equal(3, center.FilteredNotifications.Count);
            }
            if (page is JunkItemsViewModel junk)
            {
                Assert.True(junk.IsActive);
                Assert.NotEmpty(junk.FilteredItems);
                junk.FilteredItems[0].IsJunk = true;
                Render();
            }
            if (page is NotifyDropViewModel drops)
                Assert.Equal(["Pixie Wing", "Void Aura"], drops.NotifyDropList);
            if (page is DiscordWebhookViewModel webhook)
            {
                webhook.UrlInput = Webhooks.Url;
                webhook.SaveUrlCommand.Execute(null);
                Render();
                Assert.True(webhook.HasUrl);
            }
            // Back: the page, then the hub, then the settings.
            Assert.True(shell.GoBack());
            Render();
            Assert.True(shell.Settings.Tools.IsListVisible);
            if (page is NotificationCenterViewModel c)
                Assert.False(c.IsActive);
        }
        Assert.True(shell.GoBack());
        Render();
        Assert.False(shell.Settings.ShowTools);
        Assert.False(window.GetVisualDescendantOfType(nameof(ToolsView))?.IsEffectivelyVisible ?? false);
        Assert.True(shell.GoBack());
        Assert.Equal(AppPage.Home, shell.CurrentPage);

        // The Drops tab with a drop on both lists.
        app.Aqw.Server.LastSession!.Json(new System.Text.Json.Nodes.JsonObject
        {
            ["cmd"] = "dropItem",
            ["items"] = new System.Text.Json.Nodes.JsonObject { ["11"] = NotificationHubTests.Drop(11) },
        });
        await Sandbox.Until(() => app.Engine.GetWorldLists().Drops.Count == 1);
        events.Set([new JunkItem(11, "Pixie Wing", "Item")]);
        shell.NavigateCommand.Execute("World");
        shell.World.SelectTabCommand.Execute("Drops");
        for (int i = 0; i < 100 && shell.World.Drops.Count == 0; i++)
        {
            await Task.Delay(20);
            Render();
        }
        DropListItem drop = Assert.Single(shell.World.Drops);
        Assert.True(drop.Watched);
        Assert.True(drop.Junk);
        Render();

        window.Close();
        List<string> problems;
        lock (sink.Messages)
            problems = sink.Messages.Where(m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase) || m.Contains("Error", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
