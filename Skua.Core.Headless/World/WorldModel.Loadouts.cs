using System.Globalization;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.World;

// Outfits ("loadouts") and cosmetics, as the game keeps them.
//
// Game.as: loadPrefs (success) sets world.objInfo["customs"] = resObj.result, which the live server sends at login as
// {"costumes":{},"loadouts":{},"prefs":{}}; customs.loadouts maps a set name to {he, ba, ar, co, Weapon, pe, am, mi:
// ItemID, colors: {hair, skin, eye, base, trim, accessory}} (interface OutfitSets/outfitsetsr2.swf, outfitedit.as).
// The outfit interface sends addLoadout [name, JSON body, original name] and removeLoadout [name]; the replies only say
// success (removeLoadout also names setName), and the interface then changes its copy of the sets and writes them back
// to customs.loadouts when it closes (main.as organizeSets). The model applies the change when the success arrives.
// equipLoadout / wearLoadout ["cmd", name, keepColors 1|0]: equipLoadout has no reply handler (the server's equipItem,
// updateClass and colour packets do the work); wearLoadout's reply shows the costume (Weapon, he, ba, co).
public sealed partial class WorldModel
{
    private static readonly string[] s_costumeSlots = { "Weapon", "he", "ba", "co" };

    /// <summary>addLoadout requests sent and not answered yet: (name, body, original name), oldest first.</summary>
    private readonly Queue<(string Name, JsonObject Body, string Original)> _pendingLoadoutSaves = new();

    /// <summary>
    /// <c>world.objInfo.customs.loadouts</c>: the character's saved outfits by name (a copy), or null before the server
    /// sent loadPrefs on this connection.
    /// </summary>
    public JsonObject? GetLoadouts()
    {
        lock (_gate)
            return (ObjInfo["customs"] as JsonObject)?["loadouts"]?.DeepClone() as JsonObject;
    }

    /// <summary>True once loadPrefs arrived on this connection (<c>world.objInfo.customs</c> exists).</summary>
    public bool HasLoadPrefs
    {
        get { lock (_gate) return ObjInfo["customs"] is JsonObject; }
    }

    private JsonObject ObjInfo => (JsonObject)_world["objInfo"]!;

    private JsonObject? LoadoutsNode()
    {
        if (ObjInfo["customs"] is not JsonObject customs)
            return null;
        if (customs["loadouts"] is not JsonObject loadouts)
            customs["loadouts"] = loadouts = new JsonObject();
        return loadouts;
    }

    // ---------------------------------------------------------------- inbound
    private bool ApplyLoadoutJson(string cmd, JsonObject d)
    {
        switch (cmd)
        {
            case "loadPrefs":
                if (JsonUtil.AsBool(d["success"]) && d["result"] is JsonNode result)
                    ObjInfo["customs"] = result.DeepClone();
                return true;

            case "addLoadout":
                (string Name, JsonObject Body, string Original)? save = _pendingLoadoutSaves.Count > 0 ? _pendingLoadoutSaves.Dequeue() : null;
                if (!JsonUtil.AsBool(d["success"]))
                {
                    RecordMessage(cmd, JsonUtil.AsString(d["msg"]) ?? cmd, null);
                    return true;
                }
                if (save is { } s && LoadoutsNode() is JsonObject saved)
                {
                    // outfitedit.onServerResponseUpdate replaces the edited set (renamed: the old name goes) or adds the new one.
                    if (s.Original.Length > 0 && s.Original != s.Name)
                        saved.Remove(s.Original);
                    saved[s.Name] = s.Body.DeepClone();
                }
                return true;

            case "removeLoadout":
                if (!JsonUtil.AsBool(d["success"]))
                {
                    RecordMessage(cmd, JsonUtil.AsString(d["msg"]) ?? cmd, null);
                    return true;
                }
                if (JsonUtil.AsString(d["setName"]) is string gone && LoadoutsNode() is JsonObject sets)
                    sets.Remove(gone);
                return true;

            case "buyLoadoutSlots":
                if (JsonUtil.AsInt(d["bitSuccess"]) == 1)
                {
                    int slots = JsonUtil.AsInt(d["iSlots"]);
                    ObjData["iLoadoutSlots"] = JsonUtil.AsInt(ObjData["iLoadoutSlots"]) + slots;
                    ObjData["intCoins"] = JsonUtil.AsInt(ObjData["intCoins"]) - slots * 200;
                }
                return true;

            case "wearItem":
                OnWearItem(d);
                return true;

            case "unwearItem":
                OnUnwearItem(d);
                return true;

            case "wearLoadout":
                OnWearLoadout(d);
                return true;

            default:
                return false;
        }
    }

    // Game.as wearItem: the shown look of a slot (eqp[sES]) and, for this character, Avatar.wearItem (bWear).
    private void OnWearItem(JsonObject d)
    {
        int uid = JsonUtil.AsInt(d["uid"], _myUid);
        string? slot = JsonUtil.AsString(d["sES"]);
        if (EqpOf(uid) is JsonObject eqp && slot is not null)
        {
            JsonObject shown = eqp[slot] as JsonObject ?? new JsonObject();
            shown["sFile"] = JsonUtil.AsString(d["sFile"]) is "undefined" or null ? string.Empty : JsonUtil.AsString(d["sFile"]);
            shown["sLink"] = d["sLink"]?.DeepClone();
            int itemId = JsonUtil.AsInt(d["ItemID"]);
            shown["sType"] = itemId is 156 or 12583 ? "Unarmed" : d["sType"]?.DeepClone();
            eqp[slot] = shown;
        }
        if (uid != _myUid)
            return;
        if (!JsonUtil.AsBool(d["success"], true))
            RecordMessage("wearItem", JsonUtil.AsString(d["msg"]) ?? "wearItem", null);
        WearItem(JsonUtil.AsInt(d["ItemID"]));
    }

    // Game.as unwearItem: this character's item stops being shown; bRemove drops the slot's look, else the reply's.
    private void OnUnwearItem(JsonObject d)
    {
        int uid = JsonUtil.AsInt(d["uid"], _myUid);
        string? slot = JsonUtil.AsString(d["sES"]);
        if (uid == _myUid && FindById(Items, "ItemID", JsonUtil.AsInt(d["ItemID"])) is JsonObject item)
            item["bWear"] = 0;
        if (EqpOf(uid) is JsonObject eqp && slot is not null)
        {
            if (d.ContainsKey("bRemove"))
            {
                eqp.Remove(slot);
            }
            else if (eqp[slot] is JsonObject shown)
            {
                shown["sFile"] = JsonUtil.AsString(d["sFile"]) is "undefined" or null ? string.Empty : JsonUtil.AsString(d["sFile"]);
                shown["sLink"] = d["sLink"]?.DeepClone();
                shown["sType"] = d["sType"]?.DeepClone();
            }
        }
        if (uid == _myUid && !JsonUtil.AsBool(d["success"], true))
            RecordMessage("unwearItem", JsonUtil.AsString(d["msg"]) ?? "unwearItem", null);
    }

    // Game.as wearLoadout: for Weapon, he, ba, co the slot's shown item is cleared (this character), then the costume's
    // piece, when there is one, is shown.
    private void OnWearLoadout(JsonObject d)
    {
        int uid = JsonUtil.AsInt(d["uid"], _myUid);
        bool mine = uid == _myUid;
        JsonObject? eqp = EqpOf(uid);
        JsonObject? costume = d["costume"] as JsonObject;
        foreach (string spot in s_costumeSlots)
        {
            if (mine)
                UnwearAt(spot);
            if (costume?[spot] is not JsonObject piece)
                continue;
            if (eqp is not null)
            {
                JsonObject shown = eqp[spot] as JsonObject ?? new JsonObject();
                shown["sFile"] = JsonUtil.AsString(piece["sFile"]) is "undefined" or null ? string.Empty : JsonUtil.AsString(piece["sFile"]);
                shown["sLink"] = piece["sLink"]?.DeepClone();
                shown["sType"] = piece["sType"]?.DeepClone();
                eqp[spot] = shown;
            }
            if (mine)
                WearItem(JsonUtil.AsInt(piece["ItemID"]));
        }
        if (mine && !JsonUtil.AsBool(d["success"], true))
            RecordMessage("wearLoadout", JsonUtil.AsString(d["msg"]) ?? "wearLoadout", null);
    }

    // Avatar.wearItem: the item's slot loses its shown item, then this one is shown.
    private void WearItem(int itemId)
    {
        if (FindById(Items, "ItemID", itemId) is not JsonObject item)
            return;
        if (JsonUtil.AsString(item["sES"]) is string es)
            UnwearAt(es);
        item["bWear"] = 1;
    }

    // Avatar.unwearItemAtES.
    private void UnwearAt(string slot)
    {
        foreach (JsonNode? node in Items)
        {
            if (node is JsonObject o && JsonUtil.AsString(o["sES"]) == slot)
                o["bWear"] = 0;
        }
    }

    private JsonObject? EqpOf(int uid)
    {
        JsonObject? objData;
        if (uid == _myUid)
        {
            objData = ObjData;
        }
        else
        {
            if (Avatars[uid.ToString(CultureInfo.InvariantCulture)] is not JsonObject avatar)
                return null;
            objData = avatar["objData"] as JsonObject;
        }
        if (objData is null)
            return null;
        if (objData["eqp"] is not JsonObject eqp)
            objData["eqp"] = eqp = new JsonObject();
        return eqp;
    }

    // ---------------------------------------------------------------- outbound
    private void ApplyLoadoutOutbound(string cmd, string[] args)
    {
        if (cmd != "addLoadout" || args.Length < 2)
            return;
        JsonObject body;
        try
        {
            body = JsonNode.Parse(args[1]) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            body = new JsonObject();
        }
        _pendingLoadoutSaves.Enqueue((args[0], body, args.Length > 2 ? args[2] : string.Empty));
        while (_pendingLoadoutSaves.Count > 8)
            _pendingLoadoutSaves.Dequeue();
    }
}
