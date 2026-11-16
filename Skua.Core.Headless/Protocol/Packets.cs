using System.Globalization;
using System.Text;

namespace Skua.Core.Headless.Protocol;

/// <summary>
/// Builders for client-to-server messages (without the NUL terminator, which the connection adds).
/// </summary>
/// <remarks>
/// String extension messages follow SmartFox's <c>sendXtMessage(..., "str")</c>: <c>%xt%zm%cmd%room%</c> and
/// then each parameter followed by <c>%</c>. So a call with no parameters ends right after the room
/// (<c>%xt%zm%firstJoin%1%</c>) and one empty parameter gives <c>%xt%zm%restRequest%1%%</c>, which is exactly
/// what Skua.Core sends for rest.
/// </remarks>
public static class Packets
{
    /// <summary>The client token that prefixes the login nick.</summary>
    public const string ClientToken = "SPIDER#0001";

    /// <summary>The AQW SmartFox zone.</summary>
    public const string Zone = "zone_master";

    /// <summary>
    /// SmartFox API version sent with verChk. The reference clients disagree (Seraph sends 166, aqx 157); both are
    /// reported to be accepted by the live server. Override through <see cref="Net.AqwConnectionOptions.ApiVersion"/>.
    /// </summary>
    public const string DefaultApiVersion = "166";

    /// <summary>The extension every AQW command goes to.</summary>
    public const string Extension = "zm";

    public static string VersionCheck(string apiVersion = DefaultApiVersion)
    {
        return $"<msg t='sys'><body action='verChk' r='0'><ver v='{apiVersion}' /></body></msg>";
    }

    /// <summary>
    /// The SmartFox login: nick <c>SPIDER#0001~&lt;user&gt;~&lt;clientVersion&gt;</c>, password = the HTTP login's
    /// <c>sToken</c>, zone <c>zone_master</c>.
    /// </summary>
    public static string Login(string username, string clientVersion, string token, string zone = Zone)
    {
        return $"<msg t='sys'><body action='login' r='0'><login z='{zone}'>"
            + $"<nick>{CData($"{ClientToken}~{username}~{clientVersion}")}</nick>"
            + $"<pword>{CData(token)}</pword></login></body></msg>";
    }

    /// <summary>Any extension command: <c>%xt%zm%&lt;cmd&gt;%&lt;room&gt;%&lt;arg&gt;%...</c>.</summary>
    public static string Xt(string command, int room, params object?[] args)
    {
        StringBuilder sb = new();
        sb.Append("%xt%").Append(Extension).Append('%').Append(command).Append('%')
            .Append(room.ToString(CultureInfo.InvariantCulture)).Append('%');
        foreach (object? arg in args)
            sb.Append(Format(arg)).Append('%');
        return sb.ToString();
    }

    public static string FirstJoin()
    {
        return Xt("firstJoin", 1);
    }

    /// <summary>Map transfer, as Skua.Core's ScriptMap sends it.</summary>
    public static string Transfer(int room, string username, string map, string cell = "Enter", string pad = "Spawn")
    {
        return Xt("cmd", room, "tfer", username, map, cell, pad);
    }

    public static string MoveToCell(int room, string cell, string pad)
    {
        return Xt("moveToCell", room, cell, pad);
    }

    public static string RetrieveUserData(int room, int userId)
    {
        return Xt("retrieveUserData", room, userId);
    }

    public static string RetrieveUserDatas(int room, IEnumerable<int> userIds)
    {
        return Xt("retrieveUserDatas", room, userIds.Cast<object?>().ToArray());
    }

    public static string RetrieveInventory(int room, int userId)
    {
        return Xt("retrieveInventory", room, userId);
    }

    /// <summary>
    /// Generic action request (skills, auto attack, potions). Target is <c>m:&lt;MonMapID&gt;</c> or
    /// <c>p:&lt;uid&gt;</c>; several targets give <c>a1&gt;m:5,a1&gt;m:6</c>. Room is 1 in both reference clients.
    /// </summary>
    public static string Gar(int actionId, string skillRef, IEnumerable<string> targets)
    {
        string list = string.Join(',', targets.Select(t => $"{skillRef}>{t}"));
        if (list.Length == 0)
            list = skillRef + ">";
        return Xt("gar", 1, actionId, list, "wvz");
    }

    public static string AggroMonsters(int room, IEnumerable<int> monMapIds)
    {
        return Xt("aggroMon", room, monMapIds.Cast<object?>().ToArray());
    }

    public static string GetQuests(int room, IEnumerable<int> questIds)
    {
        return Xt("getQuests", room, questIds.Cast<object?>().ToArray());
    }

    public static string AcceptQuest(int room, int questId)
    {
        return Xt("acceptQuest", room, questId);
    }

    /// <summary>Turn-in, as Skua.Core's ScriptQuest sends it.</summary>
    public static string TryQuestComplete(int room, int questId, int rewardItemId = -1, bool special = false, int turnIns = 1)
    {
        return Xt("tryQuestComplete", room, questId, rewardItemId, special, turnIns, "wvz");
    }

    /// <summary>Bank load, as Skua.Core's ScriptBank sends it.</summary>
    public static string LoadBank(int room, string category = "All")
    {
        return Xt("loadBank", room, category);
    }

    public static string GetDrop(int room, int itemId)
    {
        return Xt("getDrop", room, itemId);
    }

    public static string LoadShop(int room, int shopId)
    {
        return Xt("loadShop", room, shopId);
    }

    public static string GetMapItem(int room, int mapItemId)
    {
        return Xt("getMapItem", room, mapItemId);
    }

    public static string Rest()
    {
        return Xt("restRequest", 1, string.Empty);
    }

    public static string Respawn(int room, int userId)
    {
        return Xt("resPlayerTimed", room, userId);
    }

    /// <summary>Clears (or sets) the AFK flag, as the game's World.afkPostpone does.</summary>
    public static string Afk(bool away)
    {
        return Xt("afk", 1, away);
    }

    public static string Logout()
    {
        return Xt("cmd", 1, "logout");
    }

    /// <summary>The command of an outbound <c>%xt%zm%cmd%...</c> string, or null.</summary>
    public static string? CommandOf(string packet)
    {
        if (!packet.StartsWith("%xt%", StringComparison.Ordinal))
            return null;
        int start = packet.IndexOf('%', 4);
        if (start < 0)
            return null;
        int end = packet.IndexOf('%', start + 1);
        return end < 0 ? null : packet[(start + 1)..end];
    }

    /// <summary>The fields of an outbound xt string after the room (same rules as inbound parsing).</summary>
    public static string[] ArgsOf(string packet)
    {
        string[] fields = packet.Split('%');
        // ["", "xt", "zm", cmd, room, args..., ""]
        int end = packet.EndsWith('%') ? fields.Length - 1 : fields.Length;
        return end > 5 ? fields[5..end] : Array.Empty<string>();
    }

    private static string Format(object? arg)
    {
        return arg switch
        {
            null => string.Empty,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => arg.ToString() ?? string.Empty,
        };
    }

    private static string CData(string value)
    {
        // A literal "]]>" cannot appear inside one CDATA section; split it across two.
        return "<![CDATA[" + value.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal) + "]]>";
    }
}
