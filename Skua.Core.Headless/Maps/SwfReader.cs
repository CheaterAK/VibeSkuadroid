using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Skua.Core.Headless.Maps;

/// <summary>How a SWF file's body is stored.</summary>
public enum SwfCompression
{
    /// <summary>"FWS": uncompressed.</summary>
    None,

    /// <summary>"CWS": zlib (SWF 6+).</summary>
    Zlib,

    /// <summary>"ZWS": LZMA (SWF 13+). Not decoded by this reader.</summary>
    Lzma,
}

/// <summary>The scene and frame label data of a SWF's root timeline, plus the pads placed on each labelled frame.</summary>
/// <param name="Version">SWF version byte.</param>
/// <param name="Compression">Body compression.</param>
/// <param name="FrameCount">Root timeline frame count from the header.</param>
/// <param name="Scenes">Scenes (name, first frame, 0-based) from DefineSceneAndFrameLabelData, if present.</param>
/// <param name="Labels">Root frame labels in timeline order (frame is 0-based).</param>
/// <param name="Pads">For each label: the instance names of pads on that frame (see <see cref="SwfReader"/>).</param>
public sealed record SwfTimeline(
    int Version,
    SwfCompression Compression,
    int FrameCount,
    IReadOnlyList<(string Name, int Frame)> Scenes,
    IReadOnlyList<(string Name, int Frame)> Labels,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Pads);

/// <summary>The SWF could not be read.</summary>
public sealed class SwfFormatException : Exception
{
    public SwfFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A minimal SWF tag reader for map files: what the Flash client gets from a map's root MovieClip
/// (<c>currentScene.labels</c> are the cells; pads are the clips skua.swf's World.getValidCellPads finds).
/// </summary>
/// <remarks>
/// <para>Handles "FWS" (plain) and "CWS" (zlib). "ZWS" (LZMA) is detected and refused with
/// <see cref="NotSupportedException"/>: .NET has no LZMA decoder and none is shipped here; a caller falls back to
/// the cells the server and scripts name.</para>
/// <para>Labels come from DefineSceneAndFrameLabelData (tag 86) when present, else from the root FrameLabel tags (43).
/// Pads: on each labelled frame, the named instances on the root display list (PlaceObject2/3, RemoveObject/2)
/// whose symbol class (SymbolClass, tag 76) ends in <c>Pad_&lt;n&gt;</c>, as skua.swf's <c>/::Pad_\d+$/</c> test.
/// When a SWF has no such classes, named instances whose name is a known pad name are used instead.</para>
/// </remarks>
public static partial class SwfReader
{
    /// <summary>Pad names seen in AQW maps, used when a SWF exposes no pad classes.</summary>
    public static IReadOnlySet<string> KnownPadNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Spawn", "Center", "Left", "Right", "Top", "Bottom", "Up", "Down", "Front", "Back",
        "Pad1", "Pad2", "Pad3", "Pad4", "Pad5", "Pad6",
    };

    /// <summary>The most a SWF body may inflate to (maps are a few MB; the live game SWF is about 9 MB inflated).</summary>
    public const long MaxInflatedBytes = 64L * 1024 * 1024;

    private const int TagEnd = 0;
    private const int TagShowFrame = 1;
    private const int TagPlaceObject = 4;
    private const int TagRemoveObject = 5;
    private const int TagPlaceObject2 = 26;
    private const int TagRemoveObject2 = 28;
    private const int TagFrameLabel = 43;
    private const int TagPlaceObject3 = 70;
    private const int TagSymbolClass = 76;
    private const int TagDefineSceneAndFrameLabelData = 86;

    /// <summary>The compression of a SWF, from its signature.</summary>
    public static SwfCompression DetectCompression(ReadOnlySpan<byte> swf)
    {
        if (swf.Length < 8)
            throw new SwfFormatException("Too short to be a SWF.");
        return (swf[0], swf[1], swf[2]) switch
        {
            ((byte)'F', (byte)'W', (byte)'S') => SwfCompression.None,
            ((byte)'C', (byte)'W', (byte)'S') => SwfCompression.Zlib,
            ((byte)'Z', (byte)'W', (byte)'S') => SwfCompression.Lzma,
            _ => throw new SwfFormatException("Not a SWF (bad signature)."),
        };
    }

    /// <summary>The uncompressed body (everything after the 8-byte header).</summary>
    /// <exception cref="NotSupportedException">The SWF is LZMA compressed (ZWS).</exception>
    public static byte[] Decompress(byte[] swf, out SwfCompression compression, out int version)
    {
        compression = DetectCompression(swf);
        version = swf[3];
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(swf.AsSpan(4));
        switch (compression)
        {
            case SwfCompression.None:
                return swf[8..];

            case SwfCompression.Zlib:
                try
                {
                    using MemoryStream input = new(swf, 8, swf.Length - 8, writable: false);
                    return InflateBounded(input, length, MaxInflatedBytes)
                        ?? throw new SwfFormatException($"The zlib body inflates past its declared length or {MaxInflatedBytes / (1024 * 1024)} MB.");
                }
                catch (InvalidDataException e)
                {
                    throw new SwfFormatException("Corrupt zlib body.", e);
                }

            default:
                throw new NotSupportedException("LZMA-compressed SWF (ZWS) is not supported by this reader.");
        }
    }

    /// <summary>
    /// Inflates a CWS body (the zlib stream after the 8-byte header), bounded by the header's own file length field
    /// (which counts the header) and by <paramref name="cap"/>: a small CWS can claim, or inflate to, gigabytes.
    /// Nothing is preallocated from the header. Null when the body would exceed the bound.
    /// </summary>
    /// <exception cref="InvalidDataException">The zlib stream is corrupt.</exception>
    internal static byte[]? InflateBounded(Stream compressedBody, uint headerFileLength, long cap)
    {
        long declared = headerFileLength - 8L;
        long limit = declared > 0 ? Math.Min(declared, cap) : cap; // no usable length given: the cap alone
        using ZLibStream zlib = new(compressedBody, CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > limit)
                return null;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <summary>Reads the root timeline's scenes, labels and pads.</summary>
    /// <exception cref="SwfFormatException">Malformed file.</exception>
    /// <exception cref="NotSupportedException">LZMA (ZWS) file.</exception>
    public static SwfTimeline Read(byte[] swf)
    {
        byte[] body = Decompress(swf, out SwfCompression compression, out int version);
        Reader r = new(body);
        try
        {
            int nbits = r.PeekByte() >> 3;
            r.SkipBits(5 + nbits * 4);
            r.AlignByte();
            r.U16(); // frame rate (8.8)
            int frameCount = r.U16();

            List<(string, int)> scenes = new();
            List<(string, int)> sceneLabels = new();
            List<(string, int)> frameLabels = new();
            Dictionary<int, string> symbolClasses = new();
            Dictionary<int, (int CharId, string? Name)> displayList = new();
            List<(int Frame, string Label)> labelledFrames = new();
            Dictionary<int, List<(int CharId, string Name)>> namedOnFrame = new();
            int frame = 0;
            string? pendingLabel = null;

            while (!r.AtEnd)
            {
                (int code, int length) = r.TagHeader();
                int start = r.Position;
                if (start + length > body.Length)
                    throw new SwfFormatException($"Tag {code} runs past the end of the file.");
                switch (code)
                {
                    case TagEnd:
                        r.Position = body.Length;
                        continue;

                    case TagShowFrame:
                        if (pendingLabel is not null)
                        {
                            labelledFrames.Add((frame, pendingLabel));
                            pendingLabel = null;
                        }
                        namedOnFrame[frame] = displayList.OrderBy(p => p.Key).Where(p => p.Value.Name is not null).Select(p => (p.Value.CharId, p.Value.Name!)).ToList();
                        frame++;
                        break;

                    case TagFrameLabel:
                        string label = r.CString(start + length);
                        frameLabels.Add((label, frame));
                        pendingLabel ??= label;
                        break;

                    case TagDefineSceneAndFrameLabelData:
                        uint sceneCount = r.EncodedU32();
                        for (uint i = 0; i < sceneCount; i++)
                        {
                            int offset = (int)r.EncodedU32();
                            scenes.Add((r.CString(start + length), offset));
                        }
                        uint labelCount = r.EncodedU32();
                        for (uint i = 0; i < labelCount; i++)
                        {
                            int frameNum = (int)r.EncodedU32();
                            sceneLabels.Add((r.CString(start + length), frameNum));
                        }
                        break;

                    case TagSymbolClass:
                        int symbols = r.U16();
                        for (int i = 0; i < symbols; i++)
                        {
                            int tag = r.U16();
                            symbolClasses[tag] = r.CString(start + length);
                        }
                        break;

                    case TagPlaceObject:
                        int poChar = r.U16();
                        int poDepth = r.U16();
                        displayList[poDepth] = (poChar, null);
                        break;

                    case TagPlaceObject2:
                    case TagPlaceObject3:
                        ReadPlace(r, code, start + length, displayList);
                        break;

                    case TagRemoveObject:
                        r.U16();
                        displayList.Remove(r.U16());
                        break;

                    case TagRemoveObject2:
                        displayList.Remove(r.U16());
                        break;
                }
                r.Position = start + length;
            }

            List<(string Name, int Frame)> labels = sceneLabels.Count > 0 ? sceneLabels : frameLabels;
            HashSet<int> padChars = symbolClasses.Where(p => PadClass().IsMatch(p.Value)).Select(p => p.Key).ToHashSet();
            Dictionary<string, IReadOnlyList<string>> pads = new(StringComparer.Ordinal);
            foreach ((string name, int labelFrame) in labels)
            {
                if (pads.ContainsKey(name) || !namedOnFrame.TryGetValue(labelFrame, out List<(int CharId, string Name)>? named))
                    continue;
                List<string> found = padChars.Count > 0
                    ? named.Where(n => padChars.Contains(n.CharId)).Select(n => n.Name).Distinct().ToList()
                    : named.Where(n => KnownPadNames.Contains(n.Name)).Select(n => n.Name).Distinct().ToList();
                pads[name] = found;
            }
            return new SwfTimeline(version, compression, frameCount, scenes, labels, pads);
        }
        catch (IndexOutOfRangeException e)
        {
            throw new SwfFormatException("Truncated SWF.", e);
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new SwfFormatException("Truncated SWF.", e);
        }
    }

    // PlaceObject2/3: flags, depth, [class name], [character], [matrix], [color transform], [ratio], [name] ...
    private static void ReadPlace(Reader r, int code, int end, Dictionary<int, (int CharId, string? Name)> displayList)
    {
        int flags = r.U8();
        int flags2 = code == TagPlaceObject3 ? r.U8() : 0;
        int depth = r.U16();
        bool hasClassName = (flags2 & 0x08) != 0 || ((flags2 & 0x10) != 0 && (flags & 0x02) != 0);
        if (code == TagPlaceObject3 && hasClassName)
            r.CString(end);
        int? charId = (flags & 0x02) != 0 ? r.U16() : null;
        if ((flags & 0x04) != 0)
            r.SkipMatrix();
        if ((flags & 0x08) != 0)
            r.SkipColorTransformWithAlpha();
        if ((flags & 0x10) != 0)
            r.U16();
        string? name = (flags & 0x20) != 0 ? r.CString(end) : null;

        bool move = (flags & 0x01) != 0;
        if (displayList.TryGetValue(depth, out (int CharId, string? Name) existing) && move)
        {
            displayList[depth] = (charId ?? existing.CharId, name ?? (charId is null ? existing.Name : null));
        }
        else if (charId is int id)
        {
            displayList[depth] = (id, name);
        }
    }

    [GeneratedRegex(@"(^|[.:])Pad_\d+$")]
    private static partial Regex PadClass();

    private sealed class Reader
    {
        private readonly byte[] _data;
        private int _bit;

        public Reader(byte[] data)
        {
            _data = data;
        }

        public int Position { get; set; }

        public bool AtEnd => Position + 2 > _data.Length;

        public int PeekByte() => _data[Position];

        public int U8() => _data[Position++];

        public int U16()
        {
            int v = _data[Position] | (_data[Position + 1] << 8);
            Position += 2;
            return v;
        }

        public (int Code, int Length) TagHeader()
        {
            int header = U16();
            int code = header >> 6;
            int length = header & 0x3f;
            if (length == 0x3f)
            {
                length = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position));
                Position += 4;
            }
            if (length < 0)
                throw new SwfFormatException("Negative tag length.");
            return (code, length);
        }

        public uint EncodedU32()
        {
            uint result = 0;
            for (int i = 0; i < 5; i++)
            {
                int b = U8();
                result |= (uint)(b & 0x7f) << (7 * i);
                if ((b & 0x80) == 0)
                    break;
            }
            return result;
        }

        public string CString(int end)
        {
            int start = Position;
            while (Position < end && _data[Position] != 0)
                Position++;
            string s = Encoding.UTF8.GetString(_data, start, Position - start);
            if (Position < end)
                Position++;
            return s;
        }

        // ---- bit fields (MATRIX, CXFORM, RECT)
        public void SkipBits(int count)
        {
            int total = _bit + count;
            Position += total / 8;
            _bit = total % 8;
        }

        public void AlignByte()
        {
            if (_bit != 0)
            {
                Position++;
                _bit = 0;
            }
        }

        private uint Bits(int count)
        {
            uint v = 0;
            for (int i = 0; i < count; i++)
            {
                int b = (_data[Position] >> (7 - _bit)) & 1;
                v = (v << 1) | (uint)b;
                if (++_bit == 8)
                {
                    _bit = 0;
                    Position++;
                }
            }
            return v;
        }

        public void SkipMatrix()
        {
            if (Bits(1) == 1)
                SkipBits(2 * (int)Bits(5));
            if (Bits(1) == 1)
                SkipBits(2 * (int)Bits(5));
            SkipBits(2 * (int)Bits(5));
            AlignByte();
        }

        public void SkipColorTransformWithAlpha()
        {
            bool add = Bits(1) == 1;
            bool mult = Bits(1) == 1;
            int n = (int)Bits(4);
            if (mult)
                SkipBits(4 * n);
            if (add)
                SkipBits(4 * n);
            AlignByte();
        }
    }
}
