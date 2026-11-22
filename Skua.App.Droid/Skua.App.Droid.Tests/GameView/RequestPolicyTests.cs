using Skua.App.Droid.Core.GameView;

namespace Skua.App.Droid.Tests.GameView;

public class RequestPolicyTests
{
    [Theory]
    [InlineData("https://game.aq.com/__vibeskua__/index.html", "index.html")]
    [InlineData("https://game.aq.com/__vibeskua__/", "index.html")]
    [InlineData("https://GAME.aq.com/__vibeskua__/gameview.js", "gameview.js")]
    [InlineData("https://game.aq.com:443/__vibeskua__/config.json", "config.json")]
    [InlineData("https://game.aq.com/__vibeskua__/ruffle-0.7.1/172de42e6619eac8f371.wasm", "ruffle-0.7.1/172de42e6619eac8f371.wasm")]
    [InlineData("https://game.aq.com/__vibeskua__/skua.swf?x=1", "skua.swf")]
    public void Serves_bundled_files_under_the_reserved_path(string url, string local)
    {
        RequestDecision d = RequestPolicy.Decide(url);
        Assert.Equal(RequestAction.ServeLocal, d.Action);
        Assert.Equal(local, d.LocalPath);
    }

    [Theory]
    [InlineData("https://game.aq.com/game/api/data/gameversion")]
    [InlineData("https://game.aq.com/game/api/login/now?ran=0.123")]
    [InlineData("https://game.aq.com/game/gamefiles/Game4000r9.swf?ver=0.5")]
    [InlineData("https://game.aq.com/game/gamefiles/maps/battleon.swf")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("blob:https://game.aq.com/1234")]
    public void Lets_the_game_files_and_api_through(string url) =>
        Assert.Equal(RequestAction.PassThrough, RequestPolicy.Decide(url).Action);

    [Theory]
    [InlineData("http://game.aq.com/game/gamefiles/Game4000r9.swf")]      // plain http
    [InlineData("https://game.aq.com:8443/game/x")]                       // other port
    [InlineData("https://evil.game.aq.com/game/x")]                       // subdomain
    [InlineData("https://game.aq.com.evil.net/game/x")]                   // suffix trick
    [InlineData("https://content.aq.com/game/x")]                         // other aq host
    [InlineData("https://www.google-analytics.com/analytics.js")]
    [InlineData("https://game.aq.com/")]                                  // other paths on the host
    [InlineData("https://game.aq.com/gamex/y")]
    [InlineData("https://user:pw@game.aq.com/game/x")]                    // user info
    [InlineData("https://game.aq.com/__vibeskua__/%2e%2e/x")]             // encoded traversal
    [InlineData("https://game.aq.com/__vibeskua__/a%2Fb")]                // encoded slash
    [InlineData("https://game.aq.com/__vibeskua__/a//b")]                 // empty segment
    [InlineData("file:///data/data/x")]
    [InlineData("content://x/y")]
    [InlineData("ws://127.0.0.1:1234/x")]
    [InlineData("not a url")]
    public void Blocks_everything_else(string url) =>
        Assert.Equal(RequestAction.Block, RequestPolicy.Decide(url).Action);

    [Fact]
    public void Dot_segments_are_resolved_before_the_decision_and_never_reach_the_local_files()
    {
        // System.Uri removes "/../" before the check: this one becomes /game/x on the network, not a local file.
        RequestDecision d = RequestPolicy.Decide("https://game.aq.com/__vibeskua__/../game/x");
        Assert.Equal(RequestAction.PassThrough, d.Action);
        Assert.Null(RequestPolicy.LocalAssetPath("../x"));
        Assert.Null(RequestPolicy.LocalAssetPath("a/./b"));
        Assert.Null(RequestPolicy.LocalAssetPath("a/../b"));
    }

    [Fact]
    public void Block_keys_carry_no_query_or_deep_path()
    {
        Assert.Equal("https://www.example.com/a", RequestPolicy.BlockKey("https://www.example.com/a/b/c?token=x"));
        Assert.Equal("data:", RequestPolicy.BlockKey("data:text/plain,hello"));
    }

    [Theory]
    [InlineData("x.wasm", "application/wasm")]
    [InlineData("index.html", "text/html")]
    [InlineData("a/ruffle.js", "text/javascript")]
    [InlineData("skua.swf", "application/x-shockwave-flash")]
    public void Mime_types(string path, string mime) => Assert.Equal(mime, RequestPolicy.MimeType(path));
}
