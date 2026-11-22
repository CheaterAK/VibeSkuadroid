using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Client;

// The game's outfits ("loadouts"), as the OutfitSets interface (interface/OutfitSets/outfitsetsr2.swf) sends them:
//   equipLoadout / wearLoadout: sendXtMessage("zm", cmd, ["cmd", setName, keepColors ? 1 : 0], "str", curRoom) after
//     world.coolDown(cmd) (outfitsets.as onEquipSet / onWearSet);
//   addLoadout: [setName, JSON.stringify(set without its name), originalName] after coolDown("addLoadout"), refused
//     locally when a new set would pass the character's iLoadoutSlots ("You have no more free outfit slots!");
//   removeLoadout: [setName] after coolDown("removeLoadout").
// skua.swf's Player.equipLoadout (not exported to Skua) sends !changeColors as a Boolean in the same field; the game's
// own 1/0 is used here.
public sealed partial class GameClient
{
    /// <summary>The saved outfits by name (<c>world.objInfo.customs.loadouts</c>), or null before loadPrefs arrived.</summary>
    public JsonObject? Loadouts => World.GetLoadouts();

    /// <summary>
    /// The outfit window's "Equip": the server equips the set's items (class included). False when the name is empty
    /// or equipLoadout is cooling down (the game says "Slow down!").
    /// </summary>
    /// <param name="setName">The outfit's name as saved.</param>
    /// <param name="keepColors">Keep the character's current colours instead of the outfit's.</param>
    public bool EquipLoadout(string setName, bool keepColors = true)
    {
        return SendLoadoutCommand("equipLoadout", setName, keepColors);
    }

    /// <summary>The outfit window's "Wear": the set's weapon, helm, cape and armor are shown over what is equipped.</summary>
    public bool WearLoadout(string setName, bool keepColors = true)
    {
        return SendLoadoutCommand("wearLoadout", setName, keepColors);
    }

    /// <summary>
    /// outfitedit.onSetUpdate: saves <paramref name="set"/> (slot to ItemID, plus <c>colors</c>) as
    /// <paramref name="setName"/>, replacing <paramref name="originalName"/> when renaming an existing one. False when
    /// refused locally (no free outfit slot for a new set, cooling down, empty name).
    /// </summary>
    public bool AddLoadout(string setName, JsonObject set, string originalName = "")
    {
        if (string.IsNullOrEmpty(setName))
            return false;
        bool isNew = originalName.Length == 0 || World.GetLoadouts() is not JsonObject known || !known.ContainsKey(originalName);
        if (isNew && World.GetLoadouts() is JsonObject sets && sets.Count >= LoadoutSlots())
        {
            RaiseLog("You have no more free outfit slots!");
            return false;
        }
        if (!Locks.TryAcquire("addLoadout"))
            return false;
        JsonObject body = (JsonObject)set.DeepClone();
        body.Remove("name");
        return SendXt("addLoadout", null, setName, body.ToJsonString(), originalName);
    }

    /// <summary>outfitsets.onDeleteSet: removes a saved outfit.</summary>
    public bool RemoveLoadout(string setName)
    {
        if (string.IsNullOrEmpty(setName) || !Locks.TryAcquire("removeLoadout"))
            return false;
        return SendXt("removeLoadout", null, setName);
    }

    /// <summary><c>world.myAvatar.objData.iLoadoutSlots</c> (the game's outfit window uses it as the set limit).</summary>
    public int LoadoutSlots()
    {
        return JsonUtil.AsInt(World.Read((w, _) => (w["myAvatar"]?["objData"] as JsonObject)?["iLoadoutSlots"]?.DeepClone()));
    }

    private bool SendLoadoutCommand(string cmd, string setName, bool keepColors)
    {
        if (string.IsNullOrEmpty(setName))
            return false;
        if (!Locks.TryAcquire(cmd))
        {
            RaiseLog("Slow down! Last action was too fast.");
            return false;
        }
        return SendXt(cmd, null, "cmd", setName, keepColors ? 1 : 0);
    }
}
