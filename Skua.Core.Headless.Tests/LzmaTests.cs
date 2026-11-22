using Skua.Core.Headless.Maps;
using Skua.Core.Headless.Tests.Fakes;
using System.Text;

namespace Skua.Core.Headless.Tests;

/// <summary>The LZMA decoder behind "ZWS" SWF files (maps and game files).</summary>
public class LzmaTests
{
    // A raw LZMA1 stream (lc=3, lp=0, pb=2, 64 KB dictionary, end marker after the data) made by CPython's lzma module
    // (liblzma, preset 9) from Content(): matches with short and long distances, repeats and matched literals.
    private const string Fixture =
        "ADGZSe5I7+ZUuDCWPjKHMMA40ykHWu5gqlKz792l85Ykw8POwEp4sBkNiefXWGqIEPxx7AL4wjGfvFaRY01MyhjhHlbkTS92d4jF"
        + "dKV9M/cob/tQrni9FK+drEihFi7YqyUwDRg9G/tCaMsMinpNquEOqAb5ux0AW1Dlv2LgEXtOlvuMM5GFVJaks+dHmmbm1b5N4YUO"
        + "bDrCNnqz9YGwjSfRIumhL32vIfedDMQA+HeUfbvpxNOMDqlxJrzGs/Qmlw24mr5GbSdPH6ixaCzaP8ohNZZ4kxqtZ1b06aPriIaJ"
        + "1I9ft81ClaGHA9Ny9nKgMO5VcOXxzP3SWeBizCGP2IXPip60E7C81iM5HDF1YcnRl16dwVFTfAbKrSh3iLQRd3ffMzB3jQYANi5n"
        + "THAZq42hn+CO68s64jMHgLl4ZYD0Y4JdPRyUsHvPIjaBdz2tQXGz9hxv3euV+bW4MefJFrfcpWdtfb+Uz7NzX/BhB3GitWrS+iIV"
        + "zRX/e9H3A7GBpBDByb8YQoQlXHuaoLPIpINFQGi/PtspGq25q+VcQXLx6yEpqNjxQYEM5F6xmcYx/i8PE9SHYX0bM4RP+zmY0LYZ"
        + "eoEkYqkPK5dmzAao/R28llo1qoH/ePkP+kpuSthWbaS+ZrQRby0TtV9oeR2N3lZwvY57CSFnPLytEyCYjasPJ9JRXZ7niQvQ2CQv"
        + "A1JKJx7Cdf6bZ3OGgd3WPrSQ57JLxOawEi1LCELmadiXC2pbwGWfQ/ecOPj7ClP/dFCHq0jjXDL6niYwsYSAK6HxBeTtjTXdc9kN"
        + "ExYObHHdaqA0i2oCDoeU+Aa6pF8oc+osLOxYpPchndkGUMAqHaw8Nf4T9Ba16sDTyVxbrFF8YhNZ2ABK6PqrWtVQviVk2s8TzVgx"
        + "tHi633FEDJ9oq8zNQIALUn1BjtKjlV3xB22EUAPoJvfvwZglRJeszpCjr6Kb8v0gpDHJe7Ct1rUx1sOTDZxwEXdzQqz/urw2Q3XE"
        + "ETZIRwK6w5fto16bcRhIl6iVof9tijroaXlHVuhAUpuEcf+e00WREsdtG16T//9T4RAA";

    private static byte[] Content()
    {
        StringBuilder text = new();
        for (int i = 0; i < 3000; i++)
            text.Append("cell").Append(i % 37).Append(":pad").Append(i * 7 % 11).Append(';');
        List<byte> data = new(Encoding.ASCII.GetBytes(text.ToString()));
        for (int i = 0; i < 2000; i++)
            data.Add((byte)((i * 131 + 7) % 251));
        data.AddRange(data.Take(5000).ToList());
        return data.ToArray();
    }

    private static byte[] Zws(byte[] stream, uint fileLength)
    {
        using MemoryStream file = new();
        file.Write("ZWS"u8);
        file.WriteByte(13);
        file.Write(BitConverter.GetBytes(fileLength));
        file.Write(BitConverter.GetBytes((uint)stream.Length));
        file.Write(LiteralLzmaEncoder.Properties);
        file.Write(stream);
        return file.ToArray();
    }

    [Fact]
    public void DecodesARealEncodersStream()
    {
        byte[] content = Content();
        byte[] swf = Zws(Convert.FromBase64String(Fixture), (uint)content.Length + 8);
        byte[] body = SwfReader.Decompress(swf, out SwfCompression compression, out int version);
        Assert.Equal(SwfCompression.Lzma, compression);
        Assert.Equal(13, version);
        Assert.Equal(content, body);
    }

    [Fact]
    public void DecodesTheLiteralTestEncoder()
    {
        byte[] data = new byte[70000];
        new Random(7).NextBytes(data);
        byte[] swf = LiteralLzmaEncoder.Zws(13, data);
        Assert.Equal(data, SwfReader.Decompress(swf, out _, out _));
    }

    [Fact]
    public void TheHeaderLengthBoundsTheOutput()
    {
        byte[] content = Content();
        byte[] stream = Convert.FromBase64String(Fixture);

        // Shorter than the data: exactly that much comes out.
        Assert.Equal(content[..1000], SwfReader.Decompress(Zws(stream, 1008), out _, out _));

        // No usable length, or one past the 64 MB cap (reused from the zlib path): refused before decoding.
        Assert.Throws<SwfFormatException>(() => SwfReader.Decompress(Zws(stream, 8), out _, out _));
        Assert.Throws<SwfFormatException>(() => SwfReader.Decompress(Zws(stream, (uint)(SwfReader.MaxInflatedBytes + 9)), out _, out _));
        Assert.Null(SwfReader.InflateLzmaBounded(Zws(stream, 100_008), 100_008, 1000));

        // Longer than the stream holds: the end marker comes first, a format error (not a hang or a huge buffer).
        Assert.Throws<SwfFormatException>(() => SwfReader.Decompress(Zws(stream, (uint)content.Length + 5000), out _, out _));
    }

    [Fact]
    public void CorruptOrTruncatedStreamsAreFormatErrors()
    {
        byte[] content = Content();
        byte[] stream = Convert.FromBase64String(Fixture);
        Assert.Throws<SwfFormatException>(() => SwfReader.Decompress(Zws(stream[..200], (uint)content.Length + 8), out _, out _));
        byte[] flipped = (byte[])stream.Clone();
        flipped[0] = 1;   // the range coder's first byte is always 0
        Assert.Throws<SwfFormatException>(() => SwfReader.Decompress(Zws(flipped, (uint)content.Length + 8), out _, out _));
        byte[] badProps = Zws(stream, (uint)content.Length + 8);
        badProps[12] = 225;   // lc/lp/pb out of range
        Assert.Throws<SwfFormatException>(() => SwfReader.Decompress(badProps, out _, out _));
        Assert.Throws<SwfFormatException>(() => SwfReader.Decompress("ZWS\u000d\u0010\u0000\u0000\u0000"u8.ToArray(), out _, out _));
        for (int i = 20; i < stream.Length; i += 37)
        {
            byte[] noisy = (byte[])stream.Clone();
            noisy[i] ^= 0x5A;
            try
            {
                SwfReader.Decompress(Zws(noisy, (uint)content.Length + 8), out _, out _);
            }
            catch (SwfFormatException)
            {
            }
        }
    }
}
