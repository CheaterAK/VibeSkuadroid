using Skua.Core.Headless.Maps;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Headless.World;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace Skua.Core.Headless.Tests;

/// <summary>Hostile input from the server or the web: none of it may hang, exhaust memory, or kill the reader.</summary>
public class SecurityTests
{
    [Fact]
    public void XmlRoomIsReadWithoutQuadraticRegex()
    {
        string hostile = "<" + string.Concat(Enumerable.Repeat("<body ", 50_000));
        Stopwatch sw = Stopwatch.StartNew();
        InboundPacket p = PacketCodec.Parse(hostile);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
        Assert.Equal(PacketKind.Xml, p.Kind);
        Assert.Null(p.Command);

        InboundPacket ok = PacketCodec.Parse("<msg t='sys'><body action='joinOK' r='42'><pid id='0'/></body></msg>");
        Assert.Equal("joinOK", ok.Command);
        Assert.Equal(42, ok.Room);
        // Malformed XML still yields the action through the linear fallback.
        Assert.Equal("apiOK", PacketCodec.Parse("<msg t='sys'><body action='apiOK' r='0'>").Command);
    }

    [Fact]
    public void XmlEntitiesAreNotExpanded()
    {
        string laughs = "<?xml version='1.0'?><!DOCTYPE m [<!ENTITY a 'aaaaaaaaaa'><!ENTITY b '&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;'>"
            + "<!ENTITY c '&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;'><!ENTITY d '&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;'>]>"
            + "<msg t='sys'><body action='uER' r='1'><u i='5'><n>&d;</n></u></body></msg>";
        InboundPacket p = PacketCodec.Parse(laughs);
        Assert.Null(p.XmlUserName);
        Assert.Null(p.XmlUserId);
    }

    [Fact]
    public void DuplicateJsonKeysAreAnUndecodablePacket()
    {
        InboundPacket p = PacketCodec.Parse("""{"t":"xt","b":{"r":-1,"o":{"cmd":"x","cmd":"y"}}}""");
        Assert.Equal(PacketKind.Unknown, p.Kind);
        Assert.NotNull(p.DecodeError);
    }

    [Fact]
    public async Task ADuplicateKeyPacketDoesNotStopTheReader()
    {
        await using FakeAqwServer fake = new();
        await using AqwConnection c = new(ConnectionTests.Fast());
        await c.ConnectAsync(fake.Server, "Hero", FakeAqwServer.Token, "5.32");
        fake.LastSession!.Raw("""{"t":"xt","b":{"r":-1,"o":{"cmd":"x","items":{"a":1,"a":2}}}}""");
        fake.LastSession.Str("server", "still alive");
        await ConnectionTests.Until(() => c.World.GetRecentMessages().Any(m => m.Text.Contains("still alive", StringComparison.Ordinal)));
        Assert.True(c.IsReady);
        Assert.True(c.World.GetPacketCounters().Unhandled.ContainsKey("undecodable") || c.World.GetPacketCounters().Unhandled.ContainsKey("unknown"));
    }

    [Fact]
    public void PacketCountersAreBoundedAndClearedOnReset()
    {
        WorldModel w = new();
        for (int i = 0; i < 2000; i++)
            w.Apply(Pkt.Xt("cmd" + i, "x"));
        PacketCounters c = w.GetPacketCounters();
        Assert.True(c.Unhandled.Count <= WorldModel.MaxCounterKeys);
        Assert.True(c.UnhandledSamples.Count <= WorldModel.MaxCounterKeys);
        Assert.Equal(2000 - (WorldModel.MaxCounterKeys - 1), c.Unhandled[WorldModel.OverflowCounterKey]);
        Assert.Equal(2000, c.Total);
        w.Reset();
        Assert.Empty(w.GetPacketCounters().Unhandled);
    }

    [Fact]
    public void GameSwfInflationIsBounded()
    {
        // 10 MB of zeros declared as a 1 KB file: refused without inflating it all.
        byte[] payload = new byte[10 * 1024 * 1024];
        Encoding.Latin1.GetBytes("\u0006vParam\u00045.99").CopyTo(payload, payload.Length - 20);
        using MemoryStream file = new();
        file.Write("CWS\u000a"u8);
        file.Write(BitConverter.GetBytes(1024u));
        using (ZLibStream z = new(file, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(payload);
        Assert.Null(ClientVersion.FromSwf(file.ToArray()));

        // Declared honestly it is read.
        byte[] honest = file.ToArray();
        BitConverter.GetBytes((uint)(payload.Length + 8)).CopyTo(honest, 4);
        Assert.Equal("5.99", ClientVersion.FromSwf(honest));
    }

    [Fact]
    public void MapSwfInflationIsBoundedByItsHeaderAndTheCap()
    {
        // A map whose body inflates past the length its header declares is refused, and the cache reports it.
        byte[] map = SyntheticSwf.Map([("Enter", ["Spawn"])], "CWS");
        byte[] lying = (byte[])map.Clone();
        BitConverter.GetBytes(16u).CopyTo(lying, 4);
        Assert.Throws<SwfFormatException>(() => SwfReader.Read(lying));
        Assert.StartsWith("error:", MapCellCache.FromSwf("x.swf", lying).Source);

        // Declared honestly it is read.
        Assert.Equal(["Enter"], SwfReader.Read(map).Labels.Select(l => l.Name));

        // A claim of 4 GB does not lift the cap: a body just over it is refused without being kept whole.
        byte[] payload = new byte[SwfReader.MaxInflatedBytes + 1];
        using MemoryStream file = new();
        file.Write("CWS\u000a"u8);
        file.Write(BitConverter.GetBytes(uint.MaxValue));
        using (ZLibStream z = new(file, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(payload);
        Assert.Throws<SwfFormatException>(() => SwfReader.Decompress(file.ToArray(), out _, out _));

        // A header too small to count (no length) leaves the cap alone in force; a small body still reads.
        byte[] noLength = (byte[])map.Clone();
        BitConverter.GetBytes(0u).CopyTo(noLength, 4);
        Assert.Equal(["Enter"], SwfReader.Read(noLength).Labels.Select(l => l.Name));
    }

    [Fact]
    public async Task LoginIsNeverRedirectedAndGetsOnlyFollowSameHost()
    {
        FakeHttp http = new();
        http.Routes["api/login/now"] = _ =>
        {
            HttpResponseMessage r = new(HttpStatusCode.TemporaryRedirect);
            r.Headers.Location = new Uri("https://elsewhere.example/steal");
            return r;
        };
        http.Routes["api/data/gameversion"] = _ =>
        {
            HttpResponseMessage r = new(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://game.aq.com/game/api/data/gameversion2");
            return r;
        };
        http.Routes["api/data/gameversion2"] = _ => FakeHttp.Json("""{"sFile":"G.swf"}""");
        http.Routes["api/data/servers"] = _ =>
        {
            HttpResponseMessage r = new(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://elsewhere.example/servers");
            return r;
        };
        using AqwWebApi api = new(new AqwWebApiOptions(), http);
        await Assert.ThrowsAsync<AqwLoginException>(() => api.LoginAsync("u", "p"));
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.Host == "elsewhere.example");
        Assert.Equal("G.swf", (await api.GetGameVersionAsync()).GameFile);
        await Assert.ThrowsAsync<AqwLoginException>(() => api.GetServersAsync());
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.Host == "elsewhere.example");
    }

    [Fact]
    public async Task AGameFileThatNeverEndsTimesOut()
    {
        FakeHttp http = new();
        http.Routes["gamefiles/slow.swf"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) };
        using AqwWebApi api = new(new AqwWebApiOptions { DownloadTimeout = TimeSpan.FromMilliseconds(300) }, http);
        Stopwatch sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<AqwLoginException>(() => api.DownloadGameFileAsync("slow.swf"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    // Sends one byte, then nothing until cancelled.
    private sealed class EndlessStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[0] = (byte)'C';
                return 1;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
