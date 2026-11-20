using System.IO.Compression;
using System.Text;

namespace Skua.Core.Headless.Tests.Fakes;

/// <summary>
/// Builds small, synthetic map SWFs (no game content): a root timeline with one labelled frame per cell, pads placed
/// on each frame as named instances of a symbol whose class is <c>test_fla.Pad_1</c>.
/// </summary>
public static class SyntheticSwf
{
    /// <summary>A CWS file whose string pool holds <c>vParam</c> = <paramref name="version"/> (what version discovery reads).</summary>
    public static byte[] GameFile(string version)
    {
        byte[] content = Encoding.Latin1.GetBytes("junk\u0006vParam" + (char)version.Length + version + "\u0005ISWEB");
        byte[] header = { (byte)'C', (byte)'W', (byte)'S', 10, 0, 0, 0, 0 };
        using MemoryStream output = new();
        using (ZLibStream z = new(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(content);
        return header.Concat(output.ToArray()).ToArray();
    }

    /// <param name="cells">Cell name and its pad instance names, in timeline order.</param>
    /// <param name="compression">"FWS", "CWS" or "ZWS" (LZMA, coded by <see cref="LiteralLzmaEncoder"/>).</param>
    /// <param name="sceneTag">Also write DefineSceneAndFrameLabelData (tag 86).</param>
    public static byte[] Map(IReadOnlyList<(string Cell, string[] Pads)> cells, string compression = "CWS", bool sceneTag = true)
    {
        using MemoryStream body = new();
        // RECT with nbits = 15: 5 + 4*15 = 65 bits -> 9 bytes, all zero coordinates.
        body.WriteByte(15 << 3);
        body.Write(new byte[8]);
        Write16(body, 30 << 8); // frame rate
        Write16(body, cells.Count);

        // A pad symbol (an empty sprite) and its class.
        Tag(body, 39, w =>
        {
            Write16(w, 1);   // sprite id
            Write16(w, 1);   // frame count
            Write16(w, 1 << 6); // ShowFrame
            Write16(w, 0);   // End
        });
        Tag(body, 76, w =>
        {
            Write16(w, 1);
            Write16(w, 1);
            WriteString(w, "test_fla.Pad_1");
        });
        if (sceneTag)
        {
            Tag(body, 86, w =>
            {
                WriteU30(w, 1);
                WriteU30(w, 0);
                WriteString(w, "Scene 1");
                WriteU30(w, (uint)cells.Count);
                for (int i = 0; i < cells.Count; i++)
                {
                    WriteU30(w, (uint)i);
                    WriteString(w, cells[i].Cell);
                }
            });
        }

        int depth = 1;
        List<int> placed = new();
        foreach ((string cell, string[] pads) in cells)
        {
            foreach (int d in placed)
                Tag(body, 28, w => Write16(w, d)); // RemoveObject2
            placed.Clear();
            Tag(body, 43, w => WriteString(w, cell));
            foreach (string pad in pads)
            {
                int d = depth++;
                placed.Add(d);
                Tag(body, 26, w =>
                {
                    w.WriteByte(0x20 | 0x04 | 0x02); // name, matrix, character
                    Write16(w, d);
                    Write16(w, 1);
                    w.WriteByte(0); // MATRIX: no scale, no rotate, translate bits 0 (aligned)
                    WriteString(w, pad);
                });
            }
            Write16(body, 1 << 6); // ShowFrame
        }
        Write16(body, 0); // End

        byte[] raw = body.ToArray();
        if (compression == "ZWS")
            return LiteralLzmaEncoder.Zws(13, raw);
        using MemoryStream file = new();
        file.Write(Encoding.ASCII.GetBytes(compression));
        file.WriteByte(10);
        Write32(file, (uint)(raw.Length + 8));
        if (compression == "CWS")
        {
            using ZLibStream z = new(file, CompressionLevel.Optimal, leaveOpen: true);
            z.Write(raw);
        }
        else
        {
            file.Write(raw);
        }
        return file.ToArray();
    }

    private static void Tag(Stream s, int code, Action<Stream> content)
    {
        using MemoryStream c = new();
        content(c);
        byte[] bytes = c.ToArray();
        if (bytes.Length < 0x3f)
        {
            Write16(s, (code << 6) | bytes.Length);
        }
        else
        {
            Write16(s, (code << 6) | 0x3f);
            Write32(s, (uint)bytes.Length);
        }
        s.Write(bytes);
    }

    private static void Write16(Stream s, int v)
    {
        s.WriteByte((byte)(v & 0xff));
        s.WriteByte((byte)((v >> 8) & 0xff));
    }

    private static void Write32(Stream s, uint v)
    {
        for (int i = 0; i < 4; i++)
            s.WriteByte((byte)((v >> (8 * i)) & 0xff));
    }

    private static void WriteU30(Stream s, uint v)
    {
        do
        {
            byte b = (byte)(v & 0x7f);
            v >>= 7;
            if (v != 0)
                b |= 0x80;
            s.WriteByte(b);
        }
        while (v != 0);
    }

    private static void WriteString(Stream s, string text)
    {
        s.Write(Encoding.UTF8.GetBytes(text));
        s.WriteByte(0);
    }
}

/// <summary>
/// The smallest valid LZMA encoder for tests: every byte is coded as a literal (lc=3, lp=0, pb=2), with the range
/// coder and adaptive bit models of the LZMA specification, so any data can be wrapped into a "ZWS" SWF.
/// </summary>
public static class LiteralLzmaEncoder
{
    /// <summary>The 5 property bytes: lc/lp/pb, then a 64 KB dictionary (little-endian).</summary>
    public static readonly byte[] Properties = { (2 * 5 + 0) * 9 + 3, 0x00, 0x00, 0x01, 0x00 };

    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        RangeEncoder rc = new();
        ushort[] isMatch = Filled(12 << 4);
        ushort[] literal = Filled(0x300 << 3);
        for (int pos = 0; pos < data.Length; pos++)
        {
            rc.Bit(isMatch, (0 << 4) + (pos & 3), 0);
            int prev = pos > 0 ? data[pos - 1] : 0;
            int offset = 0x300 * (prev >> 5);
            int symbol = 1;
            for (int i = 7; i >= 0; i--)
            {
                int bit = (data[pos] >> i) & 1;
                rc.Bit(literal, offset + symbol, bit);
                symbol = symbol << 1 | bit;
            }
        }
        return rc.Finish();
    }

    /// <summary>"ZWS" + version + file length + compressed length + properties + stream.</summary>
    public static byte[] Zws(byte version, ReadOnlySpan<byte> body, uint? claimedFileLength = null)
    {
        byte[] stream = Encode(body);
        using MemoryStream file = new();
        file.Write("ZWS"u8);
        file.WriteByte(version);
        file.Write(BitConverter.GetBytes(claimedFileLength ?? (uint)(body.Length + 8)));
        file.Write(BitConverter.GetBytes((uint)stream.Length));
        file.Write(Properties);
        file.Write(stream);
        return file.ToArray();
    }

    private static ushort[] Filled(int n)
    {
        ushort[] a = new ushort[n];
        Array.Fill(a, (ushort)1024);
        return a;
    }

    private sealed class RangeEncoder
    {
        private readonly MemoryStream _out = new();
        private ulong _low;
        private uint _range = 0xFFFFFFFF;
        private byte _cache;
        private long _cacheSize = 1;

        public void Bit(ushort[] probs, int index, int bit)
        {
            uint p = probs[index];
            uint bound = (_range >> 11) * p;
            if (bit == 0)
            {
                _range = bound;
                probs[index] = (ushort)(p + ((2048 - p) >> 5));
            }
            else
            {
                _low += bound;
                _range -= bound;
                probs[index] = (ushort)(p - (p >> 5));
            }
            while (_range < 1u << 24)
            {
                _range <<= 8;
                ShiftLow();
            }
        }

        private void ShiftLow()
        {
            if ((uint)_low < 0xFF000000u || (_low >> 32) != 0)
            {
                byte temp = _cache;
                do
                {
                    _out.WriteByte((byte)(temp + (byte)(_low >> 32)));
                    temp = 0xFF;
                }
                while (--_cacheSize != 0);
                _cache = (byte)((uint)_low >> 24);
            }
            _cacheSize++;
            _low = (_low & 0x00FFFFFF) << 8;
        }

        public byte[] Finish()
        {
            for (int i = 0; i < 5; i++)
                ShiftLow();
            return _out.ToArray();
        }
    }
}
