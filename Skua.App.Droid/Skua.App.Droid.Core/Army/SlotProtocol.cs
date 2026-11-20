using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Skua.App.Droid.Core.Engine;

namespace Skua.App.Droid.Core.Army;

/// <summary>What a <see cref="SlotMessage"/> is.</summary>
public enum SlotMessageKind
{
    /// <summary>Coordinator to slot: do something, answer with the same <see cref="SlotMessage.Id"/>.</summary>
    Request,

    /// <summary>Slot to coordinator: the answer to a request.</summary>
    Response,

    /// <summary>Slot to coordinator, unasked: a snapshot or log lines.</summary>
    Event,
}

/// <summary>
/// One message of the coordinator/slot protocol: UTF-8 JSON, carried in a Binder <c>Message</c> on Android (a
/// <see cref="ISlotChannel"/> in tests). Never carries a password or a login token: a slot reads its account's password
/// from the Keystore vault itself (<see cref="SlotOps.Login"/> names the account by its id).
/// </summary>
public sealed record SlotMessage
{
    public int V { get; init; } = SlotProtocol.Version;

    public SlotMessageKind Kind { get; init; }

    /// <summary>Correlates a response with its request (0 for events).</summary>
    public int Id { get; init; }

    /// <summary>The operation (<see cref="SlotOps"/>) or the event name (<see cref="SlotEvents"/>).</summary>
    public string Op { get; init; } = string.Empty;

    public JsonObject? Args { get; init; }

    /// <summary>Responses: the request succeeded.</summary>
    public bool Ok { get; init; }

    /// <summary>Responses: the user-presentable reason it failed (an <see cref="EngineException"/> message).</summary>
    public string? Error { get; init; }

    public JsonNode? Result { get; init; }
}

/// <summary>The operations a slot answers.</summary>
public static class SlotOps
{
    public const string Hello = "hello";
    public const string Snapshot = "snapshot";
    public const string Log = "log";
    public const string Servers = "servers";
    public const string Login = "login";
    public const string Logout = "logout";
    public const string JoinMap = "joinMap";
    public const string GotoPlayer = "gotoPlayer";
    public const string LoadScript = "loadScript";
    public const string StartScript = "startScript";
    public const string StopScript = "stopScript";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string WorldLists = "worldLists";
    public const string AcceptQuests = "acceptQuests";
    public const string AutoReconnect = "autoReconnect";
    public const string Restart = "restart";
    public const string Shutdown = "shutdown";
}

/// <summary>The events a slot pushes.</summary>
public static class SlotEvents
{
    public const string Snapshot = "snapshot";
    public const string Log = "log";
}

/// <summary>A malformed, oversized or incompatible message.</summary>
public sealed class SlotProtocolException : Exception
{
    public SlotProtocolException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Encoding, limits and the typed payloads of the protocol.</summary>
public static class SlotProtocol
{
    public const int Version = 1;

    /// <summary>
    /// Largest message either side accepts. A Binder transaction buffer is 1 MB per process and shared by every
    /// transaction in flight, so a message stays far below it; log batches and lists are cut to fit.
    /// </summary>
    public const int MaxMessageBytes = 256 * 1024;

    /// <summary>Most log lines in one event or <see cref="SlotOps.Log"/> answer.</summary>
    public const int MaxLogLines = 200;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
    };

    public static byte[] Encode(SlotMessage message)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > MaxMessageBytes)
            throw new SlotProtocolException($"Message {message.Op} is {bytes.Length} bytes, over the {MaxMessageBytes} byte limit.");
        return bytes;
    }

    public static SlotMessage Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            throw new SlotProtocolException("Empty message.");
        if (bytes.Length > MaxMessageBytes)
            throw new SlotProtocolException($"Message of {bytes.Length} bytes is over the {MaxMessageBytes} byte limit.");
        SlotMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<SlotMessage>(bytes, Json);
        }
        catch (JsonException e)
        {
            throw new SlotProtocolException("Malformed message.", e);
        }
        if (message is null)
            throw new SlotProtocolException("Malformed message.");
        if (message.V != Version)
            throw new SlotProtocolException($"Protocol version {message.V} is not {Version}.");
        if (string.IsNullOrEmpty(message.Op) || message.Op.Length > 64)
            throw new SlotProtocolException("Message without a valid operation.");
        return message;
    }

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Json);

    public static T? FromNode<T>(JsonNode? node)
    {
        if (node is null)
            return default;
        try
        {
            return node.Deserialize<T>(Json);
        }
        catch (JsonException e)
        {
            throw new SlotProtocolException($"Malformed {typeof(T).Name} payload.", e);
        }
    }

    public static string? GetString(JsonObject? args, string name, int maxLength = 512)
    {
        if (args?[name] is not JsonValue value)
            return null;
        if (!value.TryGetValue(out string? text))
            throw new SlotProtocolException($"Argument {name} must be text.");
        if (text.Length > maxLength)
            throw new SlotProtocolException($"Argument {name} is too long.");
        return text;
    }

    public static string RequireString(JsonObject? args, string name, int maxLength = 512) =>
        GetString(args, name, maxLength) is { Length: > 0 } text ? text : throw new SlotProtocolException($"Argument {name} is missing.");

    public static bool GetBool(JsonObject? args, string name, bool fallback = false) =>
        args?[name] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    public static int GetInt(JsonObject? args, string name, int fallback = 0) =>
        args?[name] is JsonValue v && v.TryGetValue(out int i) ? i : fallback;
}

/// <summary>What <see cref="SlotOps.Hello"/> answers.</summary>
public sealed record SlotHello(int Slot, int ProcessId, int Protocol, string? AccountId);

/// <summary>What a slot reports about itself next to the engine snapshot (the coordinator's status card).</summary>
/// <param name="Slot">Slot number.</param>
/// <param name="AccountId">The account the slot logged in last (null after a logout).</param>
/// <param name="Snapshot">The engine's snapshot (no secrets).</param>
/// <param name="QueueText">The slot's script queue line, if a queue runs.</param>
public sealed record SlotStatus(int Slot, string? AccountId, EngineSnapshot Snapshot, string? QueueText = null);
