using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Tests.Fakes;

/// <summary>
/// A synthetic bank in the shape of the game's <c>api/char/bank</c> reply: item objects with the ~45 fields real AQW
/// items carry (names, long descriptions, file and link names, enhancement and requirement fields, meta), so a test
/// can measure what a 3000- or 5000-item bank costs. Deterministic (seeded); no real account data.
/// </summary>
public static class LargeBank
{
    private static readonly string[] s_types = ["Item", "Armor", "Helm", "Cape", "Sword", "Dagger", "Staff", "Axe", "Pet", "Misc", "Resource", "Quest Item", "Necklace", "Wall Item", "Floor Item"];
    private static readonly string[] s_slots = ["None", "ar", "co", "he", "ba", "Weapon", "pe", "am", "mi"];
    private static readonly string[] s_words = ["ancient", "shadow", "light", "dragon", "void", "chaos", "legion", "nulgath", "frost", "flame", "doom", "star", "blade", "crystal", "dark", "spirit",
        "eternal", "golden", "infernal", "celestial", "abyssal", "primal", "arcane", "radiant", "corrupted", "elemental", "dimensional", "temporal", "hollowborn", "obsidian"];

    /// <summary>The bank's items (as the reply carries them), ItemIDs from <paramref name="firstId"/> up.</summary>
    public static List<JsonObject> Items(int count, int firstId = 100000, int seed = 20261125)
    {
        Random r = new(seed);
        List<JsonObject> items = new(count);
        for (int i = 0; i < count; i++)
            items.Add(Item(r, firstId + i, 90000000 + i));
        return items;
    }

    /// <summary>The reply body for <paramref name="items"/> (a JSON array, as the server sends it).</summary>
    public static string Body(IEnumerable<JsonObject> items) => new JsonArray(items.Select(i => (JsonNode)i.DeepClone()).ToArray()).ToJsonString();

    private static string Words(Random r, int min, int max)
    {
        int n = r.Next(min, max + 1);
        StringBuilder sb = new();
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(s_words[r.Next(s_words.Length)]);
        }
        return sb.ToString();
    }

    private static string Title(string s) => string.Join(' ', s.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static JsonObject Item(Random r, int itemId, int charItemId)
    {
        string type = s_types[r.Next(s_types.Length)];
        bool stackable = type is "Item" or "Resource" or "Quest Item" or "Misc";
        string name = Title(Words(r, 2, 4)) + " " + itemId.ToString(CultureInfo.InvariantCulture);
        string file = Words(r, 1, 2).Replace(' ', '_');
        // Descriptions in the live bank run from one line to several paragraphs (lore, drop sources, notes).
        string desc = Title(Words(r, 1, 1)) + " " + Words(r, 20, 110) + ". " + (r.Next(3) == 0 ? "Note: " + Words(r, 8, 30) + "." : string.Empty);
        int stack = stackable ? (r.Next(4) == 0 ? 1000 : r.Next(2) == 0 ? 500 : 99) : 1;
        return new JsonObject
        {
            ["ItemID"] = itemId,
            ["CharItemID"] = charItemId,
            ["sName"] = name,
            ["sDesc"] = desc,
            ["sFile"] = stackable ? string.Empty : $"items/{type.ToLowerInvariant()}s/{file}{r.Next(1, 30)}.swf",
            ["sLink"] = stackable ? string.Empty : Title(file).Replace(' ', '_') + r.Next(1, 30),
            ["sElmt"] = r.Next(5) == 0 ? "Fire" : "None",
            ["sType"] = type,
            ["sES"] = stackable ? "None" : s_slots[r.Next(1, s_slots.Length)],
            ["sIcon"] = stackable ? "iibag" : "iw" + type.ToLowerInvariant(),
            ["iCost"] = r.Next(0, 500000),
            ["iRng"] = r.Next(0, 100),
            ["iDPS"] = r.Next(0, 100),
            ["iLvl"] = r.Next(1, 101),
            ["iStk"] = stack,
            ["iQty"] = stackable ? r.Next(1, stack + 1) : 1,
            ["iRty"] = r.Next(10, 100),
            ["iHrs"] = r.Next(0, 5000),
            ["iEnh"] = stackable ? 0 : r.Next(0, 80000),
            ["EnhID"] = stackable ? 0 : r.Next(0, 80000),
            ["EnhLvl"] = stackable ? 0 : r.Next(0, 101),
            ["EnhDPS"] = stackable ? 0 : 100,
            ["EnhRng"] = stackable ? 0 : r.Next(0, 100),
            ["EnhRty"] = stackable ? 0 : r.Next(10, 100),
            ["EnhPatternID"] = stackable ? 0 : r.Next(1, 30),
            ["ProcID"] = r.Next(0, 3) == 0 ? r.Next(1, 200) : 0,
            ["PatternID"] = stackable ? 0 : r.Next(1, 30),
            ["bCoins"] = r.Next(8) == 0 ? 1 : 0,
            ["bUpg"] = r.Next(3) == 0 ? 1 : 0,
            ["bStaff"] = 0,
            ["bTemp"] = 0,
            ["bBank"] = 1,
            ["bEquip"] = 0,
            ["bHouse"] = type is "Wall Item" or "Floor Item" ? 1 : 0,
            ["bWear"] = 0,
            ["sReqQuests"] = r.Next(6) == 0 ? string.Join(",", Enumerable.Range(0, r.Next(1, 4)).Select(_ => r.Next(1000, 9999))) : string.Empty,
            ["iQSindex"] = r.Next(4) == 0 ? r.Next(1, 300) : -1,
            ["iQSvalue"] = r.Next(4) == 0 ? r.Next(1, 100) : 0,
            ["iClass"] = 0,
            ["iReqCP"] = r.Next(5) == 0 ? r.Next(1, 302500) : 0,
            ["iReqRep"] = r.Next(5) == 0 ? r.Next(1, 302500) : 0,
            ["FactionID"] = r.Next(5) == 0 ? r.Next(1, 80) : 1,
            ["sFaction"] = r.Next(5) == 0 ? Title(Words(r, 1, 2)) : "None",
            ["sMeta"] = r.Next(4) == 0 ? $"{Title(Words(r, 1, 1))}:{r.Next(1, 50)},{Title(Words(r, 1, 1))}:{r.Next(1, 50)}" : string.Empty,
            ["dPurchase"] = new DateTime(2012, 1, 1).AddDays(r.Next(0, 5000)).AddSeconds(r.Next(86400)).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ["iRank"] = 0,
            ["bMember"] = r.Next(3) == 0 ? 1 : 0,
        };
    }
}
