using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Net;

/// <summary>An entry of the AQW server list (login reply <c>servers</c> or <c>api/data/servers</c>).</summary>
public sealed record GameServer(
    string Name,
    string Host,
    int Port,
    int Players,
    int MaxPlayers,
    bool Online,
    bool MembersOnly,
    int MinLevel,
    int AccessLevel,
    string? Language,
    JsonObject? Raw)
{
    public bool IsFull => MaxPlayers > 0 && Players >= MaxPlayers;

    /// <summary>Reads one server object (<c>sName</c>, <c>sIP</c>, <c>iPort</c>, <c>iCount</c>, <c>iMax</c>, <c>bOnline</c>, <c>bUpg</c>, <c>iLevel</c>).</summary>
    public static GameServer? FromJson(JsonObject o)
    {
        string? name = JsonUtil.AsString(o["sName"]);
        string? host = JsonUtil.AsString(o["sIP"]);
        int? port = JsonUtil.AsIntOrNull(o["iPort"]);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host) || port is null or <= 0 or > 65535)
            return null;
        return new GameServer(
            name,
            host,
            port.Value,
            JsonUtil.AsInt(o["iCount"]),
            JsonUtil.AsInt(o["iMax"]),
            JsonUtil.AsBool(o["bOnline"], fallback: true),
            JsonUtil.AsBool(o["bUpg"]),
            JsonUtil.AsInt(o["iLevel"]),
            JsonUtil.AsInt(o["accessLevel"], JsonUtil.AsInt(o["iAccess"])),
            JsonUtil.AsString(o["sLang"]),
            (JsonObject)o.DeepClone());
    }

    public static IReadOnlyList<GameServer> ListFromJson(JsonNode? node)
    {
        List<GameServer> list = new();
        if (node is not JsonArray array)
            return list;
        foreach (JsonNode? entry in array)
        {
            if (entry is JsonObject o && FromJson(o) is GameServer server)
                list.Add(server);
        }
        return list;
    }

    /// <summary>Why this account cannot use the server, or null when it can.</summary>
    public string? Refusal(bool member, int level)
    {
        if (!Online)
            return "is offline";
        if (AccessLevel > 0)
            return "is for staff only";
        if (MembersOnly && !member)
            return "is for members only";
        if (level < MinLevel)
            return $"needs level {MinLevel}";
        if (IsFull)
            return "is full";
        return null;
    }

    public override string ToString()
    {
        return $"{Name} ({Players}/{MaxPlayers}{(Online ? string.Empty : ", offline")})";
    }
}

/// <summary>Chooses a server the account may join.</summary>
public static class ServerPicker
{
    /// <summary>Names of test servers never picked automatically.</summary>
    private static readonly string[] s_excluded = { "class test", "ptr" };

    /// <summary>
    /// The preferred server when usable (an exact name, or a name followed by a region such as
    /// "Swordhaven (EU)"), otherwise the least populated usable server. Null when none is usable.
    /// </summary>
    public static GameServer? Pick(IEnumerable<GameServer> servers, bool member, int level, string? preferred = null)
    {
        List<GameServer> usable = servers
            .Where(s => s.Refusal(member, level) is null)
            .ToList();
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            string want = preferred.Trim();
            GameServer? exact = usable.FirstOrDefault(s => s.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
                ?? usable.FirstOrDefault(s => s.Name.StartsWith(want + " (", StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;
        }
        return usable
            .Where(s => !s_excluded.Any(x => s.Name.Equals(x, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.MaxPlayers > 0 ? (double)s.Players / s.MaxPlayers : 1)
            .ThenBy(s => s.Players)
            .FirstOrDefault();
    }
}
