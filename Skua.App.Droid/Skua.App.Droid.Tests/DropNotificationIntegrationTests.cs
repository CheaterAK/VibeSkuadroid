using System.Text.Json.Nodes;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Scripting;
using Skua.Core.Headless;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Models;

namespace Skua.App.Droid.Tests;

public partial class SessionIntegrationTests
{
    /// <summary>
    /// Drop notifications and junk on the real Skua host over the fake server: Skua's drop messages reach the hub, a
    /// watched drop posts an alert and is picked up (obtained), a junk drop is rejected by Skua's own RejectExcept
    /// (gone from Skua's drop list and the World page), a junk drop the script asked for is kept, and the junk list
    /// is Skua's JunkItems.json.
    /// </summary>
    [Fact]
    public async Task WatchedDropsNotifyAndJunkDropsAreRejectedThroughSkua()
    {
        await using FakeAqw aqw = new();
        SkuaScriptHostAdapter host = new(null, new ScriptAssemblyStore(), ClientFileSources.SkuaScriptsDIR, o =>
        {
            o.WebApi = new AqwWebApi(new AqwWebApiOptions { BaseUri = new Uri("https://example.test/game/") }, aqw.Http);
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
            PublishInterval = TimeSpan.FromMilliseconds(20),
            MemorySampleInterval = TimeSpan.FromMilliseconds(100),
        }, host);
        RecordingDropAlertSink alerts = new();
        string dir = Sandbox.NewDir();
        NotificationHub hub = Webhooks.Hub(engine, dir, new FakeDiscord(), alerts);
        IGameEvents game = Assert.IsAssignableFrom<IGameEvents>(engine.GameEvents);
        string junkFile = ClientFileSources.SkuaJunkItemsFile;
        string? junkBefore = File.Exists(junkFile) ? File.ReadAllText(junkFile) : null;
        try
        {
            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => engine.Snapshot.Character is not null && host.Skua!.Bot.Map.Loaded);
            Skua.Core.Headless.Tests.Fakes.FakeAqwServer.Session server = aqw.Server.LastSession!;
            void Offer(int id) => server.Json(new JsonObject
            {
                ["cmd"] = "dropItem",
                ["items"] = new JsonObject { [id.ToString()] = NotificationHubTests.Drop(id) },
            });
            bool Offered(int id) => engine.GetWorldLists().Drops.Any(d => d.ItemId == id);
            bool InSkua(int id) => host.Skua!.Bot.Drops.CurrentDropInfos.Any(d => d.ID == id);

            // Junk: Old Boots (30) and Pixie Wing (11), stored in Skua's JunkItems.json; auto-reject on.
            game.Junk.Set([new JunkItem(30, "Old Boots", "Misc"), new JunkItem(11, "Pixie Wing", "Item")]);
            Assert.Contains("Old Boots", File.ReadAllText(junkFile));
            game.Junk.Reload();
            Assert.True(game.Junk.IsJunk(30));
            hub.Settings.Update(s => s with { AutoRejectJunk = true, WatchedItems = ["Pixie Crown"] });

            // A watched drop: alert, listed, kept.
            Offer(12);
            await Sandbox.Until(() => alerts.Posted.Count == 1 && InSkua(12));
            Assert.Equal(("Pixie Crown", false), (alerts.Posted[0].ItemName, alerts.Posted[0].Obtained));
            Assert.True(Offered(12));

            // A junk drop: rejected by Skua (its list and the World page), recorded.
            Offer(30);
            await Sandbox.Until(() => hub.History.Entries.Any(e => e.Message == "Old Boots (rejected: junk)"), 10000);
            await Sandbox.Until(() => !Offered(30) && !InSkua(30));
            Assert.True(Offered(12) && InSkua(12)); // the other drop stays

            // A junk drop the script asked for is kept.
            host.Skua!.Bot.Drops.Add("Pixie Wing");
            Offer(11);
            await Sandbox.Until(() => InSkua(11) && hub.History.Entries.Any(e => e.Message == "Pixie Wing (dropped)"));
            await Task.Delay(500);
            Assert.True(Offered(11) && InSkua(11));
            host.Skua.Bot.Drops.Remove("Pixie Wing");

            // Picking the watched drop up: obtained alert (the same notification, replaced).
            server.PendingDrops.Add(12);
            game.PickupDrops([12]);
            await Sandbox.Until(() => alerts.Posted.Count == 2, 10000);
            Assert.True(alerts.Posted[1].Obtained);
            Assert.Equal(12, alerts.Posted[1].ItemId);
            await Sandbox.Until(() => !Offered(12));

            // Reject from the World page's action.
            game.RejectDrops([11]);
            await Sandbox.Until(() => !Offered(11) && !InSkua(11));

            Assert.Contains(hub.History.Entries, e => e.Message == "Pixie Crown (picked up)" && e.Watched);
        }
        finally
        {
            await hub.DisposeAsync();
            await engine.DisposeAsync();
            if (junkBefore is null)
                File.Delete(junkFile);
            else
                File.WriteAllText(junkFile, junkBefore);
        }
    }
}
