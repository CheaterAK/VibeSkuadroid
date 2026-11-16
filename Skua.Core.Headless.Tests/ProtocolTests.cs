using Skua.Core.Headless.Protocol;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Tests;

public class FramerTests
{
    private static List<string> Push(PacketFramer framer, string data)
    {
        return framer.Push(Encoding.UTF8.GetBytes(data)).Select(f => Encoding.UTF8.GetString(f)).ToList();
    }

    [Fact]
    public void SplitsOnNulAndKeepsThePartialTail()
    {
        PacketFramer framer = new();
        Assert.Equal(new[] { "%xt%hi%-1%" }, Push(framer, "%xt%hi%-1%\0%xt%serv"));
        Assert.Equal(8, framer.PendingBytes);
        Assert.Equal(new[] { "%xt%server%-1%ok%" }, Push(framer, "er%-1%ok%\0"));
        Assert.Empty(Push(framer, string.Empty));
        Assert.Equal(0, framer.PendingBytes);
    }

    [Fact]
    public void ReturnsSeveralFramesFromOneRead()
    {
        PacketFramer framer = new();
        Assert.Equal(new[] { "a", "b", "c" }, Push(framer, "a\0b\0c\0"));
    }

    [Fact]
    public void SkipsAndCountsEmptyFrames()
    {
        PacketFramer framer = new();
        Assert.Equal(new[] { "a", "b" }, Push(framer, "\0a\0\0b\0"));
        Assert.Equal(2, framer.EmptyFrames);
    }

    [Fact]
    public void KeepsMultiByteCharactersSplitAcrossReads()
    {
        PacketFramer framer = new();
        byte[] bytes = Encoding.UTF8.GetBytes("%xt%chatm%-1%zone~héllo ✓%x%\0");
        int cut = Array.IndexOf(bytes, (byte)0xC3) + 1; // inside the two-byte é
        Assert.Empty(framer.Push(bytes.AsSpan(0, cut)));
        IReadOnlyList<byte[]> frames = framer.Push(bytes.AsSpan(cut));
        Assert.Equal("%xt%chatm%-1%zone~héllo ✓%x%", Encoding.UTF8.GetString(Assert.Single(frames)));
    }

    [Fact]
    public void RejectsAPartialFramePastTheLimit()
    {
        PacketFramer framer = new(maxFrameBytes: 64);
        framer.Push(new byte[40].Select(_ => (byte)'a').ToArray());
        Assert.Throws<FrameTooLargeException>(() => framer.Push(new byte[40].Select(_ => (byte)'a').ToArray()));
        Assert.Equal(0, framer.PendingBytes);
    }

    [Fact]
    public void RejectsACompleteFramePastTheLimit()
    {
        PacketFramer framer = new(maxFrameBytes: 8);
        Assert.Throws<FrameTooLargeException>(() => framer.Push(Encoding.ASCII.GetBytes("0123456789\0")));
    }
}

public class CodecTests
{
    private static string Zlib(string text)
    {
        using MemoryStream output = new();
        using (ZLibStream z = new(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(Encoding.UTF8.GetBytes(text));
        return "Z" + Convert.ToBase64String(output.ToArray());
    }

    private static string RawDeflate(string text)
    {
        using MemoryStream output = new();
        using (DeflateStream z = new(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(Encoding.UTF8.GetBytes(text));
        return "Z" + Convert.ToBase64String(output.ToArray());
    }

    [Fact]
    public void ParsesXtWithEmptyFieldsInPlace()
    {
        InboundPacket p = PacketCodec.Decode("%xt%loginResponse%-1%0%-1%%Rejected.%");
        Assert.Equal(PacketKind.Xt, p.Kind);
        Assert.Equal("loginResponse", p.Command);
        Assert.Equal(-1, p.Room);
        Assert.Equal(new[] { "0", "-1", string.Empty, "Rejected." }, p.Args);
    }

    [Fact]
    public void ParsesXtWithoutArguments()
    {
        InboundPacket p = PacketCodec.Decode("%xt%respawnMon%-1%");
        Assert.Equal("respawnMon", p.Command);
        Assert.Empty(p.Args);
    }

    [Fact]
    public void ParsesJsonPayloadAndRoom()
    {
        InboundPacket p = PacketCodec.Decode("{\"t\":\"xt\",\"b\":{\"r\":-1,\"o\":{\"cmd\":\"moveToArea\",\"areaId\":5}}}");
        Assert.Equal(PacketKind.Json, p.Kind);
        Assert.Equal("moveToArea", p.Command);
        Assert.Equal(-1, p.Room);
        Assert.Equal(5, (int)p.Data!["areaId"]!);
    }

    [Fact]
    public void JsonWithoutPayloadIsKeptAndFlagged()
    {
        InboundPacket p = PacketCodec.Decode("{\"t\":\"xt\",\"b\":{\"r\":-1}}");
        Assert.Equal(PacketKind.Json, p.Kind);
        Assert.Null(p.Command);
        Assert.NotNull(p.DecodeError);
    }

    [Fact]
    public void ParsesXmlActionRoomAndUser()
    {
        InboundPacket entered = PacketCodec.Decode("<msg t='sys'><body action='uER' r='42'><u i='10' m='0' s='0' p='3'><n><![CDATA[player4]]></n><vars></vars></u></body></msg>");
        Assert.Equal(PacketKind.Xml, entered.Kind);
        Assert.Equal("uER", entered.Command);
        Assert.Equal(42, entered.Room);
        Assert.Equal(10, entered.XmlUserId);
        Assert.Equal("player4", entered.XmlUserName);

        InboundPacket gone = PacketCodec.Decode("<msg t='sys'><body action='userGone' r='42'><user id='10' /></body></msg>");
        Assert.Equal("userGone", gone.Command);
        Assert.Equal(10, gone.XmlUserId);
        Assert.Null(gone.XmlUserName);
    }

    [Fact]
    public void ParsesApiOk()
    {
        InboundPacket p = PacketCodec.Decode("<msg t='sys'><body action='apiOK' r='0'></body></msg>");
        Assert.Equal("apiOK", p.Command);
        Assert.Equal(0, p.Room);
    }

    [Fact]
    public void ZlibCompressedMessagesDecodeLikePlainOnes()
    {
        string text = "{\"t\":\"xt\",\"b\":{\"r\":-1,\"o\":{\"cmd\":\"loadInventoryBig\",\"items\":[]}}}";
        InboundPacket p = PacketCodec.Decode(Zlib(text));
        Assert.True(p.WasCompressed);
        Assert.Equal(text, p.Text);
        Assert.Equal("loadInventoryBig", p.Command);
    }

    [Fact]
    public void CompressedXtMessagesDecode()
    {
        InboundPacket p = PacketCodec.Decode(Zlib("%xt%server%-1%hello%"));
        Assert.True(p.WasCompressed);
        Assert.Equal(PacketKind.Xt, p.Kind);
        Assert.Equal("hello", p.Arg(0));
    }

    [Fact]
    public void RawDeflateIsAcceptedToo()
    {
        InboundPacket p = PacketCodec.Decode(RawDeflate("%xt%server%-1%hello%"));
        Assert.True(p.WasCompressed);
        Assert.Equal("server", p.Command);
    }

    [Fact]
    public void MissingBase64PaddingIsTolerated()
    {
        string packed = Zlib("%xt%server%-1%hi there%").TrimEnd('=');
        InboundPacket p = PacketCodec.Decode(packed);
        Assert.True(p.WasCompressed);
        Assert.Equal("hi there", p.Arg(0));
    }

    [Fact]
    public void MessagesThatDoNotInflateArePassedOnFlagged()
    {
        InboundPacket p = PacketCodec.Decode("Zbm90IHpsaWIgYXQgYWxs");
        Assert.Equal(PacketKind.Unknown, p.Kind);
        Assert.True(p.DecompressionFailed);
        Assert.Equal("Zbm90IHpsaWIgYXQgYWxs", p.Text);
        Assert.NotNull(p.DecodeError);
    }

    [Fact]
    public void OversizedInflationIsRejected()
    {
        string big = "%xt%server%-1%" + new string('a', PacketCodec.MaxDecompressedBytes + 10) + "%";
        PacketCodec.UnpackResult result = PacketCodec.Unpack(Zlib(big));
        Assert.False(result.Success);
        Assert.Contains("inflates past", result.Error);
    }

    [Fact]
    public void PackRoundTrips()
    {
        string text = "{\"t\":\"xt\",\"b\":{\"r\":-1,\"o\":{\"cmd\":\"x\",\"v\":\"✓\"}}}";
        Assert.Equal(text, PacketCodec.Decode(PacketCodec.Pack(text)).Text);
    }

    [Fact]
    public void UnknownKindsAreReportedNotThrown()
    {
        InboundPacket p = PacketCodec.Decode("hello");
        Assert.Equal(PacketKind.Unknown, p.Kind);
        Assert.Contains("unknown message kind", p.DecodeError);
        Assert.Equal(PacketKind.Unknown, PacketCodec.Decode("{not json").Kind);
        Assert.Equal(PacketKind.Unknown, PacketCodec.Decode("%yt%a%1%").Kind);
    }

    [Fact]
    public void ExtensionResponseMatchesSkuaSwfShapeForJson()
    {
        InboundPacket p = PacketCodec.Decode("{\"t\":\"xt\",\"b\":{\"r\":-1,\"o\":{\"cmd\":\"ccqr\",\"QuestID\":7,\"bSuccess\":1}}}");
        JsonObject pext = (JsonObject)JsonNode.Parse(p.ToExtensionResponseJson()!)!;
        Assert.Equal("onExtensionResponse", (string)pext["type"]!);
        Assert.Equal("json", (string)pext["params"]!["type"]!);
        Assert.Equal("ccqr", (string)pext["params"]!["dataObj"]!["cmd"]!);
        Assert.Equal(7, (int)pext["params"]!["dataObj"]!["QuestID"]!);
    }

    [Fact]
    public void ExtensionResponseMatchesSkuaSwfShapeForStrings()
    {
        // ScriptInterface.HandleFlashCall reads data[0] = cmd, data[2] = user, data[3] = "afk:true" for uotls.
        InboundPacket p = PacketCodec.Decode("%xt%uotls%-1%someone%afk:true%");
        JsonObject pext = (JsonObject)JsonNode.Parse(p.ToExtensionResponseJson()!)!;
        Assert.Equal("str", (string)pext["params"]!["type"]!);
        JsonArray data = (JsonArray)pext["params"]!["dataObj"]!;
        Assert.Equal(new[] { "uotls", "-1", "someone", "afk:true" }, data.Select(x => (string)x!).ToArray());
    }

    [Fact]
    public void XmlHasNoExtensionResponse()
    {
        Assert.Null(PacketCodec.Decode("<msg t='sys'><body action='apiOK' r='0'></body></msg>").ToExtensionResponseJson());
    }
}

public class PacketsTests
{
    [Fact]
    public void HandshakeMatchesTheWireFormat()
    {
        Assert.Equal("<msg t='sys'><body action='verChk' r='0'><ver v='166' /></body></msg>", Packets.VersionCheck());
        Assert.Equal(
            "<msg t='sys'><body action='login' r='0'><login z='zone_master'><nick><![CDATA[SPIDER#0001~player1~5.31]]></nick><pword><![CDATA[tok]]></pword></login></body></msg>",
            Packets.Login("player1", "5.31", "tok"));
    }

    [Fact]
    public void CDataTerminatorInsideAValueIsSplit()
    {
        Assert.Contains("<pword><![CDATA[a]]]]><![CDATA[>b]]></pword>", Packets.Login("u", "1.0", "a]]>b"));
    }

    [Fact]
    public void CommandsMatchWhatSkuaCoreSends()
    {
        Assert.Equal("%xt%zm%firstJoin%1%", Packets.FirstJoin());
        Assert.Equal("%xt%zm%restRequest%1%%", Packets.Rest());
        Assert.Equal("%xt%zm%cmd%42%tfer%Player1%battleon%Enter%Spawn%", Packets.Transfer(42, "Player1", "battleon"));
        Assert.Equal("%xt%zm%moveToCell%42%r2%Left%", Packets.MoveToCell(42, "r2", "Left"));
        Assert.Equal("%xt%zm%gar%1%3%a1>m:5,a1>m:6%wvz%", Packets.Gar(3, "a1", new[] { "m:5", "m:6" }));
        Assert.Equal("%xt%zm%gar%1%250%a3>%wvz%", Packets.Gar(250, "a3", Array.Empty<string>()));
        Assert.Equal("%xt%zm%acceptQuest%42%3%", Packets.AcceptQuest(42, 3));
        Assert.Equal("%xt%zm%tryQuestComplete%42%3%-1%false%1%wvz%", Packets.TryQuestComplete(42, 3));
        Assert.Equal("%xt%zm%tryQuestComplete%42%3%900%false%5%wvz%", Packets.TryQuestComplete(42, 3, 900, false, 5));
        Assert.Equal("%xt%zm%getQuests%42%1%2%", Packets.GetQuests(42, new[] { 1, 2 }));
        Assert.Equal("%xt%zm%loadBank%42%All%", Packets.LoadBank(42));
        Assert.Equal("%xt%zm%getDrop%42%1027%", Packets.GetDrop(42, 1027));
        Assert.Equal("%xt%zm%retrieveUserData%42%7%", Packets.RetrieveUserData(42, 7));
        Assert.Equal("%xt%zm%retrieveUserDatas%42%8%9%", Packets.RetrieveUserDatas(42, new[] { 8, 9 }));
        Assert.Equal("%xt%zm%retrieveInventory%42%7%", Packets.RetrieveInventory(42, 7));
        Assert.Equal("%xt%zm%afk%1%false%", Packets.Afk(false));
        Assert.Equal("%xt%zm%cmd%1%logout%", Packets.Logout());
        Assert.Equal("%xt%zm%aggroMon%42%1%2%", Packets.AggroMonsters(42, new[] { 1, 2 }));
    }

    [Fact]
    public void OutboundPacketsCanBeTakenApart()
    {
        Assert.Equal("moveToCell", Packets.CommandOf("%xt%zm%moveToCell%42%r2%Left%"));
        Assert.Equal(new[] { "r2", "Left" }, Packets.ArgsOf("%xt%zm%moveToCell%42%r2%Left%"));
        Assert.Equal(new[] { string.Empty }, Packets.ArgsOf("%xt%zm%restRequest%1%%"));
        Assert.Empty(Packets.ArgsOf("%xt%zm%firstJoin%1%"));
        Assert.Null(Packets.CommandOf("<msg t='sys'/>"));
    }
}
