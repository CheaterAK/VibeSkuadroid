using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Tests.Fakes;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>
/// The login path the Android app takes: an HTTP login for the server list, then Skua's own relogin (EnsureRelogin),
/// which logs in over HTTP again before it joins. Each web login replaces the token on the server, so the socket must
/// use the token of the web login just before it, and one web login per socket attempt is enough.
/// </summary>
public sealed class LoginTokenTests : IAsyncLifetime
{
    private static readonly string s_data = Path.Combine(Path.GetTempPath(), "skua-headless-logintoken-" + Environment.ProcessId);
    private readonly List<string> _log = new();
    private FakeWorld _world = null!;

    public Task InitializeAsync()
    {
        _world = new FakeWorld();
        // The server replaces the token as soon as it processes a web login; the reply takes a while to arrive.
        _world.Http.Delays["api/login/now"] = TimeSpan.FromMilliseconds(500);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _world.DisposeAsync();
    }

    private HeadlessSkuaSession NewSession(AqwSessionOptions? session = null)
    {
        return HeadlessSkuaSession.Create(new HeadlessSkuaOptions
        {
            DataDirectory = s_data,
            WebApi = _world.Api,
            Game = new HeadlessGameOptions { Session = session ?? _world.SessionOptions, Client = new() { MapLoadTime = TimeSpan.FromMilliseconds(100) } },
            Log = e =>
            {
                lock (_log)
                    _log.Add($"[{e.Source}] {e.Message}");
            },
        });
    }

    private string Log()
    {
        lock (_log)
            return string.Join('\n', _log);
    }

    [Fact]
    public async Task SkuasReloginJoinsWithTheOneWebLoginBeforeIt()
    {
        await using HeadlessSkuaSession session = NewSession();
        Assert.True(await session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"), Log());

        // One web login (Skua's relogin reuses the server list whose token is still unused) and one socket login with it.
        Assert.Equal(1, _world.Http.Hits["api/login/now"]);
        Assert.Equal(new[] { (_world.Server.IssuedTokens.Single(), (string?)null) }, _world.Server.LoginOutcomes.ToArray());
        Assert.Equal(new[] { "hero~5.99" }, _world.Server.LoginNicks.ToArray());
        Assert.Equal(new[] { "157" }, _world.Server.ApiVersions.ToArray());
        Assert.Equal("Hero", session.Bot.Player.Username);

        string log = Log();
        Assert.DoesNotContain(FakeAqwServer.TokenPrefix, log);
        Assert.Contains("its server list is shown again instead of a second web login", log);
        Assert.Contains("with the token of web login #1", log);
        Assert.Contains("[handshake]", log);
        Assert.Contains("<< %xt%loginResponse%-1%true%", log);
    }

    [Fact]
    public async Task WithTwoWebLoginsInARowTheSocketUsesTheSecondToken()
    {
        // The reuse turned off: the engine's web login, then Skua's own. Before the fix Skua saw the first login's
        // server list as ready, joined at once and sent the first token, which the second login was replacing.
        HeadlessSkuaSession session = HeadlessSkuaSession.Create(new HeadlessSkuaOptions
        {
            DataDirectory = s_data,
            WebApi = _world.Api,
            Game = new HeadlessGameOptions
            {
                Session = _world.SessionOptions,
                Client = new() { MapLoadTime = TimeSpan.FromMilliseconds(100) },
                ReuseUnusedWebLoginFor = TimeSpan.Zero,
            },
            Log = e =>
            {
                lock (_log)
                    _log.Add($"[{e.Source}] {e.Message}");
            },
        });
        await using (session)
        {
            Assert.True(await session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"), Log());
            Assert.Equal(2, _world.Http.Hits["api/login/now"]);
            Assert.Equal(new[] { (_world.Server.IssuedTokens.Last(), (string?)null) }, _world.Server.LoginOutcomes.ToArray());
            Assert.Contains("with the token of web login #2", Log());
        }
    }

    [Fact]
    public async Task SkuasSecondAttemptSendsTheKnownGoodVersionAfterTheDiscoveredOneIsRefused()
    {
        // Live: the server refused 5.33 (Game4000r9.swf's vParam) with "Character data could not be retrieved"
        // while a client sending 5.32 got in. Skua's first attempt is refused, its second (fresh web login) sends 5.32.
        _world.Server.RefusedClientVersions.Add("5.33");
        _world.Http.Routes["api/data/gameversion"] = _ => FakeHttp.Json("""{"sFile":"Game4000r9.swf","sTitle":"t"}""");
        _world.Http.Routes["gamefiles/Game4000r9.swf"] = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(SyntheticSwf.GameFile("5.33")),
        };
        AqwSessionOptions options = _world.SessionOptions;
        options.ClientVersion = null;
        options.DiscoverClientVersion = true;
        await using HeadlessSkuaSession session = NewSession(options);
        Assert.True(await session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"), Log());

        (string Token, string? Refusal)[] outcomes = _world.Server.LoginOutcomes.ToArray();
        Assert.Equal(2, outcomes.Length); // no attempt beyond Skua's own
        Assert.Equal(FakeAqwServer.CharacterDataRefusal, outcomes[0].Refusal);
        Assert.Null(outcomes[1].Refusal);
        Assert.Equal(new[] { "hero~5.33", "hero~5.32" }, _world.Server.LoginNicks.ToArray());
        Assert.Equal(2, _world.Http.Hits["api/login/now"]); // one web login per socket attempt
        Assert.NotEqual(outcomes[0].Token, outcomes[1].Token);

        string log = Log();
        Assert.Contains("Fallback armed", log);
        Assert.Contains("Fallback: the last socket login with client version 5.33 was refused", log);
        Assert.Contains("Fallback result: client version 5.32 got in where 5.33 was refused.", log);
        Assert.Contains("%xt%loginResponse%-1%0%-1%%" + FakeAqwServer.CharacterDataRefusal + "%", log);
    }
}
