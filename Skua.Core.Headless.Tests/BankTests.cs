using Skua.Core.Headless.Commands;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Headless.World;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Tests;

/// <summary>
/// The bank, loaded as Game4000r9.swf does (Game.getBank: POST api/char/bank with ccid and token headers and the
/// form field layout), against <see cref="FakeAqwServer.AnswerBankRequest"/>, which refuses any other request shape.
/// </summary>
public class BankTests : IAsyncLifetime
{
    private readonly FakeAqwServer _fake = new() { AnswerSocketLoadBank = false };
    private readonly FakeHttp _http = new();
    private readonly List<string> _log = new();

    public Task InitializeAsync()
    {
        _http.Routes["api/login/now"] = _ => FakeHttp.Json(new JsonObject
        {
            ["login"] = new JsonObject { ["bSuccess"] = 1, ["sToken"] = _fake.IssueToken("Hero"), ["unm"] = "Hero", ["userid"] = 1, ["iLevel"] = 30, ["iUpgDays"] = -1 },
            ["servers"] = new JsonArray(new JsonObject { ["sName"] = "Sandbox", ["sIP"] = "127.0.0.1", ["iPort"] = _fake.Port, ["iCount"] = 1, ["iMax"] = 1000, ["bOnline"] = 1, ["bUpg"] = 0 }),
        }.ToJsonString());
        _http.Routes["api/char/bank"] = _fake.AnswerBankRequest;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fake.DisposeAsync();
    }

    private AqwSession NewSession(string baseUri = "https://game.aq.com/game/", AqwWebApiOptions? api = null)
    {
        AqwWebApiOptions options = api ?? new AqwWebApiOptions();
        options.BaseUri = new Uri(baseUri);
        AqwSession session = new(new AqwSessionOptions
        {
            Connection = ConnectionTests.Fast(),
            DiscoverClientVersion = false,
            BankSocketTimeout = TimeSpan.FromMilliseconds(500),
        }, new AqwWebApi(options, _http));
        session.Log += (_, e) =>
        {
            lock (_log)
                _log.Add(e.ToString());
        };
        return session;
    }

    private static async Task<AqwSession> InGame(AqwSession session, string password = "pw")
    {
        await session.LoginAsync(new AqwCredentials("hero", password));
        await session.ConnectAsync(session.PickServer());
        await ConnectionTests.Until(() => session.World.CharId == 777);
        return session;
    }

    private string[] Log()
    {
        lock (_log)
            return _log.ToArray();
    }

    private string[] BankRequests() => _fake.BankRequests.Select(r => r ?? "ok").ToArray();

    private void AssertNoTokenLeaked()
    {
        foreach (string token in _fake.IssuedTokens)
            Assert.DoesNotContain(Log(), l => l.Contains(token, StringComparison.Ordinal));
        Assert.DoesNotContain(Log(), l => l.Contains(FakeAqwServer.TokenPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadsOverHttpsWithTheGamesRequestWhenTheSocketIsSilent()
    {
        await using AqwSession session = await InGame(NewSession());
        int seen = 0;
        session.PacketReceived += (_, p) =>
        {
            if (p.Command == "loadBank")
                Interlocked.Increment(ref seen);
        };

        IReadOnlyList<ItemInfo> bank = await session.Commands.LoadBankAsync();

        Assert.Equal("Pixie Wing", Assert.Single(bank).Name);
        Assert.Equal(new[] { "ok" }, BankRequests());
        Assert.True(session.World.IsBankLoaded);
        Assert.Equal(1, seen); // the loadBank Skua's Bank.Load waits for
        Assert.DoesNotContain("loadBank", _fake.LastSession!.Commands);
        Assert.Contains(Log(), l => l.StartsWith("[Info] Bank: loaded over HTTPS (POST api/char/bank", StringComparison.Ordinal)
            && l.Contains("HTTP 200, 1 items received, 1 in the bank", StringComparison.Ordinal) && l.Contains(" ms.", StringComparison.Ordinal));
        AssertNoTokenLeaked();
    }

    [Fact]
    public async Task UsesTheNewestWebLoginsToken()
    {
        await using AqwSession session = await InGame(NewSession());
        // A web login while the socket is up (Skua's relogin starting): the server now only accepts its token.
        await session.LoginAsync(new AqwCredentials("hero", "pw"));
        await session.Commands.LoadBankAsync();
        Assert.Equal(new[] { "ok" }, BankRequests());
    }

    [Fact]
    public async Task IsRequestedOncePerConnectionAndConcurrentLoadsShareOneRequest()
    {
        await using AqwSession session = await InGame(NewSession());
        Task<BankLoadResult>[] loads = Enumerable.Range(0, 4).Select(_ => session.LoadBankAsync()).ToArray();
        BankLoadResult[] results = await Task.WhenAll(loads);
        Assert.All(results, r => Assert.Equal(BankLoadMethod.Http, r.Method));
        BankLoadResult again = await session.LoadBankAsync();
        Assert.Equal(BankLoadMethod.AlreadyLoaded, again.Method);
        Assert.True(again.Loaded);
        Assert.Single(BankRequests());

        // A new connection starts with no bank, as the game's new Avatar does.
        await session.ConnectAsync(session.PickServer());
        await ConnectionTests.Until(() => session.World.CharId == 777);
        Assert.False(session.World.IsBankLoaded);
        Assert.Equal(BankLoadMethod.Http, (await session.LoadBankAsync()).Method);
        Assert.Equal(2, BankRequests().Length);
    }

    [Fact]
    public async Task FallsBackToTheSocketWhenHttpFails()
    {
        _fake.BankHttpStatus = HttpStatusCode.Forbidden;
        _fake.AnswerSocketLoadBank = true;
        await using AqwSession session = await InGame(NewSession());
        BankLoadResult result = await session.LoadBankAsync();
        Assert.Equal(BankLoadMethod.Socket, result.Method);
        Assert.Equal(403, result.HttpStatus);
        Assert.True(session.World.IsBankLoaded);
        Assert.Equal("Pixie Wing", Assert.Single(session.World.GetBank()).Name);
        Assert.Contains("loadBank", _fake.LastSession!.Commands);
        Assert.Contains(Log(), l => l.StartsWith("[Warning] Bank: the HTTPS load (POST api/char/bank) failed after", StringComparison.Ordinal)
            && l.Contains("HTTP 403 (23 bytes)", StringComparison.Ordinal) && l.Contains("Falling back to the socket request loadBank All", StringComparison.Ordinal));
        Assert.Contains(Log(), l => l.StartsWith("[Info] Bank: loaded over the socket (loadBank All fallback): 1 items", StringComparison.Ordinal));
        AssertNoTokenLeaked();
    }

    [Fact]
    public async Task ReportsWhyWhenNeitherWayLoadsIt()
    {
        _fake.BankHttpStatus = HttpStatusCode.InternalServerError;
        await using AqwSession session = await InGame(NewSession());
        TimeoutException e = await Assert.ThrowsAsync<TimeoutException>(() => session.Commands.LoadBankAsync());
        Assert.Contains("HTTP 500", e.Message, StringComparison.Ordinal);
        Assert.Contains("no loadBank reply within", e.Message, StringComparison.Ordinal);
        Assert.False(session.World.IsBankLoaded);
        Assert.Contains(Log(), l => l.StartsWith("[Warning] Bank: not loaded. HTTPS: The bank request returned HTTP 500", StringComparison.Ordinal));
        AssertNoTokenLeaked();
    }

    [Fact]
    public async Task WithoutATokenInMemoryNothingGoesOverHttp()
    {
        _fake.AnswerSocketLoadBank = true;
        await using AqwSession session = await InGame(NewSession());
        session.ForgetCredentials();
        BankLoadResult result = await session.LoadBankAsync();
        Assert.Equal(BankLoadMethod.Socket, result.Method);
        Assert.Empty(BankRequests());
        Assert.Contains("no web login token in memory", result.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.test/game/")]
    [InlineData("http://game.aq.com/game/")]
    [InlineData("https://game.aq.com:8443/game/")]
    [InlineData("https://game.aq.com.evil.test/game/")]
    public async Task TheTokenOnlyGoesToHttpsGameAqCom(string baseUri)
    {
        using AqwWebApi api = new(new AqwWebApiOptions { BaseUri = new Uri(baseUri) }, _http);
        AqwBankException e = await Assert.ThrowsAsync<AqwBankException>(() => api.GetBankAsync(777, FakeAqwServer.Token));
        Assert.StartsWith("Refused to send the login token", e.Message, StringComparison.Ordinal);
        Assert.Equal(0, _http.Hits.GetValueOrDefault("api/char/bank"));
        Assert.DoesNotContain(FakeAqwServer.Token, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRequestIsTheGamesRequest()
    {
        // Checked field by field against the fake's contract (which the session tests pass) and directly here.
        HttpRequestMessage? seen = null;
        string? body = null;
        _http.Routes["api/char/bank"] = r =>
        {
            seen = r;
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttp.Json("[]");
        };
        using AqwWebApi api = new(new AqwWebApiOptions(), _http);
        BankReply reply = await api.GetBankAsync(777, "tok+en/=");
        Assert.Empty(reply.Items);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Matches(@"^https://game\.aq\.com/game/api/char/bank\?v=0\.\d+$", seen.RequestUri!.ToString());
        Assert.Equal("777", Assert.Single(seen.Headers.GetValues("ccid")));
        Assert.Equal("tok+en/=", Assert.Single(seen.Headers.GetValues("token")));
        Assert.Equal("application/x-www-form-urlencoded", seen.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("layout=%7B%22cat%22%3A%22all%22%7D", body);
        Assert.StartsWith("Mozilla/", seen.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APostIsNeverRedirected()
    {
        _http.Routes["api/char/bank"] = _ =>
        {
            HttpResponseMessage r = new(HttpStatusCode.TemporaryRedirect) { Content = new StringContent(string.Empty) };
            r.Headers.Location = new Uri("https://evil.test/steal");
            return r;
        };
        using AqwWebApi api = new(new AqwWebApiOptions(), _http);
        AqwBankException e = await Assert.ThrowsAsync<AqwBankException>(() => api.GetBankAsync(777, FakeAqwServer.Token));
        Assert.Equal(307, e.HttpStatus);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task FailuresNeverCarryTheTokenOrTheBody()
    {
        string token = FakeAqwServer.Token;
        _http.Routes["api/char/bank"] = _ => throw new HttpRequestException($"proxy said no to token {token}");
        using AqwWebApi api = new(new AqwWebApiOptions(), _http);
        AqwBankException e = await Assert.ThrowsAsync<AqwBankException>(() => api.GetBankAsync(777, token));
        Assert.DoesNotContain(token, e.ToString(), StringComparison.Ordinal);
        Assert.Contains("<token>", e.Message, StringComparison.Ordinal);
        Assert.Null(e.InnerException);

        _http.Routes["api/char/bank"] = _ => FakeHttp.Json($$"""{"error":"bad token {{token}}","code":7}""");
        e = await Assert.ThrowsAsync<AqwBankException>(() => api.GetBankAsync(777, token));
        Assert.DoesNotContain(token, e.ToString(), StringComparison.Ordinal);
        Assert.Contains("keys: error, code", e.Message, StringComparison.Ordinal);

        _http.Routes["api/char/bank"] = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent($"<html>{token}</html>") };
        e = await Assert.ThrowsAsync<AqwBankException>(() => api.GetBankAsync(777, token));
        Assert.DoesNotContain(token, e.ToString(), StringComparison.Ordinal);
        Assert.Equal(401, e.HttpStatus);
        Assert.Equal(Encoding.UTF8.GetByteCount($"<html>{token}</html>"), e.BodyBytes);
    }

    [Fact]
    public async Task TheReplyIsBoundedBySize()
    {
        string big = "[" + string.Join(',', Enumerable.Repeat("""{"ItemID":1,"sName":"x"}""", 200)) + "]";
        _http.Routes["api/char/bank"] = _ => FakeHttp.Json(big);
        using AqwWebApi api = new(new AqwWebApiOptions { MaxBankBytes = 1000 }, _http);
        AqwBankException e = await Assert.ThrowsAsync<AqwBankException>(() => api.GetBankAsync(777, FakeAqwServer.Token));
        Assert.Contains("1000 byte limit", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnObjectWrappingTheItemsIsAcceptedAndNamed()
    {
        _http.Routes["api/char/bank"] = _ => FakeHttp.Json("""{"items":[{"ItemID":11,"sName":"Pixie Wing","iQty":2}]}""");
        using AqwWebApi api = new(new AqwWebApiOptions(), _http);
        BankReply reply = await api.GetBankAsync(777, FakeAqwServer.Token);
        Assert.Single(reply.Items);
        Assert.Equal("object.items", reply.Shape);
    }

    [Fact]
    public async Task AWebLoginFinishingAfterForgetCredentialsIsDropped()
    {
        await using AqwSession session = NewSession();
        _http.Delays["api/login/now"] = TimeSpan.FromMilliseconds(300);
        Task<LoginResult> login = session.LoginAsync(new AqwCredentials("hero", "pw"));
        await ConnectionTests.Until(() => _http.Hits.GetValueOrDefault("api/login/now") == 1);
        session.ForgetCredentials();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.Null(session.Account);

        // A login started afterwards is kept as usual.
        _http.Delays.Remove("api/login/now");
        await session.LoginAsync(new AqwCredentials("hero", "pw"));
        Assert.NotNull(session.Account);
    }

    [Fact]
    public async Task ThePasswordIsNotRedactedInPlaceOnTheSocket()
    {
        // The password never goes on the socket; game text that happens to contain it must not be marked as it.
        _fake.LoginMessage = "Welcome to the hunter22 fields";
        await using AqwSession session = await InGame(NewSession(), "hunter22");
        Assert.DoesNotContain(Log(), l => l.Contains("<password>", StringComparison.Ordinal));
        Assert.Contains(Log(), l => l.Contains("Welcome to the hunter22 fields", StringComparison.Ordinal));
    }
}
