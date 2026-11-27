using System.Text.Json.Nodes;

namespace Skua.Core.Headless.World;

// Immutable views handed out by WorldModel. Every snapshot carries the server's own object (deep copied, safe to
// keep and mutate) next to the typed fields, with the field names the real client exposes, so a later
// IFlashUtil adapter can answer arbitrary object-path reads from the same data.

/// <summary>The logged-in character: <c>world.myAvatar.objData</c> plus its live leaf (<c>world.myAvatar.dataLeaf</c>).</summary>
public sealed record PlayerInfo(
    int UserId,
    string Username,
    int Level,
    long Gold,
    long Coins,
    long Exp,
    long ExpToLevel,
    string ClassName,
    long ClassPoints,
    int Hp,
    int HpMax,
    int Mp,
    int MpMax,
    int State,
    bool Afk,
    string Cell,
    string Pad,
    int BagSlots,
    int BankSlots,
    int HouseSlots,
    int AccessLevel,
    int UpgradeDays,
    bool CharacterLoaded,
    bool InventoryLoaded,
    JsonObject ObjData,
    JsonObject DataLeaf)
{
    /// <summary>intState: 0 dead, 1 idle, 2 in combat.</summary>
    public bool IsAlive => State != 0 && !(Hp <= 0 && HpMax > 0);

    public bool InCombat => State == 2;
}

/// <summary>The current map/room.</summary>
public sealed record AreaInfo(
    int RoomId,
    string MapName,
    string AreaName,
    string MapFileName,
    string Cell,
    string Pad,
    int UserCount,
    int MonsterCount,
    JsonObject? Raw);

/// <summary>An item in the backpack, house inventory, temporary inventory or bank.</summary>
public sealed record ItemInfo(
    int ItemId,
    int CharItemId,
    string Name,
    int Quantity,
    int MaxStack,
    string Type,
    string EquipSlot,
    bool Equipped,
    bool Temp,
    bool Coins,
    bool Member,
    JsonObject Raw)
{
    internal static ItemInfo From(JsonObject o, bool temp, bool raw = true)
    {
        return new ItemInfo(
            JsonUtil.AsInt(o["ItemID"]),
            JsonUtil.AsInt(o["CharItemID"]),
            JsonUtil.AsString(o["sName"]) ?? string.Empty,
            JsonUtil.AsInt(o["iQty"]),
            JsonUtil.AsInt(o["iStk"]),
            JsonUtil.AsString(o["sType"]) ?? string.Empty,
            JsonUtil.AsString(o["sES"]) ?? string.Empty,
            JsonUtil.AsBool(o["bEquip"]),
            temp || JsonUtil.AsBool(o["bTemp"]),
            JsonUtil.AsBool(o["bCoins"]),
            JsonUtil.AsBool(o["bUpg"]),
            raw ? (JsonObject)o.DeepClone() : new JsonObject());
    }
}

/// <summary>An entry of <c>world.questTree</c> and/or a quest the server has accepted.</summary>
/// <param name="Status">The client's quest status field: "p" in progress, "c" completable, null otherwise.</param>
/// <param name="Accepted">The server confirmed an acceptQuest for it and no successful turn-in since.</param>
public sealed record QuestInfo(
    int QuestId,
    string Name,
    bool Loaded,
    string? Status,
    bool Accepted,
    int Slot,
    int Value,
    bool Once,
    bool Member,
    int Level,
    JsonObject? Raw);

/// <summary>A monster slot of the current map: definition (<c>mondef</c>), placement (<c>monmap</c>) and live leaf (<c>monTree</c>).</summary>
public sealed record MonsterInfo(
    int MonMapId,
    int MonId,
    string Name,
    int Level,
    string Race,
    string Cell,
    int Hp,
    int HpMax,
    int State,
    JsonObject Definition,
    JsonObject Placement,
    JsonObject? Live)
{
    public bool IsAlive => State != 0 && Hp > 0;
}

/// <summary>A player in the current room (<c>world.uoTree</c> leaf, plus <c>initUserDatas</c> objData when loaded).</summary>
public sealed record AreaUserInfo(
    string Name,
    int? UserId,
    string Cell,
    string Pad,
    int Hp,
    int HpMax,
    int State,
    int Level,
    bool IsMe,
    JsonObject Leaf,
    JsonObject? ObjData);

/// <summary>An entry of <c>world.actions.active</c> / <c>passive</c> as sent by <c>sAct</c>.</summary>
public sealed record SkillInfo(
    int Index,
    string Ref,
    string Name,
    string Type,
    string Target,
    int CooldownMs,
    int ManaCost,
    bool Auto,
    bool Passive,
    DateTimeOffset? LastSent,
    JsonObject Raw);

/// <summary>An aura on a player (<c>p:&lt;uid&gt;</c>) or monster (<c>m:&lt;MonMapID&gt;</c>).</summary>
public sealed record AuraInfo(
    string Subject,
    string Name,
    double? Value,
    double? DurationSeconds,
    int Stacks,
    bool Passive,
    bool Persistent,
    string? Caster,
    DateTimeOffset AppliedAt,
    JsonObject Raw);

/// <summary>An item offered by <c>dropItem</c> and not yet picked up.</summary>
public sealed record DropInfo(
    int ItemId,
    string Name,
    int Quantity,
    int TimesOffered,
    DateTimeOffset LastOffered,
    JsonObject Raw);

/// <summary>The last shop loaded (<c>world.shopinfo</c>).</summary>
public sealed record ShopInfo(int ShopId, string Name, IReadOnlyList<ShopItemInfo> Items, JsonObject Raw);

public sealed record ShopItemInfo(int ItemId, int ShopItemId, string Name, int Cost, bool Coins, bool Member, int Quantity, JsonObject Raw);

/// <summary>A faction of <c>world.myAvatar.factions</c>. Rank math is left to the next layer.</summary>
public sealed record FactionInfo(int FactionId, string Name, long Rep, JsonObject Raw);

/// <summary>A chat or server line (server, warning, notify, chatm, whisper ...).</summary>
public sealed record ServerMessageInfo(string Command, string Text, string? Sender, DateTimeOffset At);

/// <summary>A position change the server asked the client to make (gtc, resTimed, dungeonMTC). Not acted on yet.</summary>
public sealed record ServerMoveRequest(string Command, string Cell, string Pad, DateTimeOffset At);

/// <summary>Per-command counts of everything the model saw, so protocol gaps can be measured.</summary>
public sealed record PacketCounters(
    IReadOnlyDictionary<string, long> Handled,
    IReadOnlyDictionary<string, long> Unhandled,
    IReadOnlyDictionary<string, string> UnhandledSamples,
    long Total);

/// <summary>Raised after the model applied a packet.</summary>
public sealed class WorldChangedEventArgs : EventArgs
{
    public WorldChangedEventArgs(string key, bool handled, long version)
    {
        Key = key;
        Handled = handled;
        Version = version;
    }

    /// <summary><c>&lt;kind&gt;:&lt;command&gt;</c>, e.g. <c>json:moveToArea</c> or <c>xt:uotls</c>.</summary>
    public string Key { get; }

    public bool Handled { get; }

    public long Version { get; }
}

/// <summary>
/// <see cref="WorldModel.ReadBankItems"/>: the bank's items in order, an opaque key per item (equal keys within one
/// <paramref name="Generation"/> mean equal content) and the JSON text of the items the reader did not have (null
/// where it has them).
/// </summary>
public sealed record BankItemsRead(long Version, long Generation, IReadOnlyList<object> Keys, IReadOnlyList<string?> Json);

/// <summary><see cref="WorldModel.ItemListVersions"/>: equal values mean unchanged backpack, temporary and house lists.</summary>
public readonly record struct ItemListVersions(long Inventory, long Temp, long House);
