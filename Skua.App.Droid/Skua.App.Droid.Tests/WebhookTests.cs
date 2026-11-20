using System.Net;
using System.Text.Json.Nodes;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.ViewModels;

namespace Skua.App.Droid.Tests;

public class WebhookUrlTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789-abcdefghijklmnopqrstuvwxyzABCD")]
    [InlineData("https://discordapp.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789-abcdefghijklmnopqrstuvwxyzABCD")]
    [InlineData("https://canary.discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789-abcdefghijklmnopqrstuvwxyzABCD")]
    [InlineData("https://ptb.discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789-abcdefghijklmnopqrstuvwxyzABCD")]
    [InlineData("https://canary.discordapp.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789-abcdefghijklmnopqrstuvwxyzABCD")]
    [InlineData("https://ptb.discordapp.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789-abcdefghijklmnopqrstuvwxyzABCD/")]
    [InlineData("https://discord.com/api/v10/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789-abcdefghijklmnopqrstuvwxyzABCD")]
    [InlineData("  https://DISCORD.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789-abcdefghijklmnopqrstuvwxyzABCD?thread_id=987654321098765432  ")]
    public void DiscordWebhooksAreAccepted(string url)
    {
        Assert.True(DiscordWebhookUrl.TryParse(url, out DiscordWebhookUrl? parsed, out string error), error);
        Assert.Equal(Uri.UriSchemeHttps, parsed!.Uri.Scheme);
        Assert.Contains(parsed.Uri.Host, DiscordWebhookUrl.AllowedHosts);
        Assert.Equal("123456789012345678", parsed.WebhookId);
        Assert.StartsWith("/api/webhooks/123456789012345678/", parsed.Uri.AbsolutePath);
        // The URL is a credential: its text form shows nothing usable.
        Assert.DoesNotContain(parsed.Token, parsed.ToString());
        Assert.DoesNotContain(parsed.Token, parsed.Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("http://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("ftp://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://example.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://discord.com.evil.example/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://evildiscord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://media.discordapp.net/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://www.discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://user:pw@discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://evil.example@discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://discord.com:8443/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789#x")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789?wait=true")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789?thread_id=1&x=2")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678")]
    [InlineData("https://discord.com/api/webhooks/abc/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789/extra")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/../../users/@me/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/AbCdEf%2FGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://discord.com\\api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/AbCdEf GhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://dіscord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    [InlineData("https://127.0.0.1/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789")]
    public void EverythingElseIsRefusedWithoutEchoingTheInput(string url)
    {
        Assert.False(DiscordWebhookUrl.TryParse(url, out DiscordWebhookUrl? parsed, out string error));
        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("AbCdEf", error);
    }

    [Fact]
    public void TheVaultStoresTheUrlOnlyInTheSecretStore()
    {
        MemorySecretStore store = new();
        WebhookUrlVault vault = new(store);
        Assert.False(vault.HasUrl);
        vault.Save(Webhooks.Parse());
        Assert.True(vault.HasUrl);
        Assert.Equal([WebhookUrlVault.EntryName], store.Entries.Keys);
        Assert.Equal(Webhooks.Url, vault.Load(out SecretStatus status)!.Uri.AbsoluteUri);
        Assert.Equal(SecretStatus.Found, status);

        // A transient Keystore failure keeps it; a stored value that is no longer valid is deleted.
        store.Transient = true;
        Assert.Null(vault.Load(out status));
        Assert.Equal(SecretStatus.Unavailable, status);
        Assert.True(vault.HasUrl);
        store.Transient = false;
        store.Entries[WebhookUrlVault.EntryName] = [1, 0, 3, (byte)'b', (byte)'a', (byte)'d'];
        Assert.Null(vault.Load(out _));
        Assert.False(vault.HasUrl);

        Assert.Throws<InvalidOperationException>(() => new WebhookUrlVault(new UnavailableSecretStore()).Save(Webhooks.Parse()));
    }
}

public class WebhookPayloadTests
{
    [Fact]
    public void CardsUseTheDesktopTitlesAndPingNobody()
    {
        JsonObject started = WebhookPayloads.ScriptStarted("VibeSkua Bot", "Farm_Gold");
        JsonObject embed = (JsonObject)started["embeds"]![0]!;
        Assert.Equal("Script Started", (string?)embed["title"]);
        Assert.Equal(WebhookPayloads.Green, (int)embed["color"]!);
        Assert.Equal("**VibeSkua Bot** has begun execution of **Farm\\_Gold**.", (string?)embed["description"]);
        Assert.Empty((JsonArray)started["allowed_mentions"]!["parse"]!);

        JsonObject concluded = WebhookPayloads.SessionConcluded("Hero", "Farm", new TimeSpan(1, 2, 3, 4), 1500, new Skua.App.Droid.Core.Engine.ScriptCounters(10, 0, 2, 5, 0), "battleon");
        JsonObject c = (JsonObject)concluded["embeds"]![0]!;
        Assert.Equal("Farming Session Concluded", (string?)c["title"]);
        JsonArray fields = (JsonArray)c["fields"]!;
        Assert.Contains(fields, f => (string?)f!["name"] == "Time Elapsed" && (string?)f["value"] == "26:03:04");
        Assert.Contains(fields, f => (string?)f!["name"] == "Gold Earned" && (string?)f["value"] == "1,500");
        Assert.Contains(fields, f => (string?)f!["name"] == "Kills" && (string?)f["value"] == "10");
        Assert.Contains(fields, f => (string?)f!["name"] == "Map" && (string?)f["value"] == "battleon");

        Assert.Equal("Scheduler Paused", (string?)WebhookPayloads.SchedulerPaused()["embeds"]![0]!["title"]);
        Assert.Equal("Kicked & Relogging", (string?)WebhookPayloads.Relogin("x", kicked: true)["embeds"]![0]!["title"]);
        Assert.Equal("Rare Drop!", (string?)WebhookPayloads.DropAlert("x", "Void Aura", 2, true, null)["embeds"]![0]!["title"]);
    }

    [Fact]
    public void NamesCannotFormatLinkOrMention()
    {
        string json = WebhookPayloads.DropAlert("@everyone", "[Free](https://evil.example) **x** @here", 1, false, "map`")
            .ToJsonString();
        Assert.DoesNotContain("[Free](https://evil.example)", json);
        Assert.Contains("\\\\@everyone", json);
        Assert.Contains("https://aqwwiki.wikidot.com/", json);
        string longName = new('A', 500);
        string desc = (string)WebhookPayloads.ScriptStarted(longName, longName)["embeds"]![0]!["description"]!;
        Assert.True(desc.Length < 300);
    }
}

/// <summary>The queue: rate limits, retries, never blocking the caller.</summary>
public class WebhookSenderTests
{
    private static readonly JsonObject Card = WebhookPayloads.Test();

    [Fact]
    public async Task A429IsRetriedAfterDiscordsRetryAfter()
    {
        FakeDiscord discord = new()
        {
            Respond = n => n switch
            {
                1 => FakeDiscord.TooManyRequests("{\"message\":\"You are being rate limited.\",\"retry_after\":1.25,\"global\":false}"),
                2 => FakeDiscord.TooManyRequests(retryAfterHeader: "3"),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            },
        };
        List<TimeSpan> waits = new();
        await using DiscordWebhookSender sender = Webhooks.Sender(discord, waits);
        sender.Url = Webhooks.Parse();

        WebhookResult result = await sender.SendAsync(Card);

        Assert.Equal(WebhookOutcome.Sent, result.Outcome);
        Assert.Equal(3, discord.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1.25), TimeSpan.FromSeconds(3)], waits);
        Assert.All(discord.Requests, r => Assert.Equal(Webhooks.Url, r.Uri.AbsoluteUri));
        Assert.Equal("Discord Webhook Test Successful!", (string?)JsonNode.Parse(discord.Requests[0].Body)!["embeds"]![0]!["title"]);
    }

    [Fact]
    public async Task ARealRetryAfterIsWaitedFor()
    {
        FakeDiscord discord = new()
        {
            Respond = n => n == 1 ? FakeDiscord.TooManyRequests("{\"retry_after\":0.3}") : new HttpResponseMessage(HttpStatusCode.NoContent),
        };
        await using DiscordWebhookSender sender = new(discord, new WebhookSenderOptions { MinGap = TimeSpan.Zero });
        sender.Url = Webhooks.Parse();
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        WebhookResult result = await sender.SendAsync(Card);
        Assert.Equal(WebhookOutcome.Sent, result.Outcome);
        Assert.True(sw.ElapsedMilliseconds >= 280, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task RateLimitsAreGivenUpOnAfterTheLastAttemptAndLongWaitsAreCapped()
    {
        FakeDiscord discord = new() { Respond = _ => FakeDiscord.TooManyRequests("{\"retry_after\":3600}") };
        List<TimeSpan> waits = new();
        await using DiscordWebhookSender sender = Webhooks.Sender(discord, waits, attempts: 3);
        sender.Url = Webhooks.Parse();
        WebhookResult result = await sender.SendAsync(Card);
        Assert.Equal(WebhookOutcome.Failed, result.Outcome);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal(3, discord.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)], waits);
        Assert.DoesNotContain(Webhooks.Token, result.Message);
    }

    [Fact]
    public async Task ADeletedWebhookIsNotRetriedAndServerErrorsAre()
    {
        FakeDiscord discord = new() { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        List<string> log = new();
        await using DiscordWebhookSender sender = new(discord, new WebhookSenderOptions { Delay = (_, _) => Task.CompletedTask }, log.Add);
        sender.Url = Webhooks.Parse();
        WebhookResult result = await sender.SendAsync(Card);
        Assert.Equal((WebhookOutcome.Failed, 404), (result.Outcome, result.StatusCode));
        Assert.Single(discord.Requests);

        discord.Respond = n => n < 4 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : new HttpResponseMessage(HttpStatusCode.NoContent);
        Assert.Equal(WebhookOutcome.Sent, (await sender.SendAsync(Card)).Outcome);
        Assert.Equal(4, discord.Requests.Count);
        Assert.All(log, l => Assert.DoesNotContain(Webhooks.Token, l));
        Assert.All(log, l => Assert.DoesNotContain("discord.com/api", l));
    }

    [Fact]
    public async Task AnEmptyBucketDelaysTheNextMessage()
    {
        FakeDiscord discord = new()
        {
            Respond = n =>
            {
                HttpResponseMessage r = new(HttpStatusCode.NoContent);
                if (n == 1)
                {
                    r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                    r.Headers.TryAddWithoutValidation("X-RateLimit-Reset-After", "2.5");
                }
                return r;
            },
        };
        List<TimeSpan> waits = new();
        await using DiscordWebhookSender sender = Webhooks.Sender(discord, waits);
        sender.Url = Webhooks.Parse();
        await sender.SendAsync(Card);
        await sender.SendAsync(Card);
        await sender.SendAsync(Card);
        // After the first: the bucket's reset; after the second: the minimum gap between messages.
        Assert.Equal([TimeSpan.FromSeconds(2.5), TimeSpan.FromMilliseconds(500)], waits);
    }

    [Fact]
    public async Task EnqueueNeverWaitsForDiscordAndTheOldestIsDroppedWhenFull()
    {
        FakeDiscord discord = new() { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using DiscordWebhookSender sender = Webhooks.Sender(discord, capacity: 5);
        Assert.False(sender.Enqueue(Card)); // no URL: nothing queued
        sender.Url = Webhooks.Parse();

        Task<WebhookResult> first = sender.SendAsync(Card);
        await Sandbox.Until(() => discord.Requests.Count == 1); // in flight, Discord not answering
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        List<Task<WebhookResult>> queued = new();
        for (int i = 0; i < 20; i++)
            queued.Add(sender.SendAsync(Card));
        for (int i = 0; i < 100; i++)
            Assert.True(sender.Enqueue(Card));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"enqueueing took {sw.ElapsedMilliseconds} ms");
        Assert.All(queued, t => Assert.Equal(WebhookOutcome.Dropped, t.Result.Outcome)); // pushed out by newer ones
        Assert.False(first.IsCompleted);

        discord.Gate.SetResult();
        Assert.Equal(WebhookOutcome.Sent, (await first).Outcome);
        await Sandbox.Until(() => sender.Pending == 0);
        Assert.Equal(1 + 5, discord.Requests.Count);
    }

    [Fact]
    public async Task WithoutAUrlNothingIsSent()
    {
        FakeDiscord discord = new();
        await using DiscordWebhookSender sender = Webhooks.Sender(discord);
        Assert.Equal(WebhookOutcome.NoUrl, (await sender.SendAsync(Card)).Outcome);
        Assert.Empty(discord.Requests);
    }
}

/// <summary>The hub's webhook: which events become cards, and that no secret can reach one.</summary>
public class WebhookHubTests
{
    [Fact]
    public async Task CardsCarryNoPasswordTokenAccountOrEmail()
    {
        FakeGameEvents events = new();
        await using TestApp app = new(scripts: new EventScriptHost(events));
        FakeDiscord discord = new();
        RecordingDropAlertSink alerts = new();
        SecretScrubber scrubber = new();
        await using NotificationHub hub = Webhooks.Hub(app.Engine, app.Paths.AppDataDir, discord, alerts, app.Store, scrubber);
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: true);
        await Sandbox.Until(() => app.Engine.Snapshot.Character is not null);

        Assert.True(hub.SetWebhookUrl(Webhooks.Url, out string error), error);
        Assert.Equal("<redacted>", scrubber.Scrub(Webhooks.Url)); // kept out of logs and crash reports
        hub.Settings.Update(s => s with
        {
            WebhookEnabled = true,
            WebhookNotifyRelogged = true,
            WatchedItems = ["Void Aura"],
        });

        void Session(string script)
        {
            events.Raise(new GameEvent(GameEventKind.ScriptStarted, DateTimeOffset.UtcNow) { Name = script });
            events.Drop(78, "Void Aura");
            events.Raise(new GameEvent(GameEventKind.Relogin, DateTimeOffset.UtcNow) { Flag = true });
            // The exception text could say anything (here: the password): it never leaves the device.
            events.Raise(new GameEvent(GameEventKind.ScriptError, DateTimeOffset.UtcNow) { Name = script, Detail = $"login failed for hero@example.com with {FakeAqw.Password}" });
            events.Raise(new GameEvent(GameEventKind.ScriptStopped, DateTimeOffset.UtcNow.AddMinutes(5)) { Name = script });
        }

        Session("Farm");
        await Sandbox.Until(() => discord.Requests.Count == 5, 10000);
        hub.Settings.Update(s => s with { WebhookIncludeCharacterName = true });
        Session("Farm2");
        await Sandbox.Until(() => hub.Processed == 10);
        hub.NotifySchedulerPaused();
        // The drop alert of the same item is held back for 30 s; the other four and the scheduler card are sent.
        await Sandbox.Until(() => discord.Requests.Count == 10, 10000);

        List<string> bodies = discord.Requests.Select(r => r.Body).ToList();
        string[] titles = bodies.Select(b => (string)JsonNode.Parse(b)!["embeds"]![0]!["title"]!).ToArray();
        Assert.Equal(["Script Started", "Rare Drop!", "Kicked & Relogging", "Script Error", "Farming Session Concluded",
            "Script Started", "Kicked & Relogging", "Script Error", "Farming Session Concluded", "Scheduler Paused"], titles);

        foreach (string body in bodies)
        {
            Assert.DoesNotContain(FakeAqw.Password, body);
            Assert.DoesNotContain("fake-token-", body);
            Assert.DoesNotContain("@example.com", body);
            Assert.DoesNotContain(Webhooks.Token, body);
        }
        // The character name only once allowed.
        Assert.All(bodies.Take(5), b => Assert.DoesNotContain("Hero", b, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("VibeSkua Bot", bodies[0]);
        Assert.Contains(bodies.Skip(5).Take(4), b => b.Contains("**Hero**", StringComparison.OrdinalIgnoreCase));
        JsonArray fields = (JsonArray)JsonNode.Parse(bodies[4])!["embeds"]![0]!["fields"]!;
        Assert.Contains(fields, f => (string?)f!["name"] == "Time Elapsed" && (string?)f["value"] == "00:05:00");

        // The URL is not in any settings file and is gone once deleted.
        foreach (string file in Directory.EnumerateFiles(app.Paths.FilesDir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(Webhooks.Token, File.ReadAllText(file));
        hub.ForgetWebhook();
        Assert.False(hub.WebhookConfigured);
        Assert.False(app.Store.Entries.ContainsKey(WebhookUrlVault.EntryName));
    }

    [Fact]
    public async Task EventsThatAreSwitchedOffSendNothing()
    {
        FakeGameEvents events = new();
        await using TestApp app = new(scripts: new EventScriptHost(events));
        FakeDiscord discord = new();
        await using NotificationHub hub = Webhooks.Hub(app.Engine, app.Paths.AppDataDir, discord, new RecordingDropAlertSink(), app.Store);
        Assert.True(hub.SetWebhookUrl(Webhooks.Url, out _));
        events.Raise(new GameEvent(GameEventKind.ScriptStarted, DateTimeOffset.UtcNow) { Name = "A" }); // webhook off
        hub.Settings.Update(s => s with { WebhookEnabled = true, WebhookNotifyStarted = false, WebhookNotifyRelogged = false });
        events.Raise(new GameEvent(GameEventKind.ScriptStarted, DateTimeOffset.UtcNow) { Name = "A" });
        events.Raise(new GameEvent(GameEventKind.Relogin, DateTimeOffset.UtcNow));
        events.Raise(new GameEvent(GameEventKind.MapChanged, DateTimeOffset.UtcNow) { Name = "battleon" });
        await Sandbox.Until(() => hub.Processed == 4);
        Assert.Empty(discord.Requests);
    }

    [Fact]
    public async Task TheSettingsPageStoresValidatesAndTests()
    {
        await using TestApp app = new();
        FakeDiscord discord = new();
        await using NotificationHub hub = Webhooks.Hub(app.Engine, app.Paths.AppDataDir, discord, new RecordingDropAlertSink(), app.Store);
        DiscordWebhookViewModel page = new(hub);
        Assert.False(page.HasUrl);
        Assert.False(page.SendTestCommand.CanExecute(null));

        page.UrlInput = "https://example.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789";
        page.SaveUrlCommand.Execute(null);
        Assert.True(page.IsError);
        Assert.Equal(string.Empty, page.UrlInput); // never left in the field
        Assert.False(page.HasUrl);

        page.UrlInput = Webhooks.Url;
        page.SaveUrlCommand.Execute(null);
        Assert.False(page.IsError, page.Message);
        Assert.True(page.HasUrl);
        Assert.True(page.Enabled);
        Assert.Equal(string.Empty, page.UrlInput);
        Assert.DoesNotContain(Webhooks.Token, page.StatusText);

        await page.SendTestCommand.ExecuteAsync(null);
        Assert.Contains("sent", page.Message);
        Assert.Single(discord.Requests);

        page.NotifyRelogged = true;
        page.IncludeCharacterName = true;
        Assert.True(hub.Settings.Current.WebhookNotifyRelogged);
        Assert.True(hub.Settings.Current.WebhookIncludeCharacterName);

        // A new process reads the stored URL back from the vault.
        await using NotificationHub next = Webhooks.Hub(app.Engine, app.Paths.AppDataDir, discord, new RecordingDropAlertSink(), app.Store);
        Assert.True(next.WebhookConfigured);
        page.ForgetUrlCommand.Execute(null);
        Assert.False(page.HasUrl);
        Assert.False(hub.WebhookConfigured);
    }
}
