using Skua.Core.Utils;
using System.Collections;
using System.Dynamic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Flash;

/// <summary>
/// What crosses the ExternalInterface boundary, converted the way the Windows FlashUtil does it: arguments go out as
/// Flash XML values (<c>ToFlashXml</c>) and results come back through <c>FromFlashXml</c> and
/// <c>ConvertParsedValue</c>. A callback here returns an "AS3 value": a <see cref="string"/>, an <see cref="int"/> or
/// <see cref="double"/>, a <see cref="bool"/>, or null for <c>undefined</c>/<c>void</c>.
/// </summary>
internal static class As3
{
    /// <summary>An argument as the AS3 side receives it (ToFlashXml: objects other than the handled ones become their ToString()).</summary>
    public static JsonNode? ToNode(object? arg)
    {
        switch (arg)
        {
            case null:
                return null;
            case JsonNode n:
                return n.DeepClone();
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case int or long or short or byte:
                return JsonValue.Create(System.Convert.ToInt64(arg, CultureInfo.InvariantCulture));
            case float or double or decimal:
                return JsonValue.Create(System.Convert.ToDouble(arg, CultureInfo.InvariantCulture));
            case ExpandoObject e:
                JsonObject o = new();
                foreach (KeyValuePair<string, object?> pair in (IDictionary<string, object?>)e)
                    o[pair.Key] = ToNode(pair.Value);
                return o;
            case Array a:
                JsonArray arr = new();
                foreach (object? x in a)
                    arr.Add(ToNode(x));
                return arr;
            default:
                return JsonValue.Create(arg.ToString() ?? string.Empty);
        }
    }

    /// <summary>AS3 <c>String(x)</c>.</summary>
    public static string Str(object?[] args, int index, string fallback = "")
    {
        if (index >= args.Length || args[index] is null)
            return fallback;
        return args[index] switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            object o => ToNode(o) is JsonValue v && v.TryGetValue(out string? text) ? text : o.ToString() ?? string.Empty,
            null => fallback,
        };
    }

    /// <summary>AS3 <c>int(x)</c>: numbers truncate, numeric strings parse, anything else is 0.</summary>
    public static int Int(object?[] args, int index, int fallback = 0)
    {
        if (index >= args.Length || args[index] is null)
            return fallback;
        object a = args[index]!;
        switch (a)
        {
            case int i:
                return i;
            case bool b:
                return b ? 1 : 0;
            case IConvertible when a is not string:
                try
                {
                    return (int)Math.Truncate(System.Convert.ToDouble(a, CultureInfo.InvariantCulture));
                }
                catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
                {
                    return fallback;
                }
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d):
                return (int)Math.Truncate(d);
            default:
                return 0;
        }
    }

    /// <summary>AS3 truthiness for the flags Skua passes (bool, "true", non-zero numbers).</summary>
    public static bool Bool(object?[] args, int index, bool fallback = false)
    {
        if (index >= args.Length || args[index] is null)
            return fallback;
        return args[index] switch
        {
            bool b => b,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
            int i => i != 0,
            IConvertible c => System.Convert.ToDouble(c, CultureInfo.InvariantCulture) != 0,
            _ => true,
        };
    }

    /// <summary>A JSON-text argument (skua passes items as JsonConvert strings).</summary>
    public static JsonObject? JsonArg(object?[] args, int index)
    {
        if (index >= args.Length || args[index] is null)
            return null;
        if (args[index] is string s)
        {
            if (s.Length == 0)
                return null;
            try
            {
                return JsonNode.Parse(s) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return ToNode(args[index]) as JsonObject;
    }

    /// <summary><c>JSON.stringify(value)</c>; null (undefined) for a missing value.</summary>
    public static string? Stringify(JsonNode? node, bool exists = true)
    {
        if (!exists)
            return null;
        return node is null ? "null" : node.ToJsonString();
    }

    /// <summary>A number result as FromFlashXml parses <c>&lt;number&gt;</c>: an int when it is one, else a float.</summary>
    public static object Number(double d)
    {
        string text = d.ToString("R", CultureInfo.InvariantCulture);
        if (int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out int i))
            return i;
        return (float)d;
    }

    /// <summary>The Windows FlashUtil's conversion of a parsed result to the type the caller asked for.</summary>
    public static object? Convert(object? rawValue, Type type)
    {
        if (rawValue is null)
            return DefaultProvider.GetDefault(type);
        if (type.IsInstanceOfType(rawValue))
            return rawValue;
        if (type == typeof(bool))
        {
            string s = rawValue.ToString()?.Trim() ?? string.Empty;
            if (bool.TryParse(s, out bool parsed))
                return parsed;
            if (int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out int iv))
                return iv != 0;
            return s == "1";
        }
        if (type == typeof(int))
        {
            if (rawValue is IConvertible && rawValue is not string)
            {
                try
                {
                    return System.Convert.ToInt32(rawValue, CultureInfo.InvariantCulture);
                }
                catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
                {
                }
            }
            return int.TryParse(rawValue.ToString()?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        }
        if (type == typeof(float))
            return float.TryParse(System.Convert.ToString(rawValue, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out float f) ? f : 0f;
        if (type == typeof(double))
            return double.TryParse(System.Convert.ToString(rawValue, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : 0d;
        if (type == typeof(string))
            return System.Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;
        try
        {
            return System.Convert.ChangeType(rawValue, type, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return DefaultProvider.GetDefault(type);
        }
    }

    public static bool IsEnumerable(object? o) => o is IEnumerable and not string;
}
