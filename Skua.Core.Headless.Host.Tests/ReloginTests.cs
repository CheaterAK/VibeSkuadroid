using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Scripts;
using Xunit.Abstractions;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>
/// Skua's relogin against two fake servers sharing one account database: with no Relogin Server chosen it goes back
/// to the server of the last login (not Skua's default "Twilly"), a chosen one is kept, and Skua's fallback still
/// moves on when that server is down.
/// </summary>
public sealed class ReloginTests : IAsyncLifetime
{
    private static readonly string s_data = Path.Combine(Path.GetTempPath(), "skua-headless-relogin-" + Environment.ProcessId);
    private readonly ITestOutputHelper _output;
    private readonly List<string> _log = new();
    private FakeWorld _world = null!;
    private FakeAqwServer _second = null!;
    private HeadlessSkuaSession _session = null!;
    private bool _secondFull;
    private bool _secondStopped;

    public ReloginTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private string ServersJson(bool secondFull) => $$"""
        [{"sName":"Sandbox","sIP":"127.0.0.1","iPort":{{_world.Server.Port}},"iCount":1,"iMax":1000,"bOnline":1,"bUpg":0,"iChat":2,"iLevel":0,"sLang":"xx"},
         {"sName":"Second","sIP":"127.0.0.1","iPort":{{_second.Port}},"iCount":{{(secondFull ? 1000 : 1)}},"iMax":1000,"bOnline":1,"bUpg":0,"iChat":2,"iLevel":0,"sLang":"xx"},
         {"sName":"Offline","sIP":"127.0.0.2","iPort":5588,"iCount":0,"iMax":1000,"bOnline":0,"bUpg":0,"iChat":2,"iLevel":0,"sLang":"xx"}]
        """;

    public Task InitializeAsync()
    {
        _world = new FakeWorld();
        _second = new FakeAqwServer { Accounts = _world.Server, Name = "Second" };
        _world.Http.Routes["api/login/now"] = r =>
        {
            string body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!body.Contains("pass=" + FakeWorld.Pass, StringComparison.Ordinal))
                return FakeHttp.Json("""{"login":{"bSuccess":0,"sMsg":"Wrong password"}}""");
            return FakeHttp.Json($$"""
                {"login":{"bSuccess":1,"unm":"{{FakeWorld.User}}","userid":77,"iLevel":30,"iUpgDays":-1,"sToken":"{{_world.Server.IssueToken(FakeWorld.User)}}"},
                 "servers":{{ServersJson(false)}}}
                """);
        };
        _world.Http.Routes["api/data/servers"] = _ => FakeHttp.Json(ServersJson(_secondFull));
        _session = HeadlessSkuaSession.Create(new HeadlessSkuaOptions
        {
            DataDirectory = s_data,
            WebApi = _world.Api,
            Game = new HeadlessGameOptions { Session = _world.SessionOptions, Client = new() { MapLoadTime = TimeSpan.FromMilliseconds(100) } },
            Log = e =>
            {
                lock (_log)
                    _log.Add($"[{e.Source}] {e.Message}");
            },
        });
        _session.Bot.Options.ReloginTries = 3;
        _session.Bot.Options.ReloginTryDelay = 200;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        lock (_log)
        {
            foreach (string l in _log.Where(l => l.Contains("Relogin", StringComparison.OrdinalIgnoreCase)))
                _output.WriteLine(l);
        }
        await _session.DisposeAsync();
        if (!_secondStopped)
            await _second.DisposeAsync();
        await _world.DisposeAsync();
    }

    private static int LoggedIn(FakeAqwServer server) => server.Sessions.Count(s => s.LoggedIn);

    // The connection drops (as when the server restarts or the network goes); Skua's auto relogin runs as it does after
    // a disconnect, choosing the server by its own rules.
    private async Task<bool> DropAndRelogin(FakeAqwServer current)
    {
        current.LastSession!.Close();
        Assert.True(await FakeWorld.Until(() => !_session.Game.IsInGame));
        return await _session.Bot.Servers.EnsureRelogin(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task WithNoReloginServerChosenSkuaGoesBackToTheServerOfTheLastLogin()
    {
        Assert.Equal(HeadlessSkuaSession.SkuaDefaultReloginServer, _session.Bot.Options.ReloginServer);
        Assert.False(_session.ReloginServerChosen);
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Second"), string.Join("\n", _log));
        Assert.Equal(1, LoggedIn(_second));
        Assert.Equal("Second", _session.Game.LastLoginServer?.Name);
        Assert.Equal("Second", _session.Bot.Options.ReloginServer);
        Assert.False(_session.ReloginServerChosen);

        Assert.True(await DropAndRelogin(_second), string.Join("\n", _log));
        Assert.Equal(2, LoggedIn(_second));
        Assert.Equal(0, LoggedIn(_world.Server));
    }

    [Fact]
    public async Task AChosenReloginServerIsKept()
    {
        _session.Bot.Options.ReloginServer = "Sandbox";
        Assert.True(_session.ReloginServerChosen);
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Second"), string.Join("\n", _log));
        Assert.Equal("Sandbox", _session.Bot.Options.ReloginServer);

        Assert.True(await DropAndRelogin(_second), string.Join("\n", _log));
        Assert.Equal(1, LoggedIn(_world.Server));
        Assert.Equal(1, LoggedIn(_second));
    }

    [Fact]
    public async Task SkuasFallbackStillMovesOnWhenTheLastServerIsDown()
    {
        ((ScriptOption)_session.Bot.Options).ReloginFallback = true;
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Second"), string.Join("\n", _log));
        Assert.Equal("Second", _session.Bot.Options.ReloginServer);

        // Second goes down (its listing says full, its socket refuses): Skua tries it, then the next eligible server.
        _secondFull = true;
        _secondStopped = true;
        await _second.DisposeAsync();
        Assert.True(await FakeWorld.Until(() => !_session.Game.IsInGame));
        Assert.True(await _session.Bot.Servers.EnsureRelogin(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)), string.Join("\n", _log));
        Assert.Equal(1, LoggedIn(_world.Server));
        Assert.Equal("Sandbox", _session.Game.LastLoginServer?.Name);
        // The next relogin follows the server the character is on now.
        Assert.Equal("Sandbox", _session.Bot.Options.ReloginServer);
    }
}
