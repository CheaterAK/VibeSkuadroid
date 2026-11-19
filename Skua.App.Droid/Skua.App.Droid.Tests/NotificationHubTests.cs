using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Core.ViewModels;

namespace Skua.App.Droid.Tests;

/// <summary>The process-wide hub: drop watch alerts, Notification Center history, junk settings handed to the host.</summary>
public class NotificationHubTests
{
    /// <summary>A drop as the fake server offers it after a kill.</summary>
    internal static System.Text.Json.Nodes.JsonObject Drop(int id)
    {
        System.Text.Json.Nodes.JsonObject item = Skua.Core.Headless.Tests.Fakes.FakeAqwServer.Copy(id);
        item["iQty"] = 1;
        return item;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeGameEvents Events { get; } = new();
        public RecordingDropAlertSink Alerts { get; } = new();
        public FakeDiscord Discord { get; } = new();
        public TestApp App { get; }
        public NotificationHub Hub { get; }

        public Fixture()
        {
            App = new TestApp(scripts: new EventScriptHost(Events));
            Hub = Webhooks.Hub(App.Engine, App.Paths.AppDataDir, Discord, Alerts, App.Store);
        }

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync();
            await App.DisposeAsync();
        }
    }

    [Fact]
    public async Task AWatchedDropPostsANotificationAndIsMarkedInTheHistory()
    {
        await using Fixture f = new();
        (TestApp app, FakeGameEvents events, NotificationHub hub, RecordingDropAlertSink alerts, FakeDiscord discord) = (f.App, f.Events, f.Hub, f.Alerts, f.Discord);
        hub.Settings.Update(s => s with { WatchedItems = DropWatchList.Add([], "Blinding Light of Destiny|Void Aura") });

        events.Drop(11, "Pixie Wing");
        events.Drop(77, "blinding light of destiny", 1);
        events.Raise(new GameEvent(GameEventKind.ItemObtained, DateTimeOffset.UtcNow) { Id = 78, Name = "Void Aura", Quantity = 3 });
        await Sandbox.Until(() => hub.Processed == 3);

        Assert.Equal(2, alerts.Posted.Count);
        DropAlert dropped = alerts.Posted[0];
        Assert.Equal((77, "blinding light of destiny", false), (dropped.ItemId, dropped.ItemName, dropped.Obtained));
        Assert.Equal("Watched item dropped", dropped.Title);
        Assert.False(dropped.ShowNameOnLockScreen);
        // What a secure lock screen shows names nothing.
        Assert.DoesNotContain("blinding", dropped.PublicText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hero", dropped.PublicText, StringComparison.OrdinalIgnoreCase);
        DropAlert obtained = alerts.Posted[1];
        Assert.True(obtained.Obtained);
        Assert.Equal("Void Aura x3", obtained.Text);

        IReadOnlyList<NotificationEntry> history = hub.History.Entries;
        Assert.Equal(3, history.Count);
        Assert.False(history.Single(e => e.Message.StartsWith("Pixie Wing", StringComparison.Ordinal)).Watched);
        Assert.True(history.Single(e => e.Message.StartsWith("blinding", StringComparison.Ordinal)).Watched);

        // Switched off: listed, not notified; names on the lock screen when allowed.
        hub.Settings.Update(s => s with { NotifyWatchedObtained = false, ShowNamesOnLockScreen = true });
        events.Raise(new GameEvent(GameEventKind.ItemObtained, DateTimeOffset.UtcNow) { Id = 78, Name = "Void Aura" });
        events.Drop(78, "Void Aura");
        await Sandbox.Until(() => hub.Processed == 5);
        Assert.Equal(3, alerts.Posted.Count);
        Assert.True(alerts.Posted[2].ShowNameOnLockScreen);
        Assert.Empty(discord.Requests); // no webhook configured
    }

    [Fact]
    public async Task TheHistoryIsWrittenToDiskAndReadBackByTheNextProcess()
    {
        FakeGameEvents events = new();
        await using TestApp app = new(scripts: new EventScriptHost(events));
        NotificationHub hub = Webhooks.Hub(app.Engine, app.Paths.AppDataDir, new FakeDiscord(), new RecordingDropAlertSink(), app.Store);
        events.Raise(new GameEvent(GameEventKind.MapChanged, DateTimeOffset.UtcNow) { Name = "battleon" });
        events.Raise(new GameEvent(GameEventKind.QuestCompleted, DateTimeOffset.UtcNow) { Id = 1000 });
        await Sandbox.Until(() => hub.Processed == 2);
        await hub.DisposeAsync();

        using NotificationHistory reread = new(Path.Combine(app.Paths.AppDataDir, "history.json"));
        Assert.Equal(["Completed quest #1000", "Entered battleon"], reread.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task EventHandlersReturnAtOnceWhateverTheHubIsDoing()
    {
        await using Fixture f = new();
        (TestApp app, FakeGameEvents events, NotificationHub hub, RecordingDropAlertSink alerts, FakeDiscord discord) = (f.App, f.Events, f.Hub, f.Alerts, f.Discord);
        hub.Settings.Update(s => s with { WatchedItems = ["Slow Item"] });
        ManualResetEventSlim release = new();
        alerts.AlertPosted += (_, _) => release.Wait(TimeSpan.FromSeconds(10)); // a notification that hangs

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
            events.Drop(500 + i, "Slow Item");
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"raising took {sw.ElapsedMilliseconds} ms");
        release.Set();
        await Sandbox.Until(() => hub.Processed == 200, 15000);
    }

    [Fact]
    public async Task JunkSettingsReachTheHostAndWatchedItemsAreKept()
    {
        await using Fixture f = new();
        (TestApp app, FakeGameEvents events, NotificationHub hub) = (f.App, f.Events, f.Hub);
        Assert.False(events.AutoReject);
        hub.Settings.Update(s => s with { AutoRejectJunk = true, WatchedItems = ["Pixie Wing"] });
        Assert.True(events.AutoReject);
        Assert.True(events.Keep!("pixie wing"));
        Assert.False(events.Keep!("Old Boots"));
    }

    [Fact]
    public async Task TheJunkPageMarksSellsAndSwitchesAutoReject()
    {
        await using Fixture f = new();
        (TestApp app, FakeGameEvents events, NotificationHub hub) = (f.App, f.Events, f.Hub);
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        await Sandbox.Until(() => app.Engine.GetWorldLists().Inventory.Count > 0);
        JunkItemsViewModel page = new(hub, app.Host);
        Assert.True(page.IsAvailable);
        page.IsActive = true;
        JunkItemEntry sword = page.FilteredItems.Single(e => e.Name == "Default Sword");
        Assert.False(sword.IsJunk);
        sword.IsJunk = true;
        Assert.Equal([1], events.Items.Select(j => j.Id));
        Assert.Equal(1, page.TotalJunk);

        page.JunkOnly = true;
        Assert.Equal(["Default Sword"], page.FilteredItems.Select(e => e.Name));
        page.SearchText = "boots";
        Assert.Empty(page.FilteredItems);
        page.SearchText = string.Empty;

        // Sell asks first (a second tap), then sells off the UI thread.
        await page.SellAllJunkCommand.ExecuteAsync(null);
        Assert.Equal(0, events.Sold);
        Assert.Equal("Tap again to sell", page.SellText);
        await page.SellAllJunkCommand.ExecuteAsync(null);
        Assert.Equal(1, events.Sold);

        page.AutoRejectJunk = true;
        Assert.True(hub.Settings.Current.AutoRejectJunk);
        Assert.True(events.AutoReject);

        page.UnmarkAllJunkCommand.Execute(null);
        Assert.Empty(events.Items);
    }

    [Fact]
    public async Task TheDropsTabPicksUpRejectsWatchesAndMarksJunk()
    {
        FakeGameEvents events = new();
        await using TestApp app = new(scripts: new EventScriptHost(events));
        await using NotificationHub hub = Webhooks.Hub(app.Engine, app.Paths.AppDataDir, new FakeDiscord(), new RecordingDropAlertSink());
        WorldViewModel world = new(app.Services with { Notifications = hub });
        Assert.True(world.CanHandleDrops);
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        await Sandbox.Until(() => app.Engine.Snapshot.IsOnline);
        world.Apply(app.Engine.Snapshot);

        // A drop offered by the server shows on the Drops tab.
        app.Aqw.Server.LastSession!.Json(new System.Text.Json.Nodes.JsonObject
        {
            ["cmd"] = "dropItem",
            ["items"] = new System.Text.Json.Nodes.JsonObject { ["11"] = Drop(11) },
        });
        await Sandbox.Until(() => app.Engine.GetWorldLists().Drops.Count == 1);
        world.IsActive = true;
        world.SelectTabCommand.Execute("Drops");
        DropListItem drop = Assert.Single(world.Drops);
        Assert.Equal(("Pixie Wing", false, false), (drop.Name, drop.Watched, drop.Junk));

        world.ToggleWatchCommand.Execute(drop);
        Assert.Equal(["Pixie Wing"], hub.Settings.Current.WatchedItems);
        Assert.True(world.Drops[0].Watched);
        world.ToggleJunkCommand.Execute(world.Drops[0]);
        Assert.True(events.IsJunk(11));
        Assert.True(world.Drops[0].Junk);
        world.ToggleWatchCommand.Execute(world.Drops[0]);
        Assert.Empty(hub.Settings.Current.WatchedItems);

        Assert.True(world.PickupDropCommand.CanExecute(world.Drops[0]));
        world.PickupDropCommand.Execute(world.Drops[0]);
        world.RejectDropCommand.Execute(world.Drops[0]);
        world.PickupAllDropsCommand.Execute(null);
        world.RejectAllDropsCommand.Execute(null);
        Assert.Equal(["pickup 11", "reject 11", "pickup all", "reject 11"], events.Calls);
    }
}
