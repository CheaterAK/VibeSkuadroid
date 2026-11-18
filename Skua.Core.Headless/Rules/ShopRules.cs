using System.Globalization;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Rules;

/// <summary>Shop purchase rules of the game client and skua.swf.</summary>
/// <remarks>
/// Ported from skua.swf Shop.getUnmetPurchaseRequirements (this repository, Skua.AS3) and Seraph
/// app/lib/skua/swfworld.js (VWorld.sendBuyItemRequest, maximumShopBuys), MIT.
/// </remarks>
public static class ShopRules
{
    /// <summary>
    /// skua.swf Shop.getUnmetPurchaseRequirements: what the backpack lacks for one purchase (banked materials do
    /// not count). Empty when the purchase can go ahead.
    /// </summary>
    /// <param name="item">The shop item, or null when it is not in the loaded shop.</param>
    /// <param name="quantity">Amount bought, -1 for one bundle.</param>
    /// <param name="held">Held quantity and name of an item in the backpack/temp inventory, or null when not held.</param>
    /// <param name="questValue">World.getQuestValue.</param>
    public static List<string> UnmetPurchaseRequirements(JsonObject? item, int quantity, Func<int, (int Quantity, string? Name)?> held, Func<int, int> questValue)
    {
        if (item is null)
            return new() { "Item is not in the loaded shop." };
        List<string> missing = new();
        int bundle = Math.Max(1, JsonUtil.AsInt(item["iQty"]));
        int purchased = quantity == -1 ? bundle : quantity;
        if (purchased <= 0)
            return new() { "Purchase quantity must be positive." };
        if (JsonUtil.AsDoubleOrNull(item["iQSindex"]) is double qsIndex && qsIndex >= 0
            && questValue((int)qsIndex) < JsonUtil.AsDoubleOrNull(item["iQSvalue"]).GetValueOrDefault())
            missing.Add("Quest requirement not met.");

        int bundles = Math.Max(1, purchased / bundle);
        SortedDictionary<int, double> required = new();
        Dictionary<int, string?> names = new();
        List<int> order = new();
        if (item["turnin"] is JsonArray turnin)
        {
            foreach (JsonObject m in turnin.OfType<JsonObject>())
            {
                int id = JsonUtil.AsInt(m["ItemID"]);
                if (!required.ContainsKey(id))
                    order.Add(id);
                required[id] = required.GetValueOrDefault(id) + JsonUtil.AsDoubleOrNull(m["iQty"]).GetValueOrDefault() * bundles;
                names[id] = JsonUtil.AsString(m["sName"]);
            }
        }
        if (JsonUtil.AsInt(item["TokID"]) > 0 && JsonUtil.AsInt(item["TokQty"]) > 0)
        {
            int id = JsonUtil.AsInt(item["TokID"]);
            if (!required.ContainsKey(id))
                order.Add(id);
            required[id] = required.GetValueOrDefault(id) + (int)(JsonUtil.AsDoubleOrNull(item["TokQty"]).GetValueOrDefault() * bundles);
            if (JsonUtil.AsString(item["TokName"]) is string tokName && tokName.Length > 0)
                names[id] = tokName;
        }
        // AS3 "for (key in required)" enumerates integer keys in ascending order.
        foreach (KeyValuePair<int, double> need in required)
        {
            (int Quantity, string? Name)? owned = held(need.Key);
            int have = owned?.Quantity ?? 0;
            if (have < need.Value)
            {
                string name = names.GetValueOrDefault(need.Key) ?? owned?.Name ?? "Item #" + need.Key.ToString(CultureInfo.InvariantCulture);
                missing.Add($"Missing {(need.Value - have).ToString(CultureInfo.InvariantCulture)} x {name} in inventory.");
            }
        }
        return missing;
    }

    /// <summary>
    /// World.maximumShopBuys: how many of a shop item can be bought (stack room, wallet, merge materials), at most
    /// 100 000 and the stack room.
    /// </summary>
    public static int MaximumShopBuys(JsonObject? item, JsonObject objData, Func<int, JsonObject?> owned)
    {
        if (item is null)
            return 0;
        if (JsonUtil.AsString(item["sES"]) == "ar")
            return 1;
        JsonObject? have = owned(JsonUtil.AsInt(item["ItemID"]));
        int stack = JsonUtil.AsInt(item["iStk"]);
        int room = have is not null ? stack - JsonUtil.AsInt(have["iQty"]) : stack;
        long n = room;
        if (n < 1)
            return 0;
        int cost = JsonUtil.AsInt(item["iCost"]);
        if (cost > 0)
        {
            long wallet = JsonUtil.AsBool(item["bCoins"]) ? JsonUtil.AsLong(objData["intCoins"]) : JsonUtil.AsLong(objData["intGold"]);
            n = Math.Min(wallet / cost, n);
        }
        if (item["turnin"] is JsonArray turnin)
        {
            foreach (JsonObject t in turnin.OfType<JsonObject>())
            {
                JsonObject? inv = owned(JsonUtil.AsInt(t["ItemID"]));
                if (inv is null)
                    return 0;
                int k = JsonUtil.AsInt(inv["iQty"]) / Math.Max(1, JsonUtil.AsInt(t["iQty"], 1));
                if (k == 0)
                    return 0;
                n = Math.Min(n, k);
            }
            n *= Math.Max(1, JsonUtil.AsInt(item["iQty"], 1));
        }
        return (int)Math.Min(Math.Min(n, 100000), room);
    }

    /// <summary>
    /// The checks World.sendBuyItemRequest makes before sending <c>buyItem</c>; returns why it would not send, or null.
    /// </summary>
    public static string? BuyRefusal(JsonObject item, JsonObject? shop, JsonObject objData, Func<int, long> reputation, Func<int, int> questValue)
    {
        if (shop is null)
            return "No shop is loaded.";
        if (JsonUtil.AsBool(item["bStaff"]) && JsonUtil.AsInt(objData["intAccessLevel"]) < 40)
            return "Staff only.";
        if (JsonUtil.AsString(shop["sField"]) is string field && field.Length > 0
            && QuestRules.GetAchievement(objData, field, JsonUtil.AsInt(shop["iIndex"])) != 1)
            return "Shop locked.";
        if (JsonUtil.AsBool(item["bUpg"]) && JsonUtil.AsInt(objData["iUpgDays"], -1) < 0)
            return "Member only.";
        int faction = JsonUtil.AsInt(item["FactionID"]);
        if (faction > 1 && reputation(faction) < JsonUtil.AsLong(item["iReqRep"]))
            return "Requires more reputation.";
        if (item["iQSindex"] is not null && JsonUtil.AsInt(item["iQSindex"], -1) >= 0 && questValue(JsonUtil.AsInt(item["iQSindex"])) < JsonUtil.AsInt(item["iQSvalue"]))
            return "Quest requirement not met.";
        long cost = JsonUtil.AsLong(item["iCost"]);
        if (!JsonUtil.AsBool(item["bCoins"]) && cost > JsonUtil.AsLong(objData["intGold"]))
            return "Not enough gold.";
        if (JsonUtil.AsBool(item["bCoins"]) && cost > JsonUtil.AsLong(objData["intCoins"]))
            return "Not enough AC.";
        return null;
    }
}
