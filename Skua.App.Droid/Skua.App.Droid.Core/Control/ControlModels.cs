// Records document only the parameters whose meaning is not obvious from the name.
#pragma warning disable CS1573

using Skua.App.Droid.Core.Engine;

namespace Skua.App.Droid.Core.Control;

/// <summary>Where the cell names of the map come from.</summary>
public enum CellSource
{
    /// <summary>Nothing known (offline or no map).</summary>
    None,

    /// <summary>The map file's frame labels (downloaded SWF or its disk cache): every cell of the map.</summary>
    MapFile,

    /// <summary>The map file could not be read (for example LZMA): only the cells seen in the room data (monsters, players).</summary>
    RoomData,
}

/// <summary>One tile of the schematic map.</summary>
/// <param name="Name">Cell name (frame label).</param>
/// <param name="MonstersAlive">Monsters placed in the cell that are alive.</param>
/// <param name="MonstersTotal">Monsters placed in the cell.</param>
/// <param name="Players">Other players in the cell (this character not counted).</param>
/// <param name="IsCurrent">This character is in the cell.</param>
public sealed record ControlCell(string Name, int MonstersAlive, int MonstersTotal, int Players, bool IsCurrent);

/// <summary>A monster of the current cell.</summary>
/// <param name="State">intState: 0 dead, 1 idle, 2 in combat.</param>
/// <param name="Targeted">This character has it targeted.</param>
public sealed record ControlMonster(int MonMapId, string Name, int Level, int Hp, int HpMax, int State, bool Targeted)
{
    public bool Alive => State != 0 && Hp > 0;

    public string StateText => !Alive ? "dead" : State == 2 ? "in combat" : "idle";
}

/// <summary>Another player in the current room.</summary>
/// <param name="State">intState: 0 dead, 1 idle, 2 in combat.</param>
public sealed record ControlPlayer(string Name, int Level, string Cell, string Pad, int Hp, int HpMax, int State, bool Afk)
{
    public string StateText => (State == 0 ? "dead" : State == 2 ? "in combat" : "idle") + (Afk ? " · afk" : string.Empty);
}

/// <summary>A drop waiting to be picked up (Skua's drop list: rejected drops leave it).</summary>
public sealed record ControlDrop(int ItemId, string Name, int Quantity);

/// <summary>A skill of the skill bar (index 1-4 class skills, 5 the potion slot).</summary>
public sealed record ControlSkill(int Index, string Name);

/// <summary>What the Control page shows about the game right now. Immutable.</summary>
/// <param name="Online">Logged in with the character and map loaded.</param>
/// <param name="Map">Map name (as joined, without the room number).</param>
/// <param name="Area">Room as the server names it (map-number).</param>
/// <param name="Cell">This character's cell.</param>
/// <param name="Pad">This character's pad.</param>
/// <param name="Cells">The map's cells (tiles), in map order.</param>
/// <param name="Source">Where <paramref name="Cells"/> came from.</param>
/// <param name="Pads">The pads of the current cell (from the map file; common pads when unknown).</param>
/// <param name="Monsters">Monsters of the current cell.</param>
/// <param name="Players">Other players of the current cell.</param>
/// <param name="RoomPlayers">Other players anywhere in the room.</param>
/// <param name="Drops">Drops waiting.</param>
/// <param name="Skills">Skill bar slots 1-5.</param>
/// <param name="Target">What is targeted, for display, or null.</param>
/// <param name="Alive">This character is alive.</param>
/// <param name="InCombat">This character is in combat.</param>
/// <param name="Hp">Health.</param>
/// <param name="HpMax">Maximum health.</param>
/// <param name="ShopLoaded">A shop is loaded (selling is possible).</param>
/// <param name="BankLoaded">The bank was loaded this session.</param>
/// <param name="Version">World model version this was read at.</param>
public sealed record ControlState(
    bool Online,
    string Map,
    string Area,
    string Cell,
    string Pad,
    IReadOnlyList<ControlCell> Cells,
    CellSource Source,
    IReadOnlyList<string> Pads,
    IReadOnlyList<ControlMonster> Monsters,
    IReadOnlyList<ControlPlayer> Players,
    IReadOnlyList<ControlPlayer> RoomPlayers,
    IReadOnlyList<ControlDrop> Drops,
    IReadOnlyList<ControlSkill> Skills,
    string? Target,
    bool Alive,
    bool InCombat,
    int Hp,
    int HpMax,
    bool ShopLoaded,
    bool BankLoaded,
    long Version)
{
    public static ControlState Offline { get; } = new(false, string.Empty, string.Empty, string.Empty, string.Empty, [], CellSource.None, [], [], [], [], [], [], null, false, false, 0, 0, false, false, -1);
}

/// <summary>An item of the loaded shop, with the client's purchase checks.</summary>
/// <param name="Cost">Price of one bundle.</param>
/// <param name="Coins">Priced in AC (otherwise gold).</param>
/// <param name="Member">Member only.</param>
/// <param name="Bundle">Items per purchase (shop item iQty).</param>
/// <param name="MaxStack">Stack size (iStk).</param>
/// <param name="MaxBuys">The game's maximumShopBuys: how many can be bought now (wallet, stack room, materials).</param>
/// <param name="Unmet">What is missing for one bundle (materials, quest), from skua.swf's getUnmetPurchaseRequirements.</param>
/// <param name="Refusal">Why the game would not send the purchase (gold, membership, reputation, level), or null.</param>
public sealed record ControlShopItem(int ItemId, int ShopItemId, string Name, int Cost, bool Coins, bool Member, int Bundle, int MaxStack, int MaxBuys, IReadOnlyList<string> Unmet, string? Refusal)
{
    public bool CanBuy => Refusal is null && Unmet.Count == 0 && MaxBuys > 0;

    public string Currency => Coins ? "AC" : "gold";

    public string PriceText => Cost <= 0 ? "free" : $"{Cost:N0} {Currency}{(Bundle > 1 ? $" / {Bundle}" : string.Empty)}";

    public string RequirementText => Refusal ?? (Unmet.Count > 0 ? string.Join(" ", Unmet) : MaxBuys <= 0 ? "No room or funds for more." : string.Empty);

    public bool Equals(ControlShopItem? other) =>
        other is not null && ItemId == other.ItemId && ShopItemId == other.ShopItemId && Name == other.Name && Cost == other.Cost
        && Coins == other.Coins && Member == other.Member && Bundle == other.Bundle && MaxStack == other.MaxStack
        && MaxBuys == other.MaxBuys && Refusal == other.Refusal && Unmet.SequenceEqual(other.Unmet);

    public override int GetHashCode() => HashCode.Combine(ItemId, ShopItemId, MaxBuys, Refusal, Unmet.Count);

    /// <summary>The price of <paramref name="quantity"/> items (whole bundles).</summary>
    public long PriceOf(int quantity) => (long)Cost * Math.Max(1, quantity / Math.Max(1, Bundle));
}

/// <summary>The shop last loaded.</summary>
public sealed record ControlShop(int ShopId, string Name, IReadOnlyList<ControlShopItem> Items);

/// <summary>A turn-in item of a quest and how many are held.</summary>
public sealed record ControlQuestItem(int ItemId, string Name, int Have, int Need)
{
    public bool Done => Have >= Need;

    public string Text => $"{Name}: {Math.Min(Have, Need)}/{Need}";
}

/// <summary>A loaded quest with the client's checks.</summary>
/// <param name="Requirements">Requirements to accept, as text (level, membership, reputation, class, items).</param>
/// <param name="AcceptRefusal">The first unmet accept requirement, or null.</param>
/// <param name="Progress">Turn-in items with what is held.</param>
/// <param name="Rewards">Gold, experience and reward items, as text.</param>
/// <param name="TurnIns">How many times it can be turned in now (the game's maximumQuestTurnIns; -1 without turn-in items).</param>
public sealed record ControlQuest(
    int QuestId,
    string Name,
    string Description,
    bool Accepted,
    bool Completable,
    bool Once,
    IReadOnlyList<string> Requirements,
    string? AcceptRefusal,
    IReadOnlyList<ControlQuestItem> Progress,
    IReadOnlyList<string> Rewards,
    int TurnIns)
{
    public string StatusText => Completable ? "ready to turn in" : Accepted ? "in progress" : "not accepted";

    public bool Equals(ControlQuest? other) =>
        other is not null && QuestId == other.QuestId && Name == other.Name && Description == other.Description
        && Accepted == other.Accepted && Completable == other.Completable && Once == other.Once
        && AcceptRefusal == other.AcceptRefusal && TurnIns == other.TurnIns && Requirements.SequenceEqual(other.Requirements)
        && Progress.SequenceEqual(other.Progress) && Rewards.SequenceEqual(other.Rewards);

    public override int GetHashCode() => HashCode.Combine(QuestId, Accepted, Completable, TurnIns);
}

/// <summary>Bank and backpack for moving items.</summary>
public sealed record ControlBank(bool Loaded, IReadOnlyList<InventoryRow> Bank, IReadOnlyList<InventoryRow> Inventory, int BankSlots, int BagSlots);
