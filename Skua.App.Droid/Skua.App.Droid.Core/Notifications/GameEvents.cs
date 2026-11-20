namespace Skua.App.Droid.Core.Notifications;

/// <summary>What happened in the game or to a script (the desktop Notification Center's event types).</summary>
public enum GameEventKind
{
    /// <summary>A drop was offered (Skua's ItemDroppedMessage). <see cref="GameEvent.Name"/>, <see cref="GameEvent.Id"/>, <see cref="GameEvent.Quantity"/>.</summary>
    ItemDropped,

    /// <summary>An item was added to the inventory (addItems, or a drop picked up).</summary>
    ItemObtained,

    /// <summary>A junk drop was rejected automatically.</summary>
    DropRejected,

    /// <summary>Items were sold. <see cref="GameEvent.Quantity"/> sold for <see cref="GameEvent.Amount"/> (AC when <see cref="GameEvent.Flag"/>).</summary>
    ItemSold,

    PlayerDied,

    /// <summary>Skua's relogin started; <see cref="GameEvent.Flag"/>: the server kicked the character.</summary>
    Relogin,

    /// <summary>Joined a map (<see cref="GameEvent.Name"/>).</summary>
    MapChanged,

    /// <summary>Quest <see cref="GameEvent.Id"/> accepted.</summary>
    QuestAccepted,

    /// <summary>Quest <see cref="GameEvent.Id"/> turned in.</summary>
    QuestCompleted,

    /// <summary>A script started (<see cref="GameEvent.Name"/>: its file name without extension).</summary>
    ScriptStarted,

    ScriptStopped,

    /// <summary>A script failed; <see cref="GameEvent.Detail"/> is the exception message (kept on the device only).</summary>
    ScriptError,
}

/// <summary>One game or script event, as the Skua host reports it (background thread).</summary>
public sealed record GameEvent(GameEventKind Kind, DateTimeOffset At)
{
    /// <summary>Item, map or script name.</summary>
    public string? Name { get; init; }

    /// <summary>Item id or quest id.</summary>
    public int Id { get; init; }

    public int Quantity { get; init; }

    /// <summary>Gold or AC of a sale.</summary>
    public long Amount { get; init; }

    /// <summary>Kind specific: kicked (relogin), paid in AC (sale).</summary>
    public bool Flag { get; init; }

    /// <summary>Free text (a script error's message). Never sent off the device.</summary>
    public string? Detail { get; init; }
}

/// <summary>An entry of the junk list (Skua's JunkItemConfig, stored in the same JunkItems.json as the desktop).</summary>
public sealed record JunkItem(int Id, string Name, string Category);

/// <summary>
/// Skua's junk list (IJunkService: the desktop's Junk Items window) on the engine's session, plus the Android
/// app's automatic rejection of junk drops.
/// </summary>
public interface IJunkList
{
    /// <summary>The junk items (read from JunkItems.json on first use).</summary>
    IReadOnlyList<JunkItem> Items { get; }

    bool IsJunk(int itemId);

    /// <summary>Replaces the list and saves it (JunkItems.json, the desktop's format).</summary>
    void Set(IEnumerable<JunkItem> items);

    /// <summary>Reads the file again (a community script sync may have replaced it).</summary>
    void Reload();

    /// <summary>
    /// Sells every junk item in the inventory that is not equipped (the desktop's "Sell all junk"). Blocking: call it
    /// off the UI thread. Returns how many inventory stacks were offered for sale.
    /// </summary>
    int SellAll();

    /// <summary>Reject a junk drop as soon as it is offered (unless the running script asked for it).</summary>
    bool AutoReject { get; set; }

    /// <summary>Items this returns true for are never rejected automatically (the drop watch list).</summary>
    Func<string, bool>? Keep { get; set; }
}

/// <summary>
/// Game and script events from the Skua host (Skua.Core's messages), and the drop actions of the desktop's Current
/// Drops window. Implemented in Skua.App.Droid.Scripting on the engine's one Skua session.
/// </summary>
public interface IGameEvents
{
    /// <summary>Raised on the thread that received the packet (the Flash bridge or the script thread): handlers must return at once.</summary>
    event EventHandler<GameEvent>? Raised;

    /// <summary>Picks the drops up (Skua's IScriptDrop.Pickup); returns at once, the server's replies arrive later.</summary>
    void PickupDrops(IReadOnlyCollection<int> itemIds);

    /// <summary>Picks every offered drop up; returns at once.</summary>
    void PickupAllDrops();

    /// <summary>Rejects the drops (Skua's IScriptDrop.RejectExcept with every other drop kept); returns at once.</summary>
    void RejectDrops(IReadOnlyCollection<int> itemIds);

    /// <summary>The junk list.</summary>
    IJunkList Junk { get; }
}

/// <summary>Implemented by a script host that provides <see cref="IGameEvents"/> (the Skua.Core host adapter).</summary>
public interface IGameEventSource
{
    /// <summary>Null until the host's session exists, or when it could not start.</summary>
    IGameEvents? GameEvents { get; }
}
