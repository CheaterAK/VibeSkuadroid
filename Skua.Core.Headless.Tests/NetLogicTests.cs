using Skua.Core.Headless.Net;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Tests;

public class PacerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SendPacer NewPacer(int max = 60)
    {
        return new SendPacer(TimeSpan.FromMilliseconds(220), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30), max);
    }

    private static OutboundItem Item(string text, SendPriority priority = SendPriority.Normal)
    {
        return new OutboundItem(text, priority, wantCompletion: true);
    }

    [Fact]
    public void GamePacketsLeaveOneGapApart()
    {
        SendPacer pacer = NewPacer();
        pacer.Enqueue(Item("%xt%zm%a%1%"));
        pacer.Enqueue(Item("%xt%zm%b%1%"));
        Assert.True(pacer.TryDequeue(T0, out OutboundItem? first, out _));
        Assert.Equal("a", first!.Command);
        pacer.MarkWritten(T0);
        Assert.False(pacer.TryDequeue(T0.AddMilliseconds(100), out _, out TimeSpan wait));
        Assert.Equal(TimeSpan.FromMilliseconds(120), wait);
        Assert.True(pacer.TryDequeue(T0.AddMilliseconds(220), out OutboundItem? second, out _));
        Assert.Equal("b", second!.Command);
    }

    [Fact]
    public void HandshakeXmlIsNotPaced()
    {
        SendPacer pacer = NewPacer();
        pacer.Enqueue(Item("%xt%zm%a%1%"));
        Assert.True(pacer.TryDequeue(T0, out _, out _));
        pacer.MarkWritten(T0);
        pacer.Enqueue(Item("<msg t='sys'/>", SendPriority.Immediate));
        Assert.True(pacer.TryDequeue(T0.AddMilliseconds(1), out OutboundItem? xml, out _));
        Assert.False(xml!.Paced);
    }

    [Fact]
    public void SlowDownWidensTheGapForTheHoldTime()
    {
        SendPacer pacer = NewPacer();
        pacer.SlowDown(T0);
        Assert.Equal(TimeSpan.FromMilliseconds(500), pacer.CurrentGap(T0.AddSeconds(29)));
        Assert.Equal(TimeSpan.FromMilliseconds(220), pacer.CurrentGap(T0.AddSeconds(30)));
    }

    [Fact]
    public void PriorityPacketsJumpTheQueueInOrder()
    {
        SendPacer pacer = NewPacer();
        pacer.Enqueue(Item("%xt%zm%n1%1%"));
        pacer.Enqueue(Item("%xt%zm%h1%1%", SendPriority.High));
        pacer.Enqueue(Item("%xt%zm%h2%1%", SendPriority.High));
        pacer.Enqueue(Item("<i/>", SendPriority.Immediate));
        List<string> order = new();
        DateTimeOffset t = T0;
        while (pacer.TryDequeue(t, out OutboundItem? item, out _))
        {
            order.Add(item!.Command ?? item.Text);
            t = t.AddSeconds(1);
            pacer.MarkWritten(t);
            t = t.AddSeconds(1);
        }
        Assert.Equal(new[] { "<i/>", "h1", "h2", "n1" }, order);
    }

    [Fact]
    public void AggroPullsAreMergedAndCounted()
    {
        SendPacer pacer = NewPacer();
        OutboundItem old = Item("%xt%zm%aggroMon%1%1%");
        pacer.Enqueue(old);
        pacer.Enqueue(Item("%xt%zm%aggroMon%1%1%2%"));
        Assert.Equal(1, pacer.Count);
        Assert.Equal(1, pacer.Superseded);
        Assert.False(old.Completion.Result);
    }

    [Fact]
    public void OverflowDropsFillerFirstThenOldestAndCompletesThemFalse()
    {
        SendPacer pacer = NewPacer(max: 3);
        OutboundItem attack = Item("%xt%zm%gar%1%1%aa>m:1%wvz%");
        OutboundItem walk = Item("%xt%zm%mv%1%1%2%8%");
        pacer.Enqueue(attack);
        pacer.Enqueue(walk);
        pacer.Enqueue(Item("%xt%zm%acceptQuest%1%5%"));
        IReadOnlyList<OutboundItem> lost = pacer.Enqueue(Item("%xt%zm%getDrop%1%5%"));
        Assert.Same(walk, Assert.Single(lost));
        Assert.False(walk.Completion.Result);
        lost = pacer.Enqueue(Item("%xt%zm%getDrop%1%6%"));
        Assert.Same(attack, Assert.Single(lost));
        Assert.Equal(2, pacer.Dropped);
        Assert.Equal(3, pacer.Count);
    }

    [Fact]
    public void ClearCompletesEverythingFalse()
    {
        SendPacer pacer = NewPacer();
        OutboundItem a = Item("%xt%zm%a%1%");
        pacer.Enqueue(a);
        Assert.Equal(1, pacer.Clear());
        Assert.False(a.Completion.Result);
    }
}

public class LivenessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static LivenessMonitor Monitor()
    {
        return new LivenessMonitor(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120), T0);
    }

    [Fact]
    public void QuietWithoutPendingRequestsIsFineUntilIdleProbe()
    {
        LivenessMonitor m = Monitor();
        Assert.Equal(LivenessVerdict.Ok, m.Check(T0.AddSeconds(60)));
        Assert.Equal(LivenessVerdict.Probe, m.Check(T0.AddSeconds(120)));
    }

    [Fact]
    public void UnansweredRequestLeadsToProbeThenDead()
    {
        LivenessMonitor m = Monitor();
        m.OnRequestExpectingReply(T0);
        Assert.Equal(LivenessVerdict.Ok, m.Check(T0.AddSeconds(24)));
        Assert.Equal(LivenessVerdict.Probe, m.Check(T0.AddSeconds(25)));
        m.OnProbeSent(T0.AddSeconds(25));
        Assert.Equal(LivenessVerdict.Probing, m.Check(T0.AddSeconds(30)));
        Assert.Equal(LivenessVerdict.Dead, m.Check(T0.AddSeconds(35)));
    }

    [Fact]
    public void AnyInboundAnswersTheProbe()
    {
        LivenessMonitor m = Monitor();
        m.OnRequestExpectingReply(T0);
        m.OnProbeSent(T0.AddSeconds(25));
        m.OnInbound(T0.AddSeconds(27));
        Assert.Equal(LivenessVerdict.Ok, m.Check(T0.AddSeconds(40)));
    }

    [Fact]
    public void RecentTrafficPostponesTheProbe()
    {
        LivenessMonitor m = Monitor();
        m.OnInbound(T0.AddSeconds(20));
        m.OnRequestExpectingReply(T0.AddSeconds(21));
        Assert.Equal(LivenessVerdict.Ok, m.Check(T0.AddSeconds(40)));
        Assert.Equal(LivenessVerdict.Probe, m.Check(T0.AddSeconds(46)));
    }
}

public class ClientVersionTests
{
    private static byte[] Swf(string signature, string body)
    {
        byte[] content = Encoding.Latin1.GetBytes(body);
        byte[] header = new byte[8];
        Encoding.ASCII.GetBytes(signature).CopyTo(header, 0);
        header[3] = 10;
        if (signature == "FWS")
            return header.Concat(content).ToArray();
        using MemoryStream output = new();
        using (ZLibStream z = new(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(content);
        return header.Concat(output.ToArray()).ToArray();
    }

    [Theory]
    [InlineData("FWS")]
    [InlineData("CWS")]
    public void ReadsVParamFromTheStringPool(string signature)
    {
        string pool = "\u0006vParam\u00045.32\u0005ISWEB";
        Assert.Equal("5.32", ClientVersion.FromSwf(Swf(signature, "junk" + pool + "more")));
    }

    [Fact]
    public void LzmaAndUnknownFilesGiveNull()
    {
        Assert.Null(ClientVersion.FromSwf(Encoding.ASCII.GetBytes("ZWS\u000aaaaaaaaaaaaaaaa")));
        Assert.Null(ClientVersion.FromSwf(Encoding.ASCII.GetBytes("hello world")));
        Assert.Null(ClientVersion.FromSwf(Swf("FWS", "no version here")));
    }

    [Fact]
    public void ReadsTheRequiredVersionFromTheRefusal()
    {
        const string message = "Your game client is an older version. Server requires AQW game version 5.40 or higher. Please close the AQW game window and try again.";
        Assert.Equal("5.40", ClientVersion.RequiredFromMessage(message));
        Assert.Null(ClientVersion.RequiredFromMessage("Invalid login token."));
        Assert.True(ClientVersion.IsValid(ClientVersion.Fallback));
    }

    [Fact]
    public async Task ResolverReadsTheLiveGameFile()
    {
        FakeHttp http = new();
        http.Routes["api/data/gameversion"] = _ => FakeHttp.Json("""{"sFile":"Game4000r9.swf","sTitle":"AQW","sVersion":"R5"}""");
        http.Routes["gamefiles/Game4000r9.swf"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Swf("CWS", "\u0006vParam\u00045.41")) };
        using AqwWebApi api = new(new AqwWebApiOptions { BaseUri = new Uri("https://example.test/game/") }, http);
        ClientVersionResolver resolver = new(api);
        ClientVersionResult result = await resolver.ResolveAsync();
        Assert.Equal("5.41", result.Version);
        Assert.Equal(ClientVersionSource.GameFile, result.Source);
        result = await resolver.ResolveAsync();
        Assert.Contains("cached", result.Detail);
        Assert.Equal(1, http.Hits["gamefiles/Game4000r9.swf"]);
    }

    [Fact]
    public async Task ResolverFallsBackWithAReason()
    {
        FakeHttp http = new();
        http.Routes["api/data/gameversion"] = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied") };
        using AqwWebApi api = new(new AqwWebApiOptions { BaseUri = new Uri("https://example.test/game/") }, http);
        ClientVersionResult result = await new ClientVersionResolver(api).ResolveAsync();
        Assert.Equal(ClientVersion.Fallback, result.Version);
        Assert.Equal(ClientVersionSource.Fallback, result.Source);
        Assert.Contains("403", result.Detail);
    }
}

public class ServerPickerTests
{
    private static GameServer S(string name, int count, int max = 1000, bool online = true, bool member = false, int level = 0, int access = 0)
    {
        return new GameServer(name, name.ToLowerInvariant() + ".example", 5588, count, max, online, member, level, access, "en", null);
    }

    [Fact]
    public void PicksTheLeastFullUsableServer()
    {
        GameServer[] servers = { S("Artix", 900), S("Twilly", 100), S("Members", 0, member: true), S("Off", 0, online: false), S("Staff", 0, access: 40), S("PTR", 0) };
        Assert.Equal("Twilly", ServerPicker.Pick(servers, member: false, level: 10)!.Name);
        Assert.Equal("Members", ServerPicker.Pick(servers, member: true, level: 10)!.Name);
    }

    [Fact]
    public void HonoursAPreferredNameWithOrWithoutRegion()
    {
        GameServer[] servers = { S("Artix", 900), S("Twilly", 100), S("Swordhaven (EU)", 500) };
        Assert.Equal("Artix", ServerPicker.Pick(servers, false, 10, "artix")!.Name);
        Assert.Equal("Swordhaven (EU)", ServerPicker.Pick(servers, false, 10, "Swordhaven")!.Name);
        Assert.Equal("Twilly", ServerPicker.Pick(servers, false, 10, "Nope")!.Name);
    }

    [Fact]
    public void FullAndLevelGatedServersAreSkipped()
    {
        GameServer[] servers = { S("Full", 1000), S("High", 0, level: 50) };
        Assert.Null(ServerPicker.Pick(servers, false, 10));
        Assert.Equal("High", ServerPicker.Pick(servers, false, 60)!.Name);
    }

    [Fact]
    public void ParsesTheServerList()
    {
        IReadOnlyList<GameServer> list = GameServer.ListFromJson(JsonNode.Parse("""[{"sName":"Twilly","sIP":"socket.example","iPort":"5588","iCount":12,"iMax":1000,"bOnline":1,"bUpg":0,"iLevel":0,"sLang":"en"},{"sName":"Broken"}]"""));
        GameServer twilly = Assert.Single(list);
        Assert.Equal(5588, twilly.Port);
        Assert.True(twilly.Online);
        Assert.False(twilly.MembersOnly);
    }
}

public class WebApiTests
{
    private const string LoginReply = """
        {"login":{"bSuccess":1,"sToken":"secret-token","unm":"Hero","userid":123,"iLevel":12,"iUpgDays":-1,"strEmail":"x"},
         "servers":[{"sName":"Twilly","sIP":"127.0.0.1","iPort":5588,"iCount":1,"iMax":1000,"bOnline":1,"bUpg":0}]}
        """;

    [Fact]
    public async Task LoginPostsTheFormAndReturnsTokenAndServers()
    {
        FakeHttp http = new();
        http.Routes["api/login/now"] = _ => FakeHttp.Json(LoginReply);
        using AqwWebApi api = new(new AqwWebApiOptions { BaseUri = new Uri("https://example.test/game/") }, http);
        LoginResult result = await api.LoginAsync("hero", "pw");
        Assert.Equal("secret-token", result.Token);
        Assert.Equal("Hero", result.Username);
        Assert.Equal(12, result.Level);
        Assert.False(result.IsMember);
        Assert.Equal("Twilly", Assert.Single(result.Servers).Name);
        Assert.False(result.Raw.ContainsKey("sToken"));
        Assert.DoesNotContain("secret-token", result.ToString());

        HttpRequestMessage request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.StartsWith("https://example.test/game/api/login/now?ran=0.", request.RequestUri!.ToString());
        Assert.Equal("user=hero&pass=pw&option=1", http.Bodies[0]);
        Assert.Equal("https://game.aq.com/game/gamefiles/", request.Headers.Referrer!.ToString());
    }

    [Fact]
    public async Task RefusedLoginThrowsRejected()
    {
        FakeHttp http = new();
        http.Routes["api/login/now"] = _ => FakeHttp.Json("""{"login":{"bSuccess":0,"sMsg":"Wrong password"}}""");
        using AqwWebApi api = new(new AqwWebApiOptions { BaseUri = new Uri("https://example.test/game/") }, http);
        AqwLoginException e = await Assert.ThrowsAsync<AqwLoginException>(() => api.LoginAsync("hero", "bad"));
        Assert.Equal(LoginFailureKind.Rejected, e.Kind);
        Assert.Contains("Wrong password", e.Message);
    }

    [Fact]
    public async Task TransportFailuresAreReportedAsHttp()
    {
        FakeHttp http = new();
        http.Routes["api/login/now"] = _ => throw new HttpRequestException("The proxy tunnel request to proxy failed with status code '403'.");
        using AqwWebApi api = new(new AqwWebApiOptions { BaseUri = new Uri("https://example.test/game/") }, http);
        AqwLoginException e = await Assert.ThrowsAsync<AqwLoginException>(() => api.LoginAsync("hero", "pw"));
        Assert.Equal(LoginFailureKind.Http, e.Kind);
        Assert.Contains("403", e.Message);
        Assert.DoesNotContain("pw", e.Message.Replace("proxy", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerListEndpoint()
    {
        FakeHttp http = new();
        http.Routes["api/data/servers"] = _ => FakeHttp.Json("""[{"sName":"Yorumi","sIP":"y.example","iPort":5588,"iCount":3,"iMax":500,"bOnline":true}]""");
        using AqwWebApi api = new(new AqwWebApiOptions { BaseUri = new Uri("https://example.test/game/") }, http);
        Assert.Equal("Yorumi", Assert.Single(await api.GetServersAsync()).Name);
    }
}

public class CredentialsTests
{
    [Fact]
    public void ReadsADotenvFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# test\nexport AQW_USER=\"someone\"\nAQW_PASS='p=ss word'\nOTHER=1\n");
            AqwCredentials c = AqwCredentials.FromEnvFile(path);
            Assert.Equal("someone", c.Username);
            Assert.Equal("p=ss word", c.Password);
            Assert.DoesNotContain("p=ss", c.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingVariablesAreAnError()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "AQW_USER=someone\n");
            Assert.Throws<InvalidDataException>(() => AqwCredentials.FromEnvFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>An HttpMessageHandler that answers from a route table; nothing leaves the process.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public List<string> Bodies { get; } = new();

    public Dictionary<string, int> Hits { get; } = new();

    public static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        string path = request.RequestUri!.AbsolutePath.TrimStart('/');
        if (path.StartsWith("game/", StringComparison.Ordinal))
            path = path[5..];
        Hits[path] = Hits.GetValueOrDefault(path) + 1;
        if (Routes.TryGetValue(path, out Func<HttpRequestMessage, HttpResponseMessage>? route))
            return route(request);
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
    }
}
