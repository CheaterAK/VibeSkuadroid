using Skua.Core.Headless.Client;
using Skua.Core.Headless.Maps;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Headless.World;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Tests;

/// <summary>Where the avatar stands and how it walks (World.updateUserDisplay, AvatarMC.walkTo, World.moveRequest/pushMove).</summary>
public class PositionTests
{
    private static readonly (string, string[])[] s_cells =
    {
        ("Enter", new[] { "Spawn", "Left" }),
        ("r2", new[] { "Left", "Right" }),
        ("r3", Array.Empty<string>()),
    };

    private static (int X, int Y) PadAt(string cell, string pad) => (cell, pad) switch
    {
        ("Enter", "Spawn") => (470, 430),
        ("Enter", "Left") => (110, 360),
        ("r2", "Left") => (-20, 400),   // pads can sit off stage
        ("r2", "Right") => (840, 380),
        _ => (0, 0),
    };

    [Fact]
    public void TheMapReaderGivesEachPadItsStagePosition()
    {
        foreach (string compression in new[] { "FWS", "CWS", "ZWS" })
        {
            SwfTimeline t = SwfReader.Read(SyntheticSwf.Map(s_cells, compression, padAt: PadAt));
            Assert.Equal(new PadPosition(470, 430), t.PadPositions["Enter"]["Spawn"]);
            Assert.Equal(new PadPosition(110, 360), t.PadPositions["Enter"]["Left"]);
            Assert.Equal(new PadPosition(-20, 400), t.PadPositions["r2"]["Left"]);
            Assert.Equal(new PadPosition(840, 380), t.PadPositions["r2"]["Right"]);
            Assert.Empty(t.PadPositions["r3"]);
        }
    }

    [Fact]
    public async Task PadPositionsSurviveTheDiskCacheAndOldEntriesAreReadAgain()
    {
        string dir = Path.Combine(Path.GetTempPath(), "skua-padpos-" + Guid.NewGuid().ToString("N"));
        int downloads = 0;
        Task<byte[]> Download(string file, CancellationToken ct)
        {
            downloads++;
            return Task.FromResult(SyntheticSwf.Map(s_cells, padAt: PadAt));
        }
        try
        {
            Assert.Equal(new PadPosition(840, 380), (await new MapCellCache(Download, dir).GetAsync("test/map.swf")).PadPositions["r2"]["Right"]);
            MapCells cached = await new MapCellCache(Download, dir).GetAsync("test/map.swf");
            Assert.Equal("cache", cached.Source);
            Assert.Equal(new PadPosition(-20, 400), cached.PadPositions["r2"]["Left"]);
            Assert.Equal(1, downloads);

            // An entry from before positions were kept: read from the map file again.
            string file = Directory.GetFiles(dir).Single();
            JsonObject old = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
            old.Remove("padPositions");
            File.WriteAllText(file, old.ToJsonString());
            Assert.Equal("swf", (await new MapCellCache(Download, dir).GetAsync("test/map.swf")).Source);
            Assert.Equal(2, downloads);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<(GameClient Client, WorldModel World, RecordingSender Sender, ManualTime Time)> InMap()
    {
        ManualTime time = new();
        WorldModel w = WorldModelTests.LoggedIn();
        RecordingSender sender = new();
        MapCellCache maps = new((_, _) => Task.FromResult(SyntheticSwf.Map(s_cells, padAt: PadAt)), null);
        GameClient c = new(w, sender, time, maps, new GameClientOptions { MapLoadTime = TimeSpan.FromMilliseconds(500) });
        InboundPacket area = Pkt.Json("""{"cmd":"moveToArea","areaId":902,"areaName":"other-902","strMapName":"other","strMapFileName":"x/other.swf","uoBranch":[{"uoName":"hero","strUsername":"Hero","entID":40001,"intHP":10,"intHPMax":10,"intState":1,"strFrame":"Enter","strPad":"Spawn","tx":0,"ty":0}],"monBranch":[],"mondef":[],"monmap":[]}""");
        w.Apply(area);
        c.OnInbound(area);
        for (int i = 0; i < 100 && c.Cells is null; i++)
            await Task.Delay(10);
        time.Advance(TimeSpan.FromSeconds(1));   // the map has loaded
        return (c, w, sender, time);
    }

    private static void Near((int X, int Y) actual, int x, int y)
    {
        Assert.InRange(actual.X, x - 5, x + 4);
        Assert.InRange(actual.Y, y - 5, y + 4);
    }

    [Fact]
    public async Task EnteringACellPlacesTheAvatarAtItsPad()
    {
        (GameClient c, WorldModel w, _, _) = await InMap();
        Near(c.Position, 470, 430);
        (int X, int Y) first = c.Position;
        Assert.Equal(first, c.Position);   // placed once, not on every read

        c.MoveToCell("r2", "Right");
        Near(c.Position, 840, 380);
        c.MoveToCell("r3", "Left");        // no such pad: the stage centre
        Assert.Equal((GameClient.StageCenterX, GameClient.StageCenterY), c.Position);
        Assert.Equal("0", w.GetGameObject("world.myAvatar.dataLeaf.tx"));
    }

    [Fact]
    public async Task WalkingTakesTheClientsTimeAndTheServerHearsAtMostOneMovePer500Ms()
    {
        (GameClient c, WorldModel w, RecordingSender s, ManualTime time) = await InMap();
        c.MoveToCell("r2", "Right");
        (int X, int Y) start = c.Position;

        // 176 px at speed 8: round(1000 * 176 / (8 * 22)) = 1000 ms.
        c.WalkTo(start.X - 176, start.Y, 8);
        Assert.Equal($"%xt%zm%mv%902%{start.X - 176}%{start.Y}%8%", Assert.Single(s.Of("mv")));
        Assert.Equal((start.X - 176).ToString(), w.GetGameObject("world.myAvatar.dataLeaf.tx"));
        Assert.True(c.IsWalking);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal((start.X - 88, start.Y), c.Position);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal((start.X - 176, start.Y), c.Position);
        Assert.False(c.IsWalking);

        // Two requests close together: the first goes out, the newest of the rest when the move timer fires. The packet
        // carries WALKSPEED whatever speed the clip walks at (World.pushMove sends World.speed).
        s.Clear();
        c.WalkSpeed = 9;
        c.WalkTo(100, 300, 20);
        time.Advance(TimeSpan.FromMilliseconds(100));
        c.WalkTo(200, 300, 20);
        c.WalkTo(300, 310, 20);
        Assert.Equal(new[] { "%xt%zm%mv%902%100%300%9%" }, s.Of("mv"));
        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(new[] { "%xt%zm%mv%902%100%300%9%", "%xt%zm%mv%902%300%310%9%" }, s.Of("mv"));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal((300, 310), c.Position);
        Assert.Equal(2, s.Of("mv").Count);

        // A walk's end survives reading; a new cell places the avatar again.
        c.MoveToCell("Enter", "Left");
        Near(c.Position, 110, 360);
    }

    [Fact]
    public async Task NoMovePacketWhileTheMapLoads()
    {
        (GameClient c, WorldModel w, RecordingSender s, ManualTime time) = await InMap();
        InboundPacket area = Pkt.Json("""{"cmd":"moveToArea","areaId":903,"areaName":"other-903","strMapName":"other","strMapFileName":"x/other.swf","uoBranch":[],"monBranch":[],"mondef":[],"monmap":[]}""");
        w.Apply(area);
        c.OnInbound(area);
        c.WalkTo(10, 20, 8);
        Assert.Empty(s.Of("mv"));                       // bitWalk is off until the cell is shown
        Assert.Equal("10", w.GetGameObject("world.myAvatar.dataLeaf.tx"));
        time.Advance(TimeSpan.FromSeconds(2));
        c.WalkTo(30, 40, 8);
        Assert.Equal("%xt%zm%mv%903%30%40%8%", Assert.Single(s.Of("mv")));
    }
}
