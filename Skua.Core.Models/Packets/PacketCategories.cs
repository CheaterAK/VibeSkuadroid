namespace Skua.Core.Models.Packets;

/// <summary>
/// The packet categories of the desktop's Packet Interceptor filter list (shared with the Android app's interceptor
/// log): each category lists the commands that belong to it, both directions of a request. A packet that belongs to
/// none is "Other".
/// </summary>
public static class PacketCategories
{
    /// <summary>The name of the category for packets in no other category.</summary>
    public const string Other = "Other";

    /// <summary>The categories in the desktop's order, without "Other".</summary>
    public static IReadOnlyList<(string Name, string[] Commands)> All { get; } = new List<(string, string[])>
    {
        ("Combat", new[] { "ct", "gar", "sar", "sars", "anim", "mtls", "cb", "restRequest", "aggroMon",
            "resPlayerTimed", "resTimed", "respawnMon", "playerDeath" }),
        ("User Data", new[] { "retrieveUserData", "retrieveUserDatas", "initUserData", "initUserDatas" }),
        ("Join", new[] { "moveToArea", "tfer", "house", "joinOK", "firstJoin", "exitArea" }),
        ("Jump", new[] { "moveToCell" }),
        ("Movement", new[] { "mv", "mtcid", "mvna", "mvnb" }),
        ("Get Map", new[] { "getMapItem" }),
        ("Quest", new[] { "getQuest", "getQuests", "getQuests2", "acceptQuest", "tryQuestComplete", "updateQuest",
            "ccqr", "turnIn", "showQuestLink" }),
        ("Shop", new[] { "loadShop", "reloadShop", "loadHairShop", "loadEnhShop", "buyItem", "sellItem",
            "enhanceItem", "enhanceItemShop", "enhanceItemLocal" }),
        ("Equip", new[] { "equipItem", "forceEquipItem", "unequipItem", "wearItem", "unwearItem" }),
        ("Drop", new[] { "getDrop", "dropItem", "getMonsterDrops", "monsterDrops" }),
        ("Chat", new[] { "message", "cc", "chatm", "whisper", "em", "emotea" }),
        ("Auras", new[] { "aura+", "aura-", "aura++", "aura--", "aura*", "aura+p", "aura-p", "clearAuras", "auSnap",
            "showAuraResult" }),
        ("Skills", new[] { "sAct", "gar", "sar", "sars", "geia", "seia" }),
        ("Stats", new[] { "uotls", "stu" }),
        ("Inventory", new[] { "loadInventoryBig", "retrieveInventory", "initInventory", "addItems", "forceAddItem",
            "removeItem", "removeTempItem", "serverUseItem", "loadBank", "bankFromInv", "bankToInv", "bankSwapInv" }),
        ("Class", new[] { "updateClass", "changeClass" }),
        ("Misc", new[] { "crafting", "setHomeTown", "afk", "summonPet" }),
    };

    private static readonly (string Name, HashSet<string> Commands)[] s_sets =
        All.Select(c => (c.Name, new HashSet<string>(c.Commands, StringComparer.Ordinal))).ToArray();

    /// <summary>The categories a packet with <paramref name="commands"/> belongs to (a ct with auras is Combat and Auras); "Other" when none.</summary>
    public static IReadOnlyList<string> Of(IEnumerable<string> commands)
    {
        string[] list = commands as string[] ?? commands.ToArray();
        List<string> names = new();
        foreach ((string name, HashSet<string> set) in s_sets)
        {
            if (list.Any(set.Contains))
                names.Add(name);
        }
        if (names.Count == 0)
            names.Add(Other);
        return names;
    }
}
