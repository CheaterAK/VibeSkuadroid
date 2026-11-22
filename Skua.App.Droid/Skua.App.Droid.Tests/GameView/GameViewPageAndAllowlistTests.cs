using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Skua.App.Droid.Core.GameView;

namespace Skua.App.Droid.Tests.GameView;

/// <summary>The extra request the client needs (quest tracking on www.aq.com) and nothing else from that host.</summary>
public class ExtraRequestAllowlistTests
{
    [Theory]
    [InlineData("https://www.aq.com/game/quest.asp?userid=12345678&qvalue=3", "GET")]
    [InlineData("https://www.aq.com/game/quest.asp?userid=0&qvalue=1", "GET")]
    [InlineData("https://WWW.aq.com/game/quest.asp?userid=1&qvalue=7", "OPTIONS")]
    public void QuestTrackingIsFetchedByTheApp(string url, string method)
    {
        RequestDecision d = RequestPolicy.Decide(url, method);
        Assert.Equal(RequestAction.AppFetch, d.Action);
        Assert.Equal("https", d.FetchUri!.Scheme);
        Assert.Equal("www.aq.com", d.FetchUri.Host);
        Assert.Equal("/game/quest.asp", d.FetchUri.AbsolutePath);
    }

    [Theory]
    [InlineData("https://www.aq.com/game/quest.asp?userid=1&qvalue=1", "POST")]          // GET only
    [InlineData("http://www.aq.com/game/quest.asp?userid=1&qvalue=1", "GET")]            // https only
    [InlineData("https://www.aq.com:8443/game/quest.asp?userid=1&qvalue=1", "GET")]      // default port only
    [InlineData("https://www.aq.com/game/quest.asp", "GET")]                             // no query
    [InlineData("https://www.aq.com/game/quest.asp?userid=1", "GET")]
    [InlineData("https://www.aq.com/game/quest.asp?qvalue=1&userid=1", "GET")]           // the client's order only
    [InlineData("https://www.aq.com/game/quest.asp?userid=1&qvalue=1&x=2", "GET")]       // nothing extra
    [InlineData("https://www.aq.com/game/quest.asp?userid=abc&qvalue=1", "GET")]         // digits only
    [InlineData("https://www.aq.com/game/quest.asp?userid=1&qvalue=1234", "GET")]
    [InlineData("https://www.aq.com/game/quest.asp?userid=1234567890123&qvalue=1", "GET")]
    [InlineData("https://www.aq.com/game/quest.aspx?userid=1&qvalue=1", "GET")]
    [InlineData("https://www.aq.com/game/../game/quest.asp?userid=1&qvalue=1&a=b", "GET")]
    [InlineData("https://user@www.aq.com/game/quest.asp?userid=1&qvalue=1", "GET")]
    [InlineData("https://www.aq.com/gamedesignnotes/", "GET")]                            // test client only
    [InlineData("https://www.aq.com/game/", "GET")]
    [InlineData("https://content.aq.com/game/gamefiles/x.swf", "GET")]
    [InlineData("https://graph.facebook.com/me?access_token=x", "GET")]
    [InlineData("https://account.aq.com/CharPage?id=x", "GET")]
    public void EverythingElseOnOtherHostsStaysBlocked(string url, string method) =>
        Assert.Equal(RequestAction.Block, RequestPolicy.Decide(url, method).Action);

    [Fact]
    public void TheGameHostRulesAreUnchanged()
    {
        Assert.Equal(RequestAction.PassThrough, RequestPolicy.Decide("https://game.aq.com/game/api/login/now", "POST").Action);
        Assert.Equal(RequestAction.ServeLocal, RequestPolicy.Decide("https://game.aq.com/__vibeskua__/gameview.js", "GET").Action);
        Assert.Equal(RequestAction.Block, RequestPolicy.Decide("https://game.aq.com/game2/x", "GET").Action);
    }

    [Fact]
    public async Task TheAppFetchSendsNoCookiesCapsTheAnswerAndNeverThrows()
    {
        HttpRequestMessage? seen = null;
        FakeHttp http = new();
        http.Routes["quest.asp"] = r =>
        {
            seen = r;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[GameApi.MaxFetchBytes * 2]) };
        };
        using HttpClient client = new(http);
        Uri uri = RequestPolicy.Decide("https://www.aq.com/game/quest.asp?userid=7&qvalue=4").FetchUri!;
        (int status, _, byte[] body) = await GameApi.FetchAllowedAsync(client, uri, CancellationToken.None);
        Assert.Equal(200, status);
        Assert.Equal(GameApi.MaxFetchBytes, body.Length);
        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.False(seen.Headers.Contains("Cookie"));
        Assert.Equal("https://www.aq.com/game/quest.asp?userid=7&qvalue=4", seen.RequestUri!.AbsoluteUri);

        http.Routes["quest.asp"] = _ => throw new HttpRequestException("down");
        Assert.Equal(502, (await GameApi.FetchAllowedAsync(client, uri, CancellationToken.None)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => GameApi.FetchAllowedAsync(client, new Uri("https://www.aq.com/x"), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    [InlineData((HttpStatusCode)399)]
    public async Task ARedirectIsNotFollowedAndAnswers502WithoutABody(HttpStatusCode code)
    {
        FakeHttp http = new();
        http.Routes["quest.asp"] = _ =>
        {
            HttpResponseMessage r = new(code) { Content = new StringContent("secret page body") };
            r.Headers.Location = new Uri("https://evil.example/secret");
            return r;
        };
        using HttpClient client = new(http);
        Uri uri = RequestPolicy.Decide("https://www.aq.com/game/quest.asp?userid=7&qvalue=4").FetchUri!;
        (int status, _, byte[] body) = await GameApi.FetchAllowedAsync(client, uri, CancellationToken.None);
        Assert.Equal(502, status);
        Assert.Empty(body);
        Assert.Equal(["quest.asp"], http.Requests); // nothing else was requested
    }

    [Fact]
    public async Task AnAnswerForAnotherUrlIsRefusedEvenWhenAHandlerFollowedARedirect()
    {
        FakeHttp http = new();
        http.Routes["quest.asp"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("secret page body"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/secret"),
        };
        using HttpClient client = new(http);
        Uri uri = RequestPolicy.Decide("https://www.aq.com/game/quest.asp?userid=7&qvalue=4").FetchUri!;
        (int status, _, byte[] body) = await GameApi.FetchAllowedAsync(client, uri, CancellationToken.None);
        Assert.Equal(502, status);
        Assert.Empty(body);
    }

    [Fact]
    public async Task TheGameViewsClientNeverFollowsRedirects()
    {
        using HttpClientHandler handler = GameApi.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);

        // End to end against a local server that redirects: the client sees the 302 itself.
        using System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int requests = 0;
        Task server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using System.Net.Sockets.TcpClient c = await listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref requests);
                    using System.Net.Sockets.NetworkStream s = c.GetStream();
                    byte[] buf = new byte[4096];
                    _ = await s.ReadAsync(buf);
                    byte[] answer = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/followed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await s.WriteAsync(answer);
                }
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or ObjectDisposedException or IOException)
            {
                // stopped
            }
        });
        HttpClientHandler local = GameApi.CreateHandler();
        local.UseProxy = false; // the test machine's proxy must not see the loopback request
        using HttpClient client = new(local);
        using HttpResponseMessage res = await client.GetAsync($"http://127.0.0.1:{port}/start");
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal(1, Volatile.Read(ref requests));
        listener.Stop();
        await server;
    }

    [Fact]
    public async Task TheServerListIsReadUpToItsCap()
    {
        FakeHttp http = new();
        http.Routes["api/data/servers"] = _ => FakeHttp.Json("[" + new string(' ', GameApi.MaxServerListBytes) + "]");
        using HttpClient client = new(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => GameApi.FetchServerListAsync(client, CancellationToken.None));
        http.Routes["api/data/servers"] = _ => FakeHttp.Json("[]");
        Assert.Empty((await GameApi.FetchServerListAsync(client, CancellationToken.None)).Endpoints);
    }

    [Fact]
    public async Task ReadCappedReadsExactlyUpToTheCap()
    {
        byte[] data = new byte[300_000];
        Assert.Equal(1000, (await GameApi.ReadCappedAsync(new MemoryStream(data), 1000, truncate: true, CancellationToken.None)).Length);
        Assert.Equal(300_000, (await GameApi.ReadCappedAsync(new MemoryStream(data), 300_000, truncate: false, CancellationToken.None)).Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => GameApi.ReadCappedAsync(new MemoryStream(data), 299_999, truncate: false, CancellationToken.None));
        Assert.Empty(await GameApi.ReadCappedAsync(new MemoryStream(), 10, truncate: false, CancellationToken.None));
    }
}

/// <summary>Whatever the page sends, the recorder's lists stay bounded.</summary>
public class GameViewRecorderCapTests
{
    [Fact]
    public void PageRecordsCannotGrowTheRecorderWithoutBound()
    {
        GameViewRecorder rec = new(() => 1.0);
        string big = new('a', 2 * GameViewRecorder.MaxHeaderValue);
        for (int i = 0; i < 5_000; i++)
        {
            rec.Console($"[vgame] {{\"type\":\"fps\",\"fps\":{i % 60}}}", "LOG");
            rec.Console("[vgame] {\"type\":\"mem\",\"wasmMB\":12}", "LOG");
            rec.Console($"[vgame] {{\"type\":\"config\",\"x\":\"{big}\"}}", "LOG");
            rec.Console($"[vgame] {{\"type\":\"mark\",\"name\":\"m{i}\"}}", "LOG");
            rec.Blocked($"https://h{i}.example/x");
            rec.Header("h" + i, "v");
            rec.NativeMemory(new NativeMemorySample(1, 1, 1, 1, 1, false));
        }
        (int headers, int fps, int pageMem, int nativeMem, int marks, int blocked) = rec.Counts();
        Assert.InRange(headers, 1, GameViewRecorder.MaxHeaders);
        Assert.InRange(fps, 1, GameViewRecorder.MaxSamples);
        Assert.InRange(pageMem, 1, GameViewRecorder.MaxSamples);
        Assert.InRange(nativeMem, 1, GameViewRecorder.MaxSamples);
        Assert.InRange(marks, 1, GameViewRecorder.MaxMarks);
        Assert.InRange(blocked, 1, GameViewRecorder.MaxBlockedKeys + 1);
        // The newest samples are kept (the HUD shows the last one), and a milestone still counts after the cap.
        Assert.Contains("19.0 fps", rec.Hud()); // 4999 % 60
        rec.Mark("login_screen");
        Assert.True(rec.HasMark("login_screen"));
        Assert.True(rec.BuildReport().Length < 2_000_000);
    }
}

/// <summary>The page's Content-Security-Policy and the service worker rules.</summary>
public class GameViewCspAndServiceWorkerTests
{
    private static Dictionary<string, string> Directives(string csp) =>
        csp.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(d => d.Split(' ')[0], d => d[(d.IndexOf(' ') + 1)..]);

    [Fact]
    public void EveryLocalResponseCarriesThePolicy()
    {
        Dictionary<string, string> headers = RequestPolicy.LocalResponseHeaders();
        Assert.Equal(RequestPolicy.ContentSecurityPolicy, headers["Content-Security-Policy"]);
        Assert.Equal("no-store", headers["Cache-Control"]);
        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
    }

    [Fact]
    public void ThePolicyAllowsOnlyTheGameTheQuestTrackingAndTheBridge()
    {
        Dictionary<string, string> d = Directives(RequestPolicy.ContentSecurityPolicy);
        Assert.Equal("'none'", d["default-src"]);
        Assert.Equal(["https://game.aq.com", "https://www.aq.com/game/quest.asp", "wss://vibeskua-bridge.invalid"], d["connect-src"].Split(' '));
        Assert.Equal("'none'", d["worker-src"]);
        Assert.Equal("'none'", d["frame-src"]);
        Assert.Equal("'none'", d["child-src"]);
        Assert.Equal("'none'", d["object-src"]);
        Assert.Equal("'none'", d["base-uri"]);
        Assert.Equal("'none'", d["form-action"]);
        Assert.Equal("'self' 'unsafe-eval' 'wasm-unsafe-eval'", d["script-src"]);
        // Nothing broader than the page's own origin and local data anywhere.
        foreach (string value in d.Values)
        {
            Assert.DoesNotContain("*", value);
            Assert.DoesNotContain("http:", value);
            Assert.DoesNotContain("https: ", value + " ");
            Assert.DoesNotContain("wss: ", value + " ");
        }
        // The bridge URLs the page is given are covered.
        Assert.StartsWith(d["connect-src"].Split(' ')[2] + "/", ChannelBridge.UrlPrefix);
    }

    [Fact]
    public void AServiceWorkerScriptIsNeverLoaded()
    {
        Dictionary<string, string> sw = new() { ["Service-Worker"] = "script" };
        Dictionary<string, string> swLower = new() { ["service-worker"] = "script" };
        Assert.Equal(RequestAction.Block, RequestPolicy.Decide("https://game.aq.com/game/sw.js", "GET", sw).Action);
        Assert.Equal(RequestAction.Block, RequestPolicy.Decide("https://game.aq.com/game/sw.js", "GET", swLower).Action);
        Assert.Equal(RequestAction.Block, RequestPolicy.DecideServiceWorker("https://game.aq.com/game/sw.js", "GET", sw).Action);
        Assert.Equal(RequestAction.PassThrough, RequestPolicy.Decide("https://game.aq.com/game/sw.js", "GET", new Dictionary<string, string> { ["Accept"] = "*/*" }).Action);
        Assert.Equal(RequestAction.PassThrough, RequestPolicy.Decide("https://game.aq.com/game/x", "GET", null).Action);
    }

    [Fact]
    public void AServiceWorkerGetsThePagesRulesButNothingTheAppFetches()
    {
        Assert.Equal(RequestAction.Block, RequestPolicy.DecideServiceWorker("https://www.aq.com/game/quest.asp?userid=1&qvalue=1", "GET", null).Action);
        Assert.Equal(RequestAction.Block, RequestPolicy.DecideServiceWorker("https://example.com/", "GET", null).Action);
        Assert.Equal(RequestAction.Block, RequestPolicy.DecideServiceWorker("https://game.aq.com/", "GET", null).Action);
        Assert.Equal(RequestAction.PassThrough, RequestPolicy.DecideServiceWorker("https://game.aq.com/game/x", "GET", null).Action);
        Assert.Equal(RequestAction.ServeLocal, RequestPolicy.DecideServiceWorker("https://game.aq.com/__vibeskua__/gameview.js", "GET", null).Action);
    }
}

/// <summary>
/// The page script itself (gameview.js) run in Node against a fake Ruffle player that answers skua.swf's callbacks
/// like the real client's objects: the login screen, auto-login through the game's own login, and "in game" from
/// the signals the decompiled client sets (sfc.isConnected, world.myAvatar, world.mapLoadInProgress; or the
/// moveToArea packet alone when reading game objects fails). Skipped where Node is not installed.
/// </summary>
public class GameViewPageTests
{
    private static string? Node()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(dir, "node");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static JsonElement Run(string scenario)
    {
        string? node = Node();
        Assert.SkipWhen(node is null, "Node.js is not installed.");
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Skua.App.Droid", "Skua.App.Droid.csproj")))
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("Skua.App.Droid.csproj not found.");
        string script = Path.Combine(dir, "Skua.App.Droid", "GameView", "web", "gameview.js");
        string harness = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gameview-harness.cjs");
        ProcessStartInfo psi = new(node!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(harness);
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(scenario);
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(20000));
        Assert.True(p.ExitCode == 0, error);
        return JsonDocument.Parse(output).RootElement;
    }

    private static List<string> Types(JsonElement run) =>
        run.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("type").GetString() + (r.TryGetProperty("name", out JsonElement n) ? ":" + n.GetString() : string.Empty)).ToList();

    [Fact]
    public void LoginScreenAutoLoginAndInGameAreRecognised()
    {
        JsonElement run = Run("login-and-poll");
        List<string> types = Types(run);
        Assert.Contains("mark:login_screen", types);
        Assert.Contains("autologin_sent", types);
        // In game only once the map finished loading, then left when the socket closed.
        int inGame = types.IndexOf("mark:in_game");
        Assert.True(inGame > types.IndexOf("autologin_sent"));
        Assert.Contains("stage:in_game", types);
        Assert.Contains("stage:left_game", types);
        Assert.Contains("modules_applied", types);
        // The game's own login got the handed-over login; no record carries it.
        Assert.Contains(run.GetProperty("calls").EnumerateArray(), c => c.ToString() == "[\"callGameFunction\",\"login\",\"hero\",\"hunter2-secret\"]");
        Assert.DoesNotContain("hunter2-secret", run.GetProperty("records").ToString());
        // Hide players and disable FX on, kill lag off (the defaults).
        Assert.Contains(run.GetProperty("calls").EnumerateArray(), c => c.ToString() == "[\"modEnable\",\"HidePlayers\"]");
        Assert.DoesNotContain(run.GetProperty("calls").EnumerateArray(), c => c[0].GetString() == "killLag");
    }

    [Fact]
    public void TheWebSocketShimNeverHandsOutTheBrowsersConstructor()
    {
        JsonElement shim = Run("login-and-poll").GetProperty("shim");
        Assert.True(shim.GetProperty("patched").GetBoolean());
        Assert.True(shim.GetProperty("constructorIsPatched").GetBoolean());       // WebSocket.prototype.constructor
        Assert.False(shim.GetProperty("realReachable").GetBoolean());
        Assert.True(shim.GetProperty("otherRefused").GetBoolean());
        Assert.True(shim.GetProperty("bridgeIsInstance").GetBoolean());
        Assert.True(shim.GetProperty("bridgeConstructorIsPatched").GetBoolean());
        Assert.False(shim.GetProperty("constructorWritable").GetBoolean());
        Assert.True(shim.GetProperty("rtcRemoved").GetBoolean());
    }

    [Fact]
    public void TheJoinPacketAloneCountsWhenGameObjectsCannotBeRead()
    {
        List<string> types = Types(Run("packets-only"));
        Assert.Contains("mark:in_game", types);
    }
}
