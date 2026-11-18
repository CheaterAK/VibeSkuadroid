using System.IO.Compression;
using System.Text;

namespace Skua.Core.Headless.Tests.Fakes;

/// <summary>
/// Builds small, synthetic map SWFs (no game content): a root timeline with one labelled frame per cell, pads placed
/// on each frame as named instances of a symbol whose class is <c>test_fla.Pad_1</c>.
/// </summary>
public static class SyntheticSwf
{
    /// <param name="cells">Cell name and its pad instance names, in timeline order.</param>
    /// <param name="compression">"FWS", "CWS" or "ZWS" (ZWS only gets the signature: the reader must refuse it).</param>
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
