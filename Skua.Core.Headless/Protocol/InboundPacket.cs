using System.Text.Json;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Protocol;

/// <summary>The three forms a server message takes on the AQW socket.</summary>
public enum PacketKind
{
    /// <summary>Not recognisable (does not start with <c>%</c>, <c>{</c> or <c>&lt;</c>, or failed to parse).</summary>
    Unknown,

    /// <summary>SmartFox string extension message: <c>%xt%&lt;cmd&gt;%&lt;room&gt;%&lt;args...&gt;%</c>.</summary>
    Xt,

    /// <summary>SmartFox JSON extension message: <c>{"t":"xt","b":{"r":&lt;room&gt;,"o":{"cmd":...}}}</c>.</summary>
    Json,

    /// <summary>SmartFox system message: <c>&lt;msg t='sys'&gt;&lt;body action='...' r='...'&gt;...</c>.</summary>
    Xml,
}

/// <summary>
/// One decoded server message. Instances are shared with every event subscriber; treat <see cref="Data"/> as
/// read only (the world model keeps its own copies).
/// </summary>
public sealed class InboundPacket
{
    private static readonly JsonSerializerOptions s_compact = new() { WriteIndented = false };

    internal InboundPacket(PacketKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public PacketKind Kind { get; }

    /// <summary>
    /// The message text after decompression. This is what the Flash client sees after
    /// <c>SmartFoxClient.handleMessage</c> unpacks it, and what Skua's <c>packetFromServer</c> event carries.
    /// </summary>
    public string Text { get; }

    /// <summary>True when the message arrived as <c>"Z" + base64(zlib(text))</c>.</summary>
    public bool WasCompressed { get; internal init; }

    /// <summary>
    /// True when the message looked compressed (leading <c>Z</c>) but would not inflate. <see cref="Text"/> is then
    /// the raw wire text and <see cref="Kind"/> is <see cref="PacketKind.Unknown"/>.
    /// </summary>
    public bool DecompressionFailed { get; internal init; }

    /// <summary>Why decoding failed, if it did (for logs and gap measurement).</summary>
    public string? DecodeError { get; internal init; }

    /// <summary>
    /// The command: the xt command (<c>uotls</c>), the JSON <c>b.o.cmd</c> (<c>moveToArea</c>) or the XML body
    /// action (<c>apiOK</c>). Null when there is none.
    /// </summary>
    public string? Command { get; internal init; }

    /// <summary>The room field (<c>-1</c> for most AQW extension replies), when present and numeric.</summary>
    public int? Room { get; internal init; }

    /// <summary>For <see cref="PacketKind.Xt"/>: the fields after the room, with empty fields kept in place.</summary>
    public IReadOnlyList<string> Args { get; internal init; } = Array.Empty<string>();

    /// <summary>For <see cref="PacketKind.Json"/>: the <c>b.o</c> object (the payload the game reads).</summary>
    public JsonObject? Data { get; internal init; }

    /// <summary>For <see cref="PacketKind.Xml"/>: the user id named by <c>&lt;u i='..'&gt;</c> or <c>&lt;user id='..'&gt;</c>.</summary>
    public int? XmlUserId { get; internal init; }

    /// <summary>For <see cref="PacketKind.Xml"/>: the user name inside <c>&lt;n&gt;</c>, when present.</summary>
    public string? XmlUserName { get; internal init; }

    /// <summary>Gets an xt argument, or null when the message has fewer.</summary>
    public string? Arg(int index)
    {
        return index >= 0 && index < Args.Count ? Args[index] : null;
    }

    /// <summary>
    /// The JSON the Flash side hands to Skua as the single argument of the <c>pext</c> call: skua.swf does
    /// <c>JSON.stringify(SFSEvent onExtensionResponse)</c>, giving
    /// <c>{"type":"onExtensionResponse","params":{"type":"json"|"str","dataObj":...}}</c>, where dataObj is
    /// <c>b.o</c> for JSON messages and <c>[cmd, room, args...]</c> for string messages. Null for XML/unknown
    /// messages, which skua.swf never reports as extension responses.
    /// </summary>
    public string? ToExtensionResponseJson()
    {
        JsonNode? dataObj;
        string type;
        switch (Kind)
        {
            case PacketKind.Json when Data is not null:
                type = "json";
                dataObj = Data.DeepClone();
                break;

            case PacketKind.Xt:
                type = "str";
                JsonArray array = new() { Command, Room?.ToString() ?? RawRoomField ?? string.Empty };
                foreach (string arg in Args)
                    array.Add(arg);
                dataObj = array;
                break;

            default:
                return null;
        }

        JsonObject packet = new()
        {
            ["type"] = "onExtensionResponse",
            ["params"] = new JsonObject
            {
                ["type"] = type,
                ["dataObj"] = dataObj,
            },
        };
        return packet.ToJsonString(s_compact);
    }

    /// <summary>The room field exactly as sent, for xt messages whose room is not numeric.</summary>
    internal string? RawRoomField { get; init; }

    public override string ToString()
    {
        string body = Text.Length > 200 ? Text[..200] + "..." : Text;
        return $"{Kind} {Command ?? "?"}: {body}";
    }
}
