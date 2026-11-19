using System;
using System.Collections.Generic;

namespace Skua.Plugins.Shared.DailyTracker;

/// <summary>
/// The quests the Daily &amp; Weekly Tracker follows: IDs, display names, the community script that does each one, and
/// how they are grouped. Extracted unchanged from the desktop plugin's window (Skua.Plugin.DailyTracker).
/// </summary>
internal static class DailyQuestCatalog
{
    /// <summary>Tracked quest IDs, in display order.</summary>
    public static readonly int[] TrackedQuests =
    [
        2091, 2098, 802, 803, 3075, 3076, 1239, 10047,
        3759, 3827, 3965, 3596,
        7156, 7157, 7158, 7159, 7160, 7161, 7162, 7163, 7164, 7165,
        8152, 8153, 8154, 8245, 8300, 8397, 8547, 8692, 8746, 9173, 10301
    ];

    /// <summary>Display names, same order as <see cref="TrackedQuests"/>.</summary>
    public static readonly string[] QuestNames =
    [
        "Mine Crafting", "Hard Core Metals", "Elders' Blood", "Sparrow's Blood",
        "Doom Member Free Spin", "Doom Free Weekly Spin", "Free Member Magic Keys", "A Grain of Dirt",
        "BeastMaster Challenge", "Shadow Shield (Daily)",
        "Glacera Ice Token (Cryomancer)", "Embrace Your Chaos",
        "Heart of Servitude", "Spirit of Justice", "Purification of Chaos", "Steadfast Will",
        "Strike of Order", "Harmony", "Ordinance", "Axiom", "Blessing of Order", "The Final Challenge",
        "Ultra Ezrajal", "Ultra Warden", "Ultra Engineer", "Ultra Tyndarius",
        "Champion Drakath", "Ultra Drago", "Ultra Dage", "Ultra Nulgath",
        "Ultra Darkon", "Ultra Speaker", "Ultra Gramiel"
    ];

    /// <summary>The script (relative to the Skua script folder, Windows separators) that does each quest.</summary>
    public static readonly IReadOnlyDictionary<int, string> QuestScriptNames = new Dictionary<int, string>
    {
        { 2091, "Dailies\\MineCrafting.cs" },
        { 2098, "Dailies\\HardCoreMetals[Mem].cs" },
        { 802,  "Dailies\\EldersBlood.cs" },
        { 803,  "Dailies\\SparrowsBlood.cs" },
        { 3075, "Dailies\\WheelofDoom.cs" },
        { 3076, "Dailies\\WheelofDoom.cs" },
        { 1239, "Dailies\\FreeDailyBoost[Mem].cs" },
        { 10047, "Dailies\\PearlOfNulgath.cs" },
        { 3759, "Dailies\\BeastMasterChallenge[Mem].cs" },
        { 3827, "Dailies\\ShadowShroud.cs" },
        { 3965, "Dailies\\Cryomancer.cs" },
        { 7156, "Dailies\\LordOfOrder.cs" },
        { 7157, "Dailies\\LordOfOrder.cs" },
        { 7158, "Dailies\\LordOfOrder.cs" },
        { 7159, "Dailies\\LordOfOrder.cs" },
        { 7160, "Dailies\\LordOfOrder.cs" },
        { 7161, "Dailies\\LordOfOrder.cs" },
        { 7162, "Dailies\\LordOfOrder.cs" },
        { 7163, "Dailies\\LordOfOrder.cs" },
        { 7164, "Dailies\\LordOfOrder.cs" },
        { 7165, "Dailies\\LordOfOrder.cs" },
        { 3596, "Dailies\\DagesScrollFragment.cs" },
        { 8152, "Ultras\\UltraEzrajal.cs" },
        { 8153, "Ultras\\UltraWarden.cs" },
        { 8154, "Ultras\\UltraEngineer.cs" },
        { 8245, "Ultras\\UltraAvatarTyndarius.cs" },
        { 8300, "Ultras\\ChampionDrakath.cs" },
        { 8397, "Ultras\\UltraDrago.cs" },
        { 8547, "Ultras\\UltraDage.cs" },
        { 8692, "Ultras\\UltraNulgath.cs" },
        { 8746, "Ultras\\UltraDarkon.cs" },
        { 9173, "Ultras\\UltraSpeaker.cs" },
        { 10301, "Ultras\\UltraGramiel.cs" }
    };

    /// <summary>The category that is started as a whole (one script does every step).</summary>
    public const string LordOfOrderCategory = "Lord of Order";

    /// <summary>The script started for the <see cref="LordOfOrderCategory"/> category.</summary>
    public const string LordOfOrderScript = "Dailies\\LordOfOrder.cs";

    /// <summary>First and last quest of the Lord of Order chain (7156 ... 7165).</summary>
    public const int LordOfOrderFirst = 7156;

    public const int LordOfOrderLast = 7165;

    /// <summary>The quest value slot that counts how far the Lord of Order chain got today.</summary>
    public const int LordOfOrderSlot = 327;

    /// <summary>The categories: name, index of the first quest in <see cref="TrackedQuests"/>, number of quests.</summary>
    public static readonly (string Name, int Start, int Count)[] Categories =
    [
        ("Resources & Miscellaneous", 0, 8),
        ("Classes & Factions", 8, 4),
        (LordOfOrderCategory, 12, 10),
        ("Ultra Bosses (Daily)", 22, 4),
        ("Ultra Bosses (Weekly)", 26, 7),
    ];

    /// <summary>A Lord of Order step (done when the chain's quest value reached it).</summary>
    public static bool IsLordOfOrder(int questId) => questId >= LordOfOrderFirst && questId <= LordOfOrderLast;

    /// <summary>The script that does <paramref name="questId"/>, or null.</summary>
    public static string? ScriptFor(int questId) =>
        QuestScriptNames.TryGetValue(questId, out string? script) ? script : null;

    /// <summary>
    /// Whether a quest row offers its own Start button: it has a script, is not done, and is not a Lord of Order step
    /// (that chain is started from its category).
    /// </summary>
    public static bool ShowsStart(int questId, bool isDone) =>
        ScriptFor(questId) is { Length: > 0 } && !isDone && !IsLordOfOrder(questId);

    /// <summary>Whether the category has a Start button of its own.</summary>
    public static bool HasCategoryScript(string categoryName) =>
        string.Equals(categoryName, LordOfOrderCategory, StringComparison.Ordinal);

    /// <summary>The script a category's Start button runs, or null.</summary>
    public static string? CategoryScript(string categoryName) =>
        HasCategoryScript(categoryName) ? LordOfOrderScript : null;
}
