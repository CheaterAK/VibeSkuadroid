using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Skua.Core.Headless.Rules;

/// <summary>
/// The AQW 5.0 inventory pools (the game's <c>InvCat</c> class, which skua.swf's Inventory.as asks for slot counts
/// and room): the backpack is split into pools. Classes never take a slot; "misc" items (Item, Note, Quest Item,
/// Resource, except an Item whose sMeta is a plain number) have their own cap (<c>loadInventoryBig.iMiscSlots</c>);
/// house items are their own inventory; everything else counts against <c>iBagSlots</c>.
/// </summary>
/// <remarks>Ported from Seraph app/lib/skua/swfworld.js (InvCat, SwfWorld.hasSpaceFor), MIT.</remarks>
public static partial class InventoryRules
{
    /// <summary>The misc cap the game uses when the server did not send one.</summary>
    public const int DefaultMiscSlots = 100;

    /// <summary>An item the game keeps in the bag at quantity 0, so spending it never frees a slot.</summary>
    public const int KeptAtZeroItemId = 45739;

    public static IReadOnlyList<string> MiscTypes { get; } = new[] { "Item", "Note", "Quest Item", "Resource" };

    private static readonly string[] s_houseTypes = { "House", "Wall Item", "Floor Item", "Guild" };

    public static bool IsClass(JsonObject? item) => item is not null && JsonUtil.AsString(item["sType"]) == "Class";

    public static bool IsMisc(JsonObject? item)
    {
        if (item is null)
            return false;
        string? type = JsonUtil.AsString(item["sType"]);
        if (type is null || !MiscTypes.Contains(type))
            return false;
        return !(type == "Item" && JsonUtil.AsString(item["sMeta"]) is string meta && PlainNumber().IsMatch(meta));
    }

    public static bool IsHouse(JsonObject? item)
    {
        if (item is null)
            return false;
        return JsonUtil.AsBool(item["bHouse"]) || s_houseTypes.Contains(JsonUtil.AsString(item["sType"]));
    }

    public static bool IsBag(JsonObject? item) => item is not null && !IsClass(item) && !IsMisc(item) && !IsHouse(item);

    /// <summary>"class", "house", "misc" or "bag".</summary>
    public static string PoolOf(JsonObject? item)
    {
        if (IsClass(item))
            return "class";
        if (IsHouse(item))
            return "house";
        return IsMisc(item) ? "misc" : "bag";
    }

    public static int CountBag(IEnumerable<JsonObject> items) => items.Count(IsBag);

    public static int CountMisc(IEnumerable<JsonObject> items) => items.Count(IsMisc);

    private static JsonObject? Owned(IEnumerable<JsonObject> items, int itemId)
    {
        return items.FirstOrDefault(x => JsonUtil.AsInt(x["ItemID"]) == itemId);
    }

    /// <summary>InvCat.isFullFor: no room for one more of <paramref name="item"/> in its pool.</summary>
    public static bool IsFullFor(IReadOnlyList<JsonObject> items, JsonObject? item, int bagSlots, int freed = 0, int miscSlots = DefaultMiscSlots)
    {
        if (item is null || IsClass(item))
            return false;
        JsonObject? have = Owned(items, JsonUtil.AsInt(item["ItemID"]));
        if (have is not null && JsonUtil.AsInt(have["iQty"]) < JsonUtil.AsInt(have["iStk"]))
            return false;
        if (IsMisc(item))
            return have is null && CountMisc(items) - freed >= miscSlots;
        return CountBag(items) - freed >= bagSlots;
    }

    private static bool IsToken(JsonObject item) => JsonUtil.AsInt(item["TokID"]) > 0 && JsonUtil.AsInt(item["TokQty"]) > 0;

    private static int Bundles(JsonObject item, int quantity)
    {
        int bundle = JsonUtil.AsInt(item["iQty"]) > 0 ? JsonUtil.AsInt(item["iQty"]) : 1;
        return Math.Max(1, quantity / bundle);
    }

    private static int TokenNeed(JsonObject item, int quantity) => (int)(JsonUtil.AsDoubleOrNull(item["TokQty"]).GetValueOrDefault() * Bundles(item, quantity));

    /// <summary>InvCat.freedBy: slots a purchase empties (materials and token used up whole, in the item's own pool).</summary>
    public static int FreedBy(IReadOnlyList<JsonObject> items, JsonObject? item, int quantity)
    {
        if (item is null || Owned(items, JsonUtil.AsInt(item["ItemID"])) is not null)
            return 0;
        string pool = PoolOf(item);
        if (pool is not "bag" and not "misc")
            return 0;
        int bundles = Bundles(item, quantity);
        int freed = 0;
        bool tokenSeen = false;
        int tokenId = JsonUtil.AsInt(item["TokID"]);
        if (item["turnin"] is JsonArray turnin)
        {
            foreach (JsonObject t in turnin.OfType<JsonObject>())
            {
                int id = JsonUtil.AsInt(t["ItemID"]);
                long need = (long)JsonUtil.AsInt(t["iQty"]) * bundles;
                if (IsToken(item) && id == tokenId)
                {
                    tokenSeen = true;
                    need += TokenNeed(item, quantity);
                }
                JsonObject? o = Owned(items, id);
                if (o is not null && id != KeptAtZeroItemId && PoolOf(o) == pool
                    && (JsonUtil.AsString(o["sES"]) == "ar" || JsonUtil.AsInt(o["iQty"]) <= need))
                    freed++;
            }
        }
        if (IsToken(item) && !tokenSeen && tokenId != KeptAtZeroItemId)
        {
            JsonObject? o = Owned(items, tokenId);
            if (o is not null && PoolOf(o) == pool && JsonUtil.AsInt(o["iQty"]) <= TokenNeed(item, quantity))
                freed++;
        }
        return freed;
    }

    /// <summary>
    /// skua.swf Inventory.inventoryHasSpaceFor (with inventory pools): room for <paramref name="item"/> in its pool.
    /// <paramref name="quantity"/> is the amount bought; a purchase counts the slots its materials free;
    /// <paramref name="outgoing"/> is an item a bank swap sends back (same pool = one slot freed).
    /// </summary>
    public static bool HasSpaceFor(JsonObject? item, IReadOnlyList<JsonObject> items, IReadOnlyList<JsonObject> houseItems,
        int bagSlots, int houseSlots, int miscSlots, int quantity = 1, bool purchase = false, JsonObject? outgoing = null)
    {
        if (item is null)
            return false;
        if (IsHouse(item))
            return houseItems.Count < houseSlots;
        int freed = purchase ? FreedBy(items, item, quantity) : 0;
        if (outgoing is not null && PoolOf(outgoing) == PoolOf(item))
            freed++;
        return !IsFullFor(items, item, bagSlots, freed, miscSlots);
    }

    [GeneratedRegex(@"^\s*\d+\s*$")]
    private static partial Regex PlainNumber();
}
