using System.Text.Json;

namespace RuffleSpike.Core;

/// <summary>A game server endpoint (the <c>sIP</c> and <c>iPort</c> of a server list entry).</summary>
public readonly record struct GameEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>
/// The only TCP endpoints the socket bridge may connect to: the host:port pairs of the live server list
/// (https://game.aq.com/game/api/data/servers). A host must also be a plain DNS name under one of
/// <see cref="AllowedDomains"/>, so even a tampered list cannot turn the bridge into a proxy to anything else.
/// </summary>
public sealed class ServerAllowlist
{
    /// <summary>Domains the game servers live under (2026-11: sock7/sock8.aq.com, euro.aqw.artix.com, asia.game.artix.com).</summary>
    public static readonly IReadOnlyList<string> AllowedDomains = ["aq.com", "artix.com"];

    private readonly HashSet<GameEndpoint> _endpoints;

    public ServerAllowlist(IEnumerable<GameEndpoint> endpoints)
    {
        _endpoints = [];
        foreach (GameEndpoint e in endpoints)
            if (TryNormalize(e.Host, e.Port, out GameEndpoint n)) _endpoints.Add(n);
    }

    public IReadOnlyCollection<GameEndpoint> Endpoints => _endpoints;

    public bool IsAllowed(string host, int port) =>
        TryNormalize(host, port, out GameEndpoint n) && _endpoints.Contains(n);

    /// <summary>Parses the server list JSON (an array of objects with <c>sIP</c> and <c>iPort</c>); bad entries are skipped.</summary>
    public static ServerAllowlist Parse(string json, out int skipped)
    {
        skipped = 0;
        List<GameEndpoint> list = [];
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("server list is not a JSON array");
        foreach (JsonElement s in doc.RootElement.EnumerateArray())
        {
            if (s.ValueKind == JsonValueKind.Object
                && s.TryGetProperty("sIP", out JsonElement ip) && ip.ValueKind == JsonValueKind.String
                && s.TryGetProperty("iPort", out JsonElement port) && TryPort(port, out int p)
                && TryNormalize(ip.GetString()!, p, out GameEndpoint e))
                list.Add(e);
            else
                skipped++;
        }
        return new ServerAllowlist(list);
    }

    private static bool TryPort(JsonElement e, out int port)
    {
        port = 0;
        if (e.ValueKind == JsonValueKind.Number) return e.TryGetInt32(out port);
        return e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), System.Globalization.NumberStyles.None, null, out port);
    }

    /// <summary>Lower-cases the host and checks it is a DNS name under an allowed domain and the port is 1..65535.</summary>
    public static bool TryNormalize(string host, int port, out GameEndpoint endpoint)
    {
        endpoint = default;
        if (port is < 1 or > 65535 || string.IsNullOrEmpty(host) || host.Length > 253) return false;
        string h = host.ToLowerInvariant();
        string[] labels = h.Split('.');
        if (labels.Length < 2) return false;
        foreach (string label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            foreach (char c in label)
                if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) return false;
        }
        // All-numeric last label would be an IPv4 address: never allowed.
        if (labels[^1].All(char.IsAsciiDigit)) return false;
        bool inDomain = AllowedDomains.Any(d => h == d || h.EndsWith("." + d, StringComparison.Ordinal));
        if (!inDomain) return false;
        endpoint = new GameEndpoint(h, port);
        return true;
    }
}
