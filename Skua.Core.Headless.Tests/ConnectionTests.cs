using Skua.Core.Headless.Commands;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Headless.World;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Tests;

/// <summary>End-to-end tests over a real loopback socket against <see cref="FakeAqwServer"/>. No external network.</summary>
public class ConnectionTests : IAsyncLifetime
{
    private readonly FakeAqwServer _fake = new();
    private readonly List<AqwConnection> _connections = new();

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (AqwConnection c in _connections)
            await c.DisposeAsync();
        await _fake.DisposeAsync();
    }

    internal static AqwConnectionOptions Fast()
    {
        return new AqwConnectionOptions
        {
            SendGap = TimeSpan.FromMilliseconds(15),
            SlowSendGap = TimeSpan.FromMilliseconds(60),
            SlowDownHold = TimeSpan.FromSeconds(30),
            LoginTimeout = TimeSpan.FromSeconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            LivenessCheckInterval = TimeSpan.FromMilliseconds(50),
            AwaitReplyTimeout = TimeSpan.FromMilliseconds(400),
            ProbeTimeout = TimeSpan.FromMilliseconds(400),
            IdleProbeAfter = TimeSpan.Zero,
        };
    }

    private AqwConnection New(AqwConnectionOptions? options = null)
    {
        AqwConnection c = new(options ?? Fast());
        _connections.Add(c);
        return c;
    }

    private async Task<AqwConnection> LoggedIn(AqwConnectionOptions? options = null, string version = "5.32")
    {
        AqwConnection c = New(options);
        await c.ConnectAsync(_fake.Server, "Hero", FakeAqwServer.Token, version);
        return c;
    }

    internal static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task LoginRunsTheHandshakeAndLoadsTheCharacter()
    {
        AqwConnection c = await LoggedIn();
        Assert.True(c.IsReady);
        WorldModel w = c.World;
        PlayerInfo me = w.GetPlayer()!;
        Assert.Equal("Hero", me.Username);
        Assert.Equal(_fake.LastSession!.Uid, me.UserId);
        Assert.Equal(30, me.Level);
        Assert.Equal(1000, me.Hp);
        Assert.Equal(3, w.GetInventory().Count);
        Assert.Equal(5, w.GetSkills().Count);
        Assert.Equal(3, w.GetMonsters().Count);
        Assert.Equal("testmap", w.GetArea().MapName);
        Assert.Equal("true", w.GetGameObject("sfc.isConnected"));

        IReadOnlyList<string> commands = _fake.LastSession.Commands;
        Assert.Equal(new[] { "firstJoin", "moveToCell", "retrieveUserData", "retrieveInventory" }, commands.Take(4));
        string login = _fake.LastSession.Received.Select(r => r.Text).Single(t => t.Contains("action='login'"));
        Assert.Contains("SPIDER#0001~hero~5.32", login); // the game lowercases the name (Game.onLoginComplete)
        Assert.Contains("z='zone_master'", login);
        Assert.Contains("<ver v='157' />", _fake.LastSession.Received.First().Text);
        Assert.Equal(new[] { "157" }, _fake.ApiVersions.ToArray());

        // moveToArea, loadInventoryBig and sAct are over 400 chars, so the fake sent them compressed.
        Assert.True(c.GetStatistics().CompressedIn >= 2);
        Assert.Equal(0, c.GetStatistics().UndecodableIn);
    }

    [Fact]
    public async Task TheFakeRefusesWhatTheRealClientWouldNotSend()
    {
        // The connection lowercases the name as the game does (the fake refuses any other case); a superseded token and
        // a verChk version the fake is told to require are refused.
        AqwConnection mixed = New();
        string fresh = _fake.IssueToken("Hero");
        _fake.RequiredApiVersion = "157";
        await mixed.ConnectAsync(_fake.Server, "Hero", fresh, "5.32");
        Assert.Equal("hero~5.32", _fake.LoginNicks.Last());

        string superseded = fresh;
        _fake.IssueToken("Hero");
        AqwConnection stale = New();
        AqwLoginException e = await Assert.ThrowsAsync<AqwLoginException>(() => stale.ConnectAsync(_fake.Server, "Hero", superseded, "5.32"));
        Assert.Equal(LoginFailureKind.SocketRejected, e.Kind);
        Assert.Contains(FakeAqwServer.CharacterDataRefusal, e.Message);

        AqwConnection oldApi = New(WithApi(Fast(), "166"));
        AqwLoginException api = await Assert.ThrowsAsync<AqwLoginException>(() => oldApi.ConnectAsync(_fake.Server, "Hero", superseded, "5.32"));
        Assert.Equal(LoginFailureKind.ApiVersionRejected, api.Kind);
    }

    private static AqwConnectionOptions WithApi(AqwConnectionOptions options, string api)
    {
        options.ApiVersion = api;
        return options;
    }

    [Fact]
    public async Task TheTokenNeverAppearsInSentEventsOrLogs()
    {
        AqwConnection c = New();
        List<string> seen = new();
        c.PacketSent += (_, e) => seen.Add(e.Text);
        c.Log += (_, e) => seen.Add(e.Message);
        await c.ConnectAsync(_fake.Server, "Hero", FakeAqwServer.Token, "5.32");
        Assert.Contains(seen, s => s.Contains("<pword><![CDATA[<token>]]>"));
        Assert.DoesNotContain(seen, s => s.Contains(FakeAqwServer.Token));
    }

    [Fact]
    public async Task JoinAndJump()
    {
        AqwConnection c = await LoggedIn();
        GameCommands cmd = new(c);
        AreaInfo shop = await cmd.JoinMapAsync("shopmap");
        Assert.Equal("shopmap", shop.MapName);
        Assert.Empty(c.World.GetMonsters());

        AreaInfo test = await cmd.JoinMapAsync("testmap", "r2", "Left");
        Assert.Equal("testmap", test.MapName);
        Assert.True(test.RoomId > shop.RoomId);
        await Until(() => _fake.LastSession!.Cell == "r2" && _fake.LastSession.Pad == "Left");
        Assert.Equal("r2", c.World.GetArea().Cell);

        Assert.True(await cmd.JumpAsync("r3", "Right"));
        await Until(() => _fake.LastSession!.Cell == "r3");
        Assert.Equal("r3", c.World.GetPlayer()!.Cell);

        JoinFailedException e = await Assert.ThrowsAsync<JoinFailedException>(() => cmd.JoinMapAsync("nowhere"));
        Assert.Contains("not available", e.Message);
    }

    [Fact]
    public async Task FightCollectDropsAndCompleteAQuest()
    {
        AqwConnection c = await LoggedIn();
        GameCommands cmd = new(c);
        WorldModel w = c.World;

        QuestInfo quest = Assert.Single(await cmd.LoadQuestsAsync(new[] { 1000 }));
        Assert.Equal("Pixie Problem", quest.Name);
        Assert.True(await cmd.AcceptQuestAsync(1000));
        Assert.Equal("p", w.GetQuest(1000)!.Status);

        QuestCompletionResult early = await cmd.TryCompleteQuestAsync(1000);
        Assert.False(early.Success);

        await cmd.JumpAsync("r2", "Left");
        long goldBefore = w.GetPlayer()!.Gold;
        int kills = 0;
        Stopwatch sw = Stopwatch.StartNew();
        while ((w.GetTempInventory().FirstOrDefault(i => i.ItemId == 10)?.Quantity ?? 0) < 3)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "fight took too long");
            MonsterInfo? target = w.GetMonsters().FirstOrDefault(m => m.Cell == "r2" && m.IsAlive);
            if (target is null)
            {
                await Task.Delay(50); // wait for a respawn
                continue;
            }
            ActionResult result = await cmd.AttackMonsterAsync(target.MonMapId);
            Assert.NotNull(result.Reply);
            Assert.Equal("ct", result.Reply!.Command);
            if (!w.GetMonster(target.MonMapId)!.IsAlive)
                kills++;
        }

        Assert.Equal(3, kills);
        Assert.Equal(goldBefore + 15, w.GetPlayer()!.Gold);
        Assert.NotEmpty(w.GetRecentActionResults());
        Assert.True(w.GetPlayer()!.State is 1 or 2);

        DropInfo wing = Assert.Single(w.GetDrops());
        Assert.Equal(11, wing.ItemId);
        Assert.Equal(3, wing.Quantity);
        Assert.True(await cmd.GetDropAsync(11));
        Assert.Empty(w.GetDrops());
        Assert.Contains(w.GetInventory(), i => i.ItemId == 11 && i.Name == "Pixie Wing");
        Assert.False(await cmd.GetDropAsync(11)); // nothing left to pick up

        QuestCompletionResult done = await cmd.TryCompleteQuestAsync(1000);
        Assert.True(done.Success);
        await Until(() => w.GetInventory().Any(i => i.ItemId == 12));
        Assert.Empty(w.GetTempInventory());
        Assert.False(w.GetQuest(1000)!.Accepted);
        Assert.Equal(goldBefore + 115, w.GetPlayer()!.Gold);
    }

    [Fact]
    public async Task LoadBank()
    {
        AqwConnection c = await LoggedIn();
        IReadOnlyList<ItemInfo> bank = await new GameCommands(c).LoadBankAsync();
        Assert.Equal("Pixie Wing", Assert.Single(bank).Name);
        Assert.True(c.World.IsBankLoaded);
    }

    [Fact]
    public async Task StaleClientVersionIsRejectedWithTheRequiredVersion()
    {
        _fake.RequiredClientVersion = "5.40";
        AqwConnection c = New();
        DisconnectInfo? info = null;
        c.Disconnected += (_, e) => info = e;
        AqwLoginException e = await Assert.ThrowsAsync<AqwLoginException>(() => c.ConnectAsync(_fake.Server, "Hero", FakeAqwServer.Token, "5.32"));
        Assert.Equal(LoginFailureKind.ClientVersionRejected, e.Kind);
        Assert.Equal("5.40", e.RequiredClientVersion);
        Assert.Equal(ConnectionState.Closed, c.State);
        Assert.Equal(DisconnectReason.LoginFailed, info!.Reason);
        Assert.True(info.ShouldReconnect);
    }

    [Fact]
    public async Task BadTokenIsRejected()
    {
        AqwConnection c = New();
        AqwLoginException e = await Assert.ThrowsAsync<AqwLoginException>(() => c.ConnectAsync(_fake.Server, "Hero", "wrong", "5.32"));
        Assert.Equal(LoginFailureKind.SocketRejected, e.Kind);
        Assert.Contains("Invalid login token", e.Message);
        Assert.False(c.DisconnectInfo!.ShouldReconnect);
    }

    [Fact]
    public async Task UnreachableServerFailsAsConnectionLost()
    {
        GameServer nowhere = _fake.Server with { Port = 1 };
        AqwConnection c = New();
        AqwLoginException e = await Assert.ThrowsAsync<AqwLoginException>(() => c.ConnectAsync(nowhere, "Hero", FakeAqwServer.Token, "5.32"));
        Assert.Equal(LoginFailureKind.ConnectionLost, e.Kind);
        Assert.Equal(DisconnectReason.TransportError, c.DisconnectInfo!.Reason);
    }

    [Fact]
    public async Task CancellationDuringLoginClosesTheConnection()
    {
        _fake.Silent = true; // never answers verChk
        AqwConnection c = New();
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.ConnectAsync(_fake.Server, "Hero", FakeAqwServer.Token, "5.32", cts.Token));
        Assert.Equal(DisconnectReason.Cancelled, c.DisconnectInfo!.Reason);
        Assert.True(c.DisconnectInfo.Intentional);
    }

    [Fact]
    public async Task LoginTimeoutIsReported()
    {
        _fake.Silent = true;
        AqwConnectionOptions options = Fast();
        options.LoginTimeout = TimeSpan.FromMilliseconds(400);
        AqwConnection c = New(options);
        AqwLoginException e = await Assert.ThrowsAsync<AqwLoginException>(() => c.ConnectAsync(_fake.Server, "Hero", FakeAqwServer.Token, "5.32"));
        Assert.Equal(LoginFailureKind.Timeout, e.Kind);
        Assert.Equal(DisconnectReason.LoginTimeout, c.DisconnectInfo!.Reason);
    }

    [Fact]
    public async Task SilentServerIsDetectedByTheLivenessProbe()
    {
        AqwConnection c = await LoggedIn();
        _fake.Silent = true;
        Assert.True(await c.SendAsync(Packets.LoadBank(c.World.RoomId)));
        DisconnectInfo info = await c.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DisconnectReason.LivenessTimeout, info.Reason);
        Assert.True(info.ShouldReconnect);
        Assert.True(c.GetStatistics().Probes >= 1);
        Assert.Contains(_fake.LastSession!.Commands, x => x == "retrieveUserData");
    }

    [Fact]
    public async Task ExplicitProbe()
    {
        AqwConnection c = await LoggedIn();
        Assert.True(await c.ProbeAsync(TimeSpan.FromSeconds(2)));
        _fake.Silent = true;
        Assert.False(await c.ProbeAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task RemoteCloseIsSignalledAndFailsWaiters()
    {
        AqwConnection c = await LoggedIn();
        Task<InboundPacket?> waiting = c.WaitForAsync(p => p.Command == "neverComes", TimeSpan.FromSeconds(10));
        _fake.LastSession!.Close();
        DisconnectInfo info = await c.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(info.Reason, new[] { DisconnectReason.RemoteClosed, DisconnectReason.TransportError });
        Assert.True(info.ShouldReconnect);
        Assert.False(info.Intentional);
        await Assert.ThrowsAsync<ConnectionClosedException>(() => waiting);
        Assert.False(await c.SendAsync("%xt%zm%x%1%"));
        Assert.Equal("false", c.World.GetGameObject("sfc.isConnected"));
    }

    [Fact]
    public async Task DisconnectLogsOutAndIsIntentional()
    {
        AqwConnection c = await LoggedIn();
        int raised = 0;
        c.Disconnected += (_, _) => Interlocked.Increment(ref raised);
        await c.DisconnectAsync();
        Assert.Equal(DisconnectReason.Requested, c.DisconnectInfo!.Reason);
        Assert.False(c.DisconnectInfo.ShouldReconnect);
        await Until(() => _fake.LastSession!.Received.Any(r => r.Text == "%xt%zm%cmd%1%logout%"));
        await c.DisconnectAsync();
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task OutgoingGamePacketsArePaced()
    {
        AqwConnectionOptions options = Fast();
        options.SendGap = TimeSpan.FromMilliseconds(100);
        AqwConnection c = await LoggedIn(options);
        _fake.SlowDownBelow = TimeSpan.FromMilliseconds(90);
        int before = _fake.LastSession!.Received.Count;
        Task<bool>[] sends = Enumerable.Range(0, 5).Select(i => c.SendAsync($"%xt%zm%noop{i}%1%")).ToArray();
        Assert.All(await Task.WhenAll(sends), Assert.True);
        await Until(() => _fake.LastSession.Received.Count >= before + 5);
        long[] stamps = _fake.LastSession.Received.Skip(before).Select(r => r.Timestamp).ToArray();
        for (int i = 1; i < stamps.Length; i++)
            Assert.True(Stopwatch.GetElapsedTime(stamps[i - 1], stamps[i]) >= TimeSpan.FromMilliseconds(90), $"gap {i} too short");
        await Task.Delay(100);
        Assert.Equal(0, c.GetStatistics().SlowDownWarnings);
    }

    [Fact]
    public async Task SlowDownWarningWidensTheGap()
    {
        AqwConnectionOptions options = Fast();
        options.SendGap = TimeSpan.FromMilliseconds(5);
        options.SlowSendGap = TimeSpan.FromMilliseconds(120);
        AqwConnection c = await LoggedIn(options);
        _fake.SlowDownBelow = TimeSpan.FromMilliseconds(60);
        await c.SendAsync("%xt%zm%noopA%1%");
        await c.SendAsync("%xt%zm%noopB%1%");
        await Until(() => c.GetStatistics().SlowDownWarnings >= 1);
        int before = _fake.LastSession!.Received.Count;
        await Task.WhenAll(Enumerable.Range(0, 3).Select(i => c.SendAsync($"%xt%zm%after{i}%1%")));
        await Until(() => _fake.LastSession.Received.Count >= before + 3);
        long[] stamps = _fake.LastSession.Received.Skip(before).Select(r => r.Timestamp).ToArray();
        for (int i = 1; i < stamps.Length; i++)
            Assert.True(Stopwatch.GetElapsedTime(stamps[i - 1], stamps[i]) >= TimeSpan.FromMilliseconds(110));
        Assert.Contains(c.World.GetRecentMessages(), m => m.Text.Contains("slow down", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AfkFlagIsClearedWhenTheServerSetsIt()
    {
        _fake.MarkAfkAfterLoad = true;
        AqwConnection c = await LoggedIn();
        await Until(() => _fake.LastSession!.Received.Any(r => r.Text == "%xt%zm%afk%1%false%"));
        await Until(() => !_fake.LastSession!.Afk && !c.World.IsMeAfk());
        Assert.True(c.GetStatistics().AfkClears >= 1);
    }

    [Fact]
    public async Task UnknownServerPacketsAreCountedNotDropped()
    {
        AqwConnection c = await LoggedIn();
        List<InboundPacket> seen = new();
        c.PacketReceived += (_, p) => seen.Add(p);
        _fake.Broadcast("%xt%mysteryCmd%-1%1%2%");
        _fake.Broadcast("Zthis-is-not-zlib");
        _fake.Broadcast(PacketCodec.Pack("{\"t\":\"xt\",\"b\":{\"r\":-1,\"o\":{\"cmd\":\"brandNew\",\"x\":1}}}"));
        await Until(() => seen.Count >= 3);
        PacketCounters counters = c.World.GetPacketCounters();
        Assert.Equal(1, counters.Unhandled["xt:mysteryCmd"]);
        Assert.Equal(1, counters.Unhandled["json:brandNew"]);
        Assert.Equal(1, counters.Unhandled["undecodable"]);
        Assert.Equal(1, c.GetStatistics().UndecodableIn);
    }

    [Fact]
    public async Task ExtensionResponsesAndThrowingSubscribers()
    {
        AqwConnection c = New();
        List<string> pext = new();
        c.ExtensionResponse += (_, e) => pext.Add(e.Json);
        c.PacketReceived += (_, _) => throw new InvalidOperationException("bad subscriber");
        await c.ConnectAsync(_fake.Server, "Hero", FakeAqwServer.Token, "5.32");
        Assert.True(c.IsReady);
        Assert.True(c.GetStatistics().SubscriberErrors > 0);
        JsonObject moveToArea = pext.Select(j => (JsonObject)JsonNode.Parse(j)!).First(o => o["params"]!["dataObj"] is JsonObject d && (string?)d["cmd"] == "moveToArea");
        Assert.Equal("json", (string)moveToArea["params"]!["type"]!);
        Assert.Contains(pext, j => j.Contains("\"loginResponse\""));
    }

    [Fact]
    public async Task ConnectionsAreSingleUse()
    {
        AqwConnection c = await LoggedIn();
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.ConnectAsync(_fake.Server, "Hero", FakeAqwServer.Token, "5.32"));
    }
}

/// <summary>Session-level tests: HTTP login (faked), version discovery and retry, reconnect, Flash events.</summary>
public class SessionTests : IAsyncLifetime
{
    private readonly FakeAqwServer _fake = new();
    private readonly FakeHttp _http = new();
    private int _logins;

    public Task InitializeAsync()
    {
        _http.Routes["api/login/now"] = _ =>
        {
            Interlocked.Increment(ref _logins);
            return FakeHttp.Json(new JsonObject
            {
                ["login"] = new JsonObject { ["bSuccess"] = 1, ["sToken"] = _fake.IssueToken("Hero"), ["unm"] = "Hero", ["userid"] = 1, ["iLevel"] = 30, ["iUpgDays"] = -1 },
                ["servers"] = new JsonArray(
                    new JsonObject { ["sName"] = "Sandbox", ["sIP"] = "127.0.0.1", ["iPort"] = _fake.Port, ["iCount"] = 1, ["iMax"] = 1000, ["bOnline"] = 1, ["bUpg"] = 0 },
                    new JsonObject { ["sName"] = "MembersOnly", ["sIP"] = "127.0.0.1", ["iPort"] = 1, ["iCount"] = 0, ["iMax"] = 1000, ["bOnline"] = 1, ["bUpg"] = 1 }),
            }.ToJsonString());
        };
        _http.Routes["api/data/gameversion"] = _ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("blocked") };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fake.DisposeAsync();
    }

    private AqwSession NewSession()
    {
        AqwSessionOptions options = new() { Connection = ConnectionTests.Fast() };
        AqwWebApi api = new(new AqwWebApiOptions { BaseUri = new Uri("https://example.test/game/") }, _http);
        return new AqwSession(options, api);
    }

    [Fact]
    public async Task RetriesWithTheVersionTheServerRequires()
    {
        _fake.RequiredClientVersion = "5.40";
        await using AqwSession session = NewSession();
        await session.LoginAsync(new AqwCredentials("hero", "pw"));
        GameServer server = session.PickServer();
        Assert.Equal("Sandbox", server.Name);
        await session.ConnectAsync(server);
        Assert.True(session.Connection!.IsReady);
        Assert.Equal("5.40", session.ClientVersion!.Version);
        Assert.Equal(ClientVersionSource.ServerRequired, session.ClientVersion.Source);
        Assert.Equal(new[] { "hero~" + ClientVersion.Fallback, "hero~5.4" }, _fake.LoginNicks.ToArray());
        Assert.Equal(2, _logins); // a fresh token for the retry
    }

    [Fact]
    public async Task ReconnectKeepsTheWorldInstanceAndEventSubscriptions()
    {
        await using AqwSession session = NewSession();
        WorldModel world = session.World;
        List<DisconnectInfo> drops = new();
        session.Disconnected += (_, e) => drops.Add(e);
        await session.LoginAsync(new AqwCredentials("hero", "pw"));
        await session.ConnectAsync(session.PickServer());
        _fake.LastSession!.Close();
        await ConnectionTests.Until(() => drops.Count == 1);
        Assert.True(drops[0].ShouldReconnect);

        await session.ReconnectAsync();
        Assert.Same(world, session.World);
        Assert.True(session.Connection!.IsReady);
        Assert.Equal("Hero", world.GetPlayer()!.Username);
        Assert.Equal(2, _fake.Sessions.Count);
        Assert.Single(drops); // the reconnect's own close of the dead connection raised nothing new
    }

    [Fact]
    public async Task FlashEventsHaveSkuaSwfShape()
    {
        await using AqwSession session = NewSession();
        using HeadlessFlashEvents flash = new(session);
        List<(string Name, object[] Args)> calls = new();
        flash.FlashCall += (name, args) =>
        {
            lock (calls)
                calls.Add((name, args));
        };
        await session.LoginAsync(new AqwCredentials("hero", "pw"));
        await session.ConnectAsync(session.PickServer());
        await session.Commands.JumpAsync("r2", "Left");
        await session.DisconnectAsync();

        List<(string Name, object[] Args)> snapshot;
        lock (calls)
            snapshot = calls.ToList();
        int raw = snapshot.FindIndex(c => c.Name == "packetFromServer" && ((string)c.Args[0]).Contains("\"moveToArea\""));
        Assert.True(raw >= 0);
        Assert.Equal("pext", snapshot[raw + 1].Name); // packetFromServer first, then pext, like skua.swf
        JsonObject pext = (JsonObject)JsonNode.Parse((string)snapshot[raw + 1].Args[0])!;
        Assert.Equal("moveToArea", (string)pext["params"]!["dataObj"]!["cmd"]!);

        Assert.Contains(snapshot, c => c.Name == "packet" && (string)c.Args[0] == $"%xt%zm%moveToCell%{session.World.RoomId}%r2%Left%");
        Assert.DoesNotContain(snapshot, c => c.Name == "packet" && ((string)c.Args[0]).StartsWith('<'));
        Assert.Equal("connectionLost", snapshot.Last().Name);
        Assert.DoesNotContain(snapshot, c => c.Args.OfType<string>().Any(s => s.Contains(FakeAqwServer.TokenPrefix)));
    }

    [Fact]
    public async Task PreferredServerTheAccountCannotUseIsSkipped()
    {
        await using AqwSession session = NewSession();
        await session.LoginAsync(new AqwCredentials("hero", "pw"));
        Assert.Equal("Sandbox", session.PickServer("MembersOnly").Name); // not usable for a free account
    }
}
