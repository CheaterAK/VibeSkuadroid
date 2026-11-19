namespace Skua.App.Droid.Core.Control;

/// <summary>
/// The game side of the Control page: reads of the live world (map cells, the current cell, drops, shop, quest,
/// bank) and manual actions, on the engine's one Skua session. Implemented in Skua.App.Droid.Plugins on Skua's
/// <c>IScriptInterface</c> and the headless client rules (<c>GameClient</c>: targeting, skills, auto attack, action
/// throttles, shop and quest checks); the ViewModel only sees this interface and the records in
/// <c>ControlModels.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reads are cheap and never throw (offline: <see cref="ControlState.Offline"/>). Actions block (they wait like
/// Skua's own API does, for a throttle, a reply or a map load; seconds at most): call them off the UI thread. They
/// throw <see cref="Engine.EngineException"/> with a message for the user when the game refuses or nothing happened.
/// </para>
/// <para>
/// No action sends a hand-written packet: each goes through Skua's API or a client rule, which waits for the
/// game's own throttle (<c>world.lock</c>) where the game has one. Moves within the map, the goto and skills, which
/// the game does not throttle, are spaced out here as well.
/// </para>
/// </remarks>
public interface IControlBackend
{
    /// <summary>The map, the current cell and the rest of the page's live data.</summary>
    ControlState Read();

    /// <summary>The shop last loaded, with the purchase checks, or null.</summary>
    ControlShop? ReadShop();

    /// <summary>A quest the client has loaded, with progress and rewards, or null when not loaded.</summary>
    ControlQuest? ReadQuest(int questId);

    /// <summary>Bank and backpack.</summary>
    ControlBank ReadBank();

    /// <summary>Joins a map (map or map-room). Returns a line for the user.</summary>
    string JoinMap(string map);

    /// <summary>Moves to a cell of the current map; a null pad picks the cell's default pad.</summary>
    string Jump(string cell, string? pad);

    /// <summary>The game's /goto to a player.</summary>
    string GotoPlayer(string name);

    /// <summary>Targets the monster and starts the auto attack (the client's attackMonsterID).</summary>
    string Attack(int monMapId);

    /// <summary>Uses skill bar slot <paramref name="index"/> (1-5) on the target, when it is ready.</summary>
    string UseSkill(int index);

    /// <summary>Stops the auto attack and drops the target.</summary>
    string CancelTarget();

    string Rest();

    string Resurrect();

    string PickupDrop(int itemId);

    string RejectDrop(int itemId);

    string LoadShop(int shopId);

    /// <summary>Buys <paramref name="quantity"/> of a shop item (-1: one bundle, the game's own buy).</summary>
    string Buy(int itemId, int shopItemId, int quantity);

    /// <summary>Sells <paramref name="quantity"/> of a backpack item (-1: the whole stack).</summary>
    string Sell(int itemId, int quantity);

    string LoadQuest(int questId);

    string AcceptQuest(int questId);

    string TurnInQuest(int questId);

    string LoadBank();

    string BankToInventory(int itemId);

    string InventoryToBank(int itemId);
}
