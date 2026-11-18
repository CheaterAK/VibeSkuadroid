using System.Globalization;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Rules;

/// <summary>What the quest rules need to know about the character, as plain lookups.</summary>
public interface IQuestCharacter
{
    /// <summary><c>world.myAvatar.objData</c> (strQuests*, intLevel, iUpgDays, achievement fields).</summary>
    JsonObject ObjData { get; }

    /// <summary>Quantity held in the backpack, house or temporary inventory (<c>world.invTree[id].iQty</c>), 0 if none.</summary>
    int HeldQuantity(int itemId);

    /// <summary>True when the item is equipped.</summary>
    bool IsEquipped(int itemId);

    /// <summary>True when the item is in the backpack or house inventory (<c>Avatar.getItemByID</c>).</summary>
    bool HasItem(int itemId);

    /// <summary>Class points of a class item (<c>Avatar.getCPByID</c>): its quantity, -1 when not owned.</summary>
    int ClassPoints(int itemId);

    /// <summary>Reputation with a faction (<c>Avatar.getRep</c>), 0 when unknown.</summary>
    long Reputation(int factionId);
}

/// <summary>
/// Quest progress and completion rules of the game client (World.as): quest slot values stored base-36 in the
/// <c>strQuests</c>, <c>strQuests2</c> ... strings, achievement bits, turn-in limits and the completable test.
/// </summary>
/// <remarks>
/// Ported from Seraph app/lib/skua/swfworld.js (VWorld.getQuestValue, setQuestValue, getAchievement,
/// maximumQuestTurnIns, checkAllQuestStatus, questAcceptable), MIT, which follows the decompiled World.as.
/// </remarks>
public static class QuestRules
{
    /// <summary>Upper bound the game puts on <see cref="MaximumTurnIns"/>.</summary>
    public const int MaxTurnIns = 250;

    /// <summary>The objData field and character index that hold slot <paramref name="slot"/>.</summary>
    public static (string Field, int Index) SlotLocation(int slot)
    {
        int bank = slot / 100;
        return (bank > 0 ? $"strQuests{bank + 1}" : "strQuests", slot - bank * 100);
    }

    /// <summary>
    /// World.getQuestValue: the base-36 digit at <paramref name="slot"/>; -1 when the string is missing, 0 past its end.
    /// </summary>
    public static int GetQuestValue(JsonObject objData, int slot)
    {
        if (slot < 0)
            return -1;
        (string field, int index) = SlotLocation(slot);
        if (objData[field] is not JsonValue v || !v.TryGetValue(out string? s) || s is null)
            return -1;
        if (index >= s.Length)
            return 0;
        return ParseBase36(s[index]);
    }

    /// <summary>World.setQuestValue (Game.as updateQuest): writes one base-36 digit, padding with '0'.</summary>
    public static void SetQuestValue(JsonObject objData, int slot, int value)
    {
        if (slot < 0)
            return;
        (string field, int index) = SlotLocation(slot);
        string s = JsonUtil.AsString(objData[field]) ?? string.Empty;
        if (s.Length <= index)
            s = s.PadRight(index + 1, '0');
        objData[field] = s[..index] + ToBase36(value) + s[(index + 1)..];
    }

    /// <summary>Applies <c>updateQuest</c> values received after the character data was loaded.</summary>
    public static void ApplyUpdates(JsonObject objData, IReadOnlyDictionary<int, int> updates)
    {
        foreach (KeyValuePair<int, int> u in updates.OrderBy(x => x.Key))
            SetQuestValue(objData, u.Key, u.Value);
    }

    /// <summary>World.getAchievement: bit <paramref name="index"/> of objData[field]; -1 when unknown.</summary>
    public static int GetAchievement(JsonObject objData, string field, int index)
    {
        if (index < 0 || index > 31 || string.IsNullOrEmpty(field))
            return -1;
        long? v = JsonUtil.AsLongOrNull(objData[field]);
        if (v is null)
            return -1;
        return (v.Value & (1L << index)) == 0 ? 0 : 1;
    }

    /// <summary>Game.as setAchievement.</summary>
    public static void SetAchievement(JsonObject objData, string field, int index, bool on)
    {
        long v = JsonUtil.AsLong(objData[field]);
        objData[field] = on ? v | (1L << index) : v & ~(1L << index);
    }

    /// <summary>
    /// World.maximumQuestTurnIns: how many times the quest can be turned in with what is held. 0 when any requirement
    /// is short or equipped, -1 when the quest has no turn-in items, never more than <see cref="MaxTurnIns"/>.
    /// </summary>
    public static int MaximumTurnIns(JsonObject? quest, IQuestCharacter character)
    {
        int max = -1;
        if (quest?["turnin"] is JsonArray turnin && turnin.Count > 0)
        {
            foreach (JsonObject t in turnin.OfType<JsonObject>())
            {
                int id = JsonUtil.AsInt(t["ItemID"]);
                int need = Math.Max(1, JsonUtil.AsInt(t["iQty"], 1));
                int held = character.HeldQuantity(id);
                if (held < need || character.IsEquipped(id))
                    return 0;
                int n = held / need;
                if (max == -1 || max > n)
                    max = n;
            }
        }
        return Math.Min(max, MaxTurnIns);
    }

    /// <summary>World.checkAllQuestStatus for one quest in progress: every turn-in item is held in full.</summary>
    public static bool IsCompletable(JsonObject quest, IQuestCharacter character, DateTimeOffset now)
    {
        if (quest["turnin"] is JsonArray turnin)
        {
            foreach (JsonObject t in turnin.OfType<JsonObject>())
            {
                if (character.HeldQuantity(JsonUtil.AsInt(t["ItemID"])) < JsonUtil.AsInt(t["iQty"], 1))
                    return false;
            }
        }
        // Timed quests (iTime, ms) expire unless their accept time stamp (ts) is recent enough.
        if (JsonUtil.AsLongOrNull(quest["iTime"]) is long time && time > 0)
        {
            long? ts = JsonUtil.AsLongOrNull(quest["ts"]);
            if (ts is null || now.ToUnixTimeMilliseconds() - ts.Value > time)
                return false;
        }
        return true;
    }

    /// <summary>The status the client shows: null (not accepted), "p" (in progress) or "c" (can be completed).</summary>
    public static string? Status(JsonObject quest, bool accepted, IQuestCharacter character, DateTimeOffset now)
    {
        if (!accepted)
            return null;
        return IsCompletable(quest, character, now) ? "c" : "p";
    }

    /// <summary>
    /// Game.getQuestValidationString, the parts that decide whether a quest can be accepted: membership, level,
    /// faction reputation, class points and items required to accept (<c>oReqd</c>). Returns the first unmet reason,
    /// or null when it can be accepted.
    /// </summary>
    public static string? AcceptRefusal(JsonObject quest, IQuestCharacter character)
    {
        JsonObject o = character.ObjData;
        if (JsonUtil.AsBool(quest["bUpg"]) && JsonUtil.AsInt(o["iUpgDays"], -1) < 0)
            return "Member only.";
        if (JsonUtil.AsInt(quest["iLvl"]) > JsonUtil.AsInt(o["intLevel"]))
            return $"Requires level {JsonUtil.AsInt(quest["iLvl"])}.";
        int faction = JsonUtil.AsInt(quest["FactionID"]);
        if (faction > 1 && character.Reputation(faction) < JsonUtil.AsLong(quest["iReqRep"]))
            return "Requires more reputation.";
        int classId = JsonUtil.AsInt(quest["iClass"]);
        if (classId > 0 && character.ClassPoints(classId) < JsonUtil.AsInt(quest["iReqCP"]))
            return "Requires more class points.";
        if (quest["oReqd"] is JsonObject required)
        {
            foreach (KeyValuePair<string, JsonNode?> r in required)
            {
                if (int.TryParse(r.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && !character.HasItem(id))
                    return "Missing a required item.";
            }
        }
        return null;
    }

    /// <summary>One base-36 digit as the game parses it (<c>parseInt(c, 36)</c>, 0 when not a digit).</summary>
    public static int ParseBase36(char c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'a' && c <= 'z')
            return c - 'a' + 10;
        if (c >= 'A' && c <= 'Z')
            return c - 'A' + 10;
        return 0;
    }

    /// <summary><c>Number(v).toString(36)</c> for the 0..35 range quest slots use.</summary>
    public static string ToBase36(int value)
    {
        if (value < 0)
            return "0";
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value < 36)
            return digits[value].ToString();
        // A slot value above 35 cannot fit one character; the game would write two and shift the string. Clamp.
        return "z";
    }
}
