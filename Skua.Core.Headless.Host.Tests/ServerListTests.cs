using Skua.Core.Headless.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Models.Servers;
using Skua.Core.Scripts;
using Skua.Core.Utils;
using System.Net;
using WebClient = Skua.Core.Utils.WebClient;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>Records every request it sees (URL and Authorization header) and answers from a callback.</summary>
internal sealed class RecordingHttp : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

    public RecordingHttp(Func<HttpRequestMessage, HttpResponseMessage>? answer = null)
    {
        _answer = answer ?? (_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    public List<(Uri Uri, string? Authorization)> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
        return Task.FromResult(_answer(request));
    }
}

/// <summary>Skua's server list and its HTTP client: no GitHub credential off GitHub, no plain-http fetch on the headless client.</summary>
public sealed class ServerListTests
{
    private const string ServersJson = """
        [{"sName":"Sandbox","sIP":"127.0.0.1","iPort":5588,"iCount":12,"iMax":1000,"bOnline":1,"bUpg":0,"iChat":2,"iLevel":0,"sLang":"xx"},
         {"sName":"Yorumi","sIP":"127.0.0.3","iPort":5588,"iCount":3,"iMax":1000,"bOnline":1,"bUpg":1,"iChat":2,"iLevel":0,"sLang":"en"}]
        """;

    private static readonly string s_data = Path.Combine(Path.GetTempPath(), "skua-headless-serverlist-" + Environment.ProcessId);

    [Theory]
    [InlineData("http://content.aq.com/game/api/data/servers")]
    [InlineData("https://content.aq.com/game/api/data/servers")]
    [InlineData("https://game.aq.com/game/gamefiles/maps/town.swf")]
    [InlineData("https://raw.githubusercontent.com/auqw/Scripts/Skua/scripts.json")]
    [InlineData("https://github.com.example.net/x")]
    [InlineData("https://notgithub.com/x")]
    [InlineData("http://api.github.com/repos/auqw/Scripts")]
    public async Task GitHubCredentialIsNeverSentOffGitHub(string url)
    {
        RecordingHttp http = new();
        using WebClient app = new(true, http);
        using WebClient user = new("user-token", http);
        (await app.GetAsync(url)).Dispose();
        (await user.GetAsync(url)).Dispose();

        Assert.Equal(2, http.Requests.Count);
        Assert.All(http.Requests, r => Assert.Null(r.Authorization));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/auqw/Scripts/commits/Skua")]
    [InlineData("https://github.com/auqw/Scripts")]
    public async Task GitHubRequestsKeepTheirCredential(string url)
    {
        RecordingHttp http = new();
        using WebClient app = new(true, http);
        using WebClient user = new("user-token", http);
        (await app.GetAsync(url)).Dispose();
        (await user.GetAsync(url)).Dispose();

        Assert.StartsWith("Basic ", http.Requests[0].Authorization);
        Assert.Equal("bearer user-token", http.Requests[1].Authorization);
        Assert.Contains("application/json", app.DefaultRequestHeaders.Accept.ToString());
        Assert.Null(app.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void ShippedClientsCarryNoCredentialInTheirDefaultHeaders()
    {
        Assert.Null(HttpClients.GitHubClient.DefaultRequestHeaders.Authorization);
        Assert.Null(HttpClients.GetAQContent.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task DesktopServerListStillComesFromContentAqComButWithoutTheCredential()
    {
        RecordingHttp http = new(_ => FakeHttp.Json(ServersJson));
        HttpClients.UserGitHubClient = new WebClient(true, http);
        try
        {
            // The constructor the desktop's container uses (no IServerListProvider registered there).
            ScriptServers servers = new(
                NotUsed<IFlashUtil>(), NotUsed<IScriptPlayer>(), NotUsed<IScriptWait>(), NotUsed<IScriptOption>(),
                NotUsed<IScriptBotStats>(), NotUsed<IScriptManager>(), NotUsed<ILogService>());
            List<Server> list = await servers.GetServers(true);

            Assert.Equal(new[] { "Sandbox", "Yorumi" }, list.Select(s => s.Name));
            (Uri uri, string? auth) = Assert.Single(http.Requests);
            Assert.Equal("http://content.aq.com/game/api/data/servers", uri.ToString());
            Assert.Null(auth);
        }
        finally
        {
            HttpClients.UserGitHubClient?.Dispose();
            HttpClients.UserGitHubClient = null;
        }
    }

    [Fact]
    public async Task HeadlessLoginReadsTheServerListOverHttpsAndNeverAsksContentAqCom()
    {
        RecordingHttp skuaClient = new(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        HttpClients.UserGitHubClient = new WebClient(true, skuaClient);
        await using FakeWorld world = new();
        world.Http.Routes["api/data/servers"] = r =>
        {
            Assert.Equal("https", r.RequestUri!.Scheme);
            Assert.Equal("game.aq.com", r.RequestUri.Host);
            Assert.Null(r.Headers.Authorization);
            return FakeHttp.Json(ServersJson.Replace("5588,\"iCount\":12", world.Server.Port + ",\"iCount\":12", StringComparison.Ordinal));
        };
        HeadlessSkuaSession session = NewSession(world);
        try
        {
            Assert.True(await session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"));
            Assert.True(world.Http.Hits.GetValueOrDefault("api/data/servers") >= 1);
            Assert.Contains(session.Bot.Servers.CachedServers, s => s.Name == "Yorumi" && s.Upgrade && s.PlayerCount == 3);
            Assert.Empty(skuaClient.Requests);
        }
        finally
        {
            await session.DisposeAsync();
            HttpClients.UserGitHubClient?.Dispose();
            HttpClients.UserGitHubClient = null;
        }
    }

    [Fact]
    public async Task HeadlessServerListFallsBackToTheLoginReply()
    {
        RecordingHttp skuaClient = new(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        HttpClients.UserGitHubClient = new WebClient(true, skuaClient);
        await using FakeWorld world = new();   // no api/data/servers route: HTTP 404
        HeadlessSkuaSession session = NewSession(world);
        try
        {
            Assert.True(await session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"));
            Assert.True(world.Http.Hits.GetValueOrDefault("api/data/servers") >= 1);
            Assert.Equal(new[] { "Sandbox", "Offline" }, session.Bot.Servers.CachedServers.Select(s => s.Name));
            Assert.Equal(world.Server.Port, session.Bot.Servers.CachedServers[0].Port);
            Assert.False(session.Bot.Servers.CachedServers[1].Online);
            Assert.Empty(skuaClient.Requests);
        }
        finally
        {
            await session.DisposeAsync();
            HttpClients.UserGitHubClient?.Dispose();
            HttpClients.UserGitHubClient = null;
        }
    }

    [Fact]
    public void HeadlessContainerUsesTheProvider()
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddHeadlessSkua();
        Assert.Contains(services, d => d.ServiceType == typeof(IServerListProvider));
    }

    private static HeadlessSkuaSession NewSession(FakeWorld world)
    {
        return HeadlessSkuaSession.Create(new HeadlessSkuaOptions
        {
            DataDirectory = s_data,
            WebApi = world.Api,
            Game = new HeadlessGameOptions { Session = world.SessionOptions, Client = new() { MapLoadTime = TimeSpan.FromMilliseconds(100) } },
        });
    }

    private static Lazy<T> NotUsed<T>() => new(() => throw new InvalidOperationException(typeof(T).Name + " is not used by GetServers"));
}
