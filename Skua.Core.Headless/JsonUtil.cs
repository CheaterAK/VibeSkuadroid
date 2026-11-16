using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless;

/// <summary>
/// Lenient reads over AQW's JSON, where the same field can arrive as a number, a numeric string or a boolean
/// ("1", 1, true) depending on the packet.
/// </summary>
public static class JsonUtil
{
    public static string? AsString(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static int? AsIntOrNull(JsonNode? node)
    {
        long? l = AsLongOrNull(node);
        return l is null ? null : (int)Math.Clamp(l.Value, int.MinValue, int.MaxValue);
    }

    public static int AsInt(JsonNode? node, int fallback = 0)
    {
        return AsIntOrNull(node) ?? fallback;
    }

    public static long? AsLongOrNull(JsonNode? node)
    {
        double? d = AsDoubleOrNull(node);
        if (d is null || double.IsNaN(d.Value) || double.IsInfinity(d.Value))
            return null;
        return (long)Math.Truncate(d.Value);
    }

    public static long AsLong(JsonNode? node, long fallback = 0)
    {
        return AsLongOrNull(node) ?? fallback;
    }

    public static double? AsDoubleOrNull(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                return value.TryGetValue(out double d) ? d : double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture);
            case JsonValueKind.String:
                string s = value.GetValue<string>();
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : null;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            default:
                return null;
        }
    }

    /// <summary>True for <c>true</c>, non-zero numbers and the strings "true"/"1".</summary>
    public static bool AsBool(JsonNode? node, bool fallback = false)
    {
        if (node is not JsonValue value)
            return fallback;
        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => AsDoubleOrNull(value) is double d && d != 0,
            JsonValueKind.String => value.GetValue<string>() is string s
                && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && n != 0)),
            _ => fallback,
        };
    }

    /// <summary>Deep copy of an object node, or an empty object.</summary>
    public static JsonObject CloneObject(JsonNode? node)
    {
        return node is JsonObject obj ? (JsonObject)obj.DeepClone() : new JsonObject();
    }

    /// <summary>Copies every property of <paramref name="patch"/> onto <paramref name="target"/> (shallow merge, deep copies).</summary>
    public static void Merge(JsonObject target, JsonObject? patch)
    {
        if (patch is null)
            return;
        foreach (KeyValuePair<string, JsonNode?> pair in patch)
            target[pair.Key] = pair.Value?.DeepClone();
    }

    /// <summary>Same lookup by number whether the key is stored as "5" or 5.</summary>
    public static bool SameId(JsonNode? node, long id)
    {
        return AsLongOrNull(node) == id;
    }

    /// <summary>Compact JSON text for a node (null becomes <c>null</c>).</summary>
    public static string ToJson(JsonNode? node)
    {
        return node is null ? "null" : node.ToJsonString();
    }
}
