using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Skua.App.Droid.Core.GameView;

namespace Skua.App.Droid.Tests.GameView;

public class ServerAllowlistTests
{
    // Shape of https://game.aq.com/game/api/data/servers (2026-11-19, shortened).
    private const string Live = """
        [{"sName":"Twilly","sIP":"sock7.aq.com","iCount":10,"iMax":1000,"bOnline":1,"iChat":2,"bUpg":0,"sLang":"xx","iPort":5593,"iLevel":0,"accessLevel":0},
         {"sName":"Twig","sIP":"sock8.aq.com","iPort":5589},
         {"sName":"Swordhaven (EU)","sIP":"euro.aqw.artix.com","iPort":5588},
         {"sName":"Yokai (SEA)","sIP":"Asia.Game.Artix.com","iPort":"5588"},
         {"sName":"Bad ip","sIP":"172.65.225.46","iPort":5588},
         {"sName":"Bad host","sIP":"evil.example.com","iPort":5588},
         {"sName":"Suffix trick","sIP":"sock7.aq.com.evil.net","iPort":5588},
         {"sName":"Lookalike","sIP":"evilaq.com","iPort":5588},
         {"sName":"Bad port","sIP":"sock7.aq.com","iPort":70000},
         {"sName":"No port","sIP":"sock7.aq.com"}]
        """;

    [Fact]
    public void Parses_the_live_list_and_skips_bad_entries()
    {
        ServerAllowlist a = ServerAllowlist.Parse(Live, out int skipped);
        Assert.Equal(6, skipped);
        Assert.Equal(4, a.Endpoints.Count);
        Assert.True(a.IsAllowed("sock7.aq.com", 5593));
        Assert.True(a.IsAllowed("SOCK7.AQ.COM", 5593));
        Assert.True(a.IsAllowed("asia.game.artix.com", 5588));
        Assert.True(a.IsAllowed("euro.aqw.artix.com", 5588));
    }

    [Theory]
    [InlineData("sock7.aq.com", 5594)]       // listed host, unlisted port
    [InlineData("sock8.aq.com", 5593)]
    [InlineData("sock7.aq.com", 843)]        // policy port: not needed under Ruffle
    [InlineData("game.aq.com", 443)]
    [InlineData("172.65.225.46", 5588)]
    [InlineData("evil.example.com", 5588)]
    [InlineData("sock7.aq.com.evil.net", 5588)]
    [InlineData("127.0.0.1", 5593)]
    [InlineData("localhost", 5593)]
    [InlineData("", 5593)]
    [InlineData("sock7.aq.com", 0)]
    [InlineData("sock7..aq.com", 5593)]
    [InlineData("sock7.aq.com.", 5593)]
    public void Rejects_anything_not_in_the_list(string host, int port)
    {
        ServerAllowlist a = ServerAllowlist.Parse(Live, out _);
        Assert.False(a.IsAllowed(host, port));
    }

    [Fact]
    public void Rejects_a_list_that_is_not_an_array() =>
        Assert.Throws<FormatException>(() => ServerAllowlist.Parse("{\"sIP\":\"sock7.aq.com\"}", out _));
}

