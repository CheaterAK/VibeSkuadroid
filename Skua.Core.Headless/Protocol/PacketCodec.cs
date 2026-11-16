using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Skua.Core.Headless.Protocol;

/// <summary>
/// Decodes and encodes AQW socket messages.
/// </summary>
/// <remarks>
/// Since AQW 5.0 the server sends larger replies compressed as <c>"Z" + base64(zlib(utf8 text))</c>; the game
/// unpacks them before anything else reads them. The base64 handling mirrors
/// <c>Skua.Core/GameProxy/CaptureProxy.cs</c> (lenient: unknown characters count as zero and padding may be
/// missing, as the game's own decoder allows). A raw-deflate fallback follows aqx-protocol (MIT, toommyliu).
/// A message that will not inflate is passed on unchanged and flagged, never dropped.
/// </remarks>
public static partial class PacketCodec
{
    /// <summary>Local safety limit on the inflated size of one message.</summary>
    public const int MaxDecompressedBytes = 16 * 1024 * 1024;

    private const string Base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>Decodes one NUL-stripped frame from the socket.</summary>
    public static InboundPacket Decode(ReadOnlySpan<byte> frame)
    {
        return Decode(Encoding.UTF8.GetString(frame));
    }

    /// <summary>Decodes one message (compressed or not) given as text.</summary>
    public static InboundPacket Decode(string wireText)
    {
        bool compressed = false;
        string text = wireText;
        if (LooksCompressed(wireText))
        {
            UnpackResult result = Unpack(wireText);
            if (!result.Success)
            {
                return new InboundPacket(PacketKind.Unknown, wireText)
                {
                    DecompressionFailed = true,
                    DecodeError = result.Error,
                };
            }
            compressed = true;
            text = result.Text!;
        }

        return Parse(text, compressed);
    }

    /// <summary>True when the text has the shape of a compressed message.</summary>
    public static bool LooksCompressed(string text)
    {
        return text.Length > 1 && text[0] == 'Z';
    }

    /// <summary>Result of <see cref="Unpack"/>.</summary>
    public readonly record struct UnpackResult(bool Success, string? Text, string? Error);

    /// <summary>Inflates a <c>"Z" + base64(zlib)</c> message.</summary>
    public static UnpackResult Unpack(string message)
    {
        if (!LooksCompressed(message))
            return new(false, null, "not a compressed message");

        byte[] compressed;
        try
        {
            compressed = DecodeBase64Lenient(message.AsSpan(1));
        }
        catch (InvalidDataException e)
        {
            return new(false, null, e.Message);
        }

        if (compressed.Length < 2)
            return new(false, null, "compressed payload too short");

        try
        {
            return new(true, Inflate(compressed, zlib: true), null);
        }
        catch (PacketTooLargeException e)
        {
            return new(false, null, e.Message);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            try
            {
                // Raw deflate has no header or checksum, so garbage can "inflate"; only accept a real message.
                string text = Inflate(compressed, zlib: false);
                return text[0] is '%' or '{' or '<'
                    ? new(true, text, null)
                    : new(false, null, $"does not inflate: {e.Message}");
            }
            catch (PacketTooLargeException tooLarge)
            {
                return new(false, null, tooLarge.Message);
            }
            catch (Exception raw) when (raw is InvalidDataException or IOException)
            {
                return new(false, null, $"does not inflate: {e.Message}");
            }
        }
    }

    /// <summary>Compresses a message the way the AQW 5.0 server does (used by tests and the fake server).</summary>
    public static string Pack(string text)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(Encoding.UTF8.GetBytes(text));
        return "Z" + Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length);
    }

    /// <summary>Parses decompressed text into a packet. Never throws.</summary>
    public static InboundPacket Parse(string text, bool wasCompressed = false)
    {
        if (string.IsNullOrEmpty(text))
            return new InboundPacket(PacketKind.Unknown, text ?? string.Empty) { DecodeError = "empty message" };

        try
        {
            return text[0] switch
            {
                '%' => ParseXt(text, wasCompressed),
                '{' => ParseJson(text, wasCompressed),
                '<' => ParseXml(text, wasCompressed),
                _ => new InboundPacket(PacketKind.Unknown, text) { WasCompressed = wasCompressed, DecodeError = $"unknown message kind '{text[0]}'" },
            };
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return new InboundPacket(PacketKind.Unknown, text) { WasCompressed = wasCompressed, DecodeError = e.Message };
        }
    }

    // "%xt%uotls%-1%name%k:v%" -> ["", "xt", "uotls", "-1", "name", "k:v", ""]
    private static InboundPacket ParseXt(string text, bool wasCompressed)
    {
        string[] fields = text.Split('%');
        if (fields.Length < 4 || fields[1] != "xt" || fields[2].Length == 0)
            return new InboundPacket(PacketKind.Unknown, text) { WasCompressed = wasCompressed, DecodeError = "malformed xt message" };

        int end = fields.Length;
        if (text.EndsWith('%'))
            end--; // the terminator, not an empty argument
        string[] args = end > 4 ? fields[4..end] : Array.Empty<string>();
        string roomField = fields[3];
        return new InboundPacket(PacketKind.Xt, text)
        {
            WasCompressed = wasCompressed,
            Command = fields[2],
            Room = int.TryParse(roomField, out int room) ? room : null,
            RawRoomField = roomField,
            Args = args,
        };
    }

    private static InboundPacket ParseJson(string text, bool wasCompressed)
    {
        JsonNode? root = JsonNode.Parse(text);
        if (root is not JsonObject obj)
            return new InboundPacket(PacketKind.Unknown, text) { WasCompressed = wasCompressed, DecodeError = "JSON message is not an object" };

        JsonObject? body = obj["b"] as JsonObject;
        JsonObject? data = body?["o"] as JsonObject;
        int? room = null;
        if (body?["r"] is JsonValue r)
            room = JsonUtil.AsIntOrNull(r);

        if (data is null)
        {
            return new InboundPacket(PacketKind.Json, text)
            {
                WasCompressed = wasCompressed,
                Room = room,
                DecodeError = "JSON message has no b.o object",
            };
        }

        // Detach so subscribers and the model work on the payload alone.
        body!.Remove("o");
        return new InboundPacket(PacketKind.Json, text)
        {
            WasCompressed = wasCompressed,
            Command = JsonUtil.AsString(data["cmd"]),
            Room = room,
            Data = data,
        };
    }

    private static InboundPacket ParseXml(string text, bool wasCompressed)
    {
        Match action = XmlActionRegex().Match(text);
        Match room = XmlRoomRegex().Match(text);
        int? userId = null;
        string? userName = null;
        try
        {
            XElement root = XElement.Parse(text);
            XElement? body = root.Element("body");
            XElement? user = body?.Element("u") ?? body?.Element("user");
            string? id = user?.Attribute("i")?.Value ?? user?.Attribute("id")?.Value;
            if (int.TryParse(id, out int uid))
                userId = uid;
            string? name = user?.Element("n")?.Value;
            if (!string.IsNullOrWhiteSpace(name))
                userName = name.Trim();
        }
        catch (System.Xml.XmlException)
        {
            // Action and room come from the regexes; the user part is optional.
        }

        return new InboundPacket(PacketKind.Xml, text)
        {
            WasCompressed = wasCompressed,
            Command = action.Success ? action.Groups[1].Value : null,
            Room = room.Success && int.TryParse(room.Groups[1].Value, out int r) ? r : null,
            XmlUserId = userId,
            XmlUserName = userName,
            DecodeError = action.Success ? null : "XML message has no body action",
        };
    }

    private static string Inflate(byte[] compressed, bool zlib)
    {
        using MemoryStream input = new(compressed);
        using Stream inflater = zlib
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (read > MaxDecompressedBytes - output.Length)
                throw new PacketTooLargeException();
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0)
            throw new InvalidDataException("inflated to nothing");
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    // Same rules as CaptureProxy._TryUnpack: trailing '=' ignored, characters outside the alphabet count as 0.
    internal static byte[] DecodeBase64Lenient(ReadOnlySpan<char> text)
    {
        int end = text.Length;
        while (end > 0 && text[end - 1] == '=')
            end--;

        byte[] bytes = new byte[end * 3 / 4];
        int bits = 0, bitCount = 0, count = 0;
        for (int i = 0; i < end; i++)
        {
            char c = text[i];
            if (c > 255)
                throw new InvalidDataException("Invalid compressed packet character.");
            bits = (bits << 6) | Math.Max(0, Base64Alphabet.IndexOf(c));
            bitCount += 6;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                bytes[count++] = (byte)(bits >> bitCount);
            }
        }
        return count == bytes.Length ? bytes : bytes[..count];
    }

    private sealed class PacketTooLargeException : IOException
    {
        public PacketTooLargeException()
            : base($"message inflates past {MaxDecompressedBytes} bytes")
        {
        }
    }

    [GeneratedRegex("action\\s*=\\s*['\"]([^'\"]+)['\"]")]
    private static partial Regex XmlActionRegex();

    [GeneratedRegex("<body[^>]*\\sr\\s*=\\s*['\"](-?\\d+)['\"]")]
    private static partial Regex XmlRoomRegex();
}
