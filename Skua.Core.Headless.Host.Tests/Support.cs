using Skua.Core.Headless.Net;
using Skua.Core.Headless.Tests.Fakes;
using System.Net;
using System.Text;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Skua.Core.Headless.Host.Tests;

/// <summary>An HttpMessageHandler that answers from a route table; nothing leaves the process.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new();

    public Dictionary<string, int> Hits { get; } = new();

    /// <summary>Per path: how long the reply takes after the route ran (the server acted at once, the answer arrives later).</summary>
    public Dictionary<string, TimeSpan> Delays { get; } = new();

    public static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath.TrimStart('/');
        if (path.StartsWith("game/", StringComparison.Ordinal))
            path = path[5..];
        lock (Hits)
            Hits[path] = Hits.GetValueOrDefault(path) + 1;
        if (!Routes.TryGetValue(path, out Func<HttpRequestMessage, HttpResponseMessage>? route))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
        HttpResponseMessage response = route(request);
        if (Delays.TryGetValue(path, out TimeSpan delay))
            await Task.Delay(delay, cancellationToken);
        return response;
    }
}

/// <summary>A fake game server plus the HTTP side (login with its server list, the map file).</summary>
internal sealed class FakeWorld : IAsyncDisposable
{
    public const string User = "Hero";
    public const string Pass = "pw";

    public FakeWorld()
    {
        Server = new FakeAqwServer { AutoAttackCooldownMs = 700, RespawnDelay = TimeSpan.FromMilliseconds(100) };
        Http.Routes["api/login/now"] = r =>
        {
            string body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!body.Contains("pass=" + Pass, StringComparison.Ordinal))
                return FakeHttp.Json("""{"login":{"bSuccess":0,"sMsg":"Wrong password"}}""");
            return FakeHttp.Json($$"""
                {"login":{"bSuccess":1,"unm":"{{User}}","userid":77,"iLevel":30,"iUpgDays":-1,"sToken":"{{Server.IssueToken(User)}}"},
                 "servers":[{"sName":"Sandbox","sIP":"127.0.0.1","iPort":{{Server.Port}},"iCount":1,"iMax":1000,"bOnline":1,"bUpg":0,"iChat":2,"iLevel":0,"sLang":"xx"},
                            {"sName":"Offline","sIP":"127.0.0.2","iPort":5588,"iCount":0,"iMax":1000,"bOnline":0,"bUpg":0,"iChat":2,"iLevel":0,"sLang":"xx"}]}
                """);
        };
        // The bank as the game loads it (POST api/char/bank); the socket loadBank stays unanswered, as suspected live.
        Server.AnswerSocketLoadBank = false;
        Http.Routes["api/char/bank"] = Server.AnswerBankRequest;
        Http.Routes["gamefiles/maps/test/testmap.swf"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(SyntheticSwf.Map(new[] { ("Enter", new[] { "Spawn", "Left" }), ("r2", new[] { "Left", "Right" }), ("r3", new[] { "Left" }) })),
        };
        Api = new AqwWebApi(new AqwWebApiOptions(), Http);
    }

    public FakeAqwServer Server { get; }

    public FakeHttp Http { get; } = new();

    public AqwWebApi Api { get; }

    public AqwSessionOptions SessionOptions => new() { ClientVersion = "5.99", DiscoverClientVersion = false };

    public async ValueTask DisposeAsync()
    {
        Api.Dispose();
        await Server.DisposeAsync();
    }

    public static async Task<bool> Until(Func<bool> condition, int timeoutMs = 10000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
