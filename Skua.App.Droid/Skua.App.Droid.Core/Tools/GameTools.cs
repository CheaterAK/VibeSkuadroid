namespace Skua.App.Droid.Core.Tools;

/// <summary>
/// The game-side halves of the desktop tools ported to the Tools pages (Fast Travel, Loader, Grabber): what they do
/// through Skua's services on the engine's one session. Implemented in Skua.App.Droid.Plugins (on Skua.Core); the
/// ViewModels only see these interfaces and plain records, as they only see <see cref="Engine.IBotEngine"/>.
/// </summary>
/// <remarks>
/// Confirmation is the pages' job (the app's own confirmation UI): nothing here asks anything, and nothing here goes
/// through Skua's dialog service (which declines every unattended confirmation on this client).
/// </remarks>
public interface IGameTools
{
    IFastTravelBackend FastTravel { get; }

    ILoaderBackend Loader { get; }

    IGrabberBackend Grabber { get; }
}

/// <summary>What the Grabber grabs (Skua's GrabberTypes, the desktop Grabber's tabs).</summary>
public enum GrabberCategory
{
    ShopItems,
    ShopIds,
    Quests,
    MapItemIds,
    Inventory,
    HouseInventory,
    TempInventory,
    Bank,
    CellMonsters,
    MapMonsters,
}

/// <summary>The Grabber's item actions (the desktop Grabber's buttons).</summary>
public enum GrabberAction
{
    Buy,
    LoadShop,
    OpenQuest,
    AcceptQuest,
    RegisterQuest,
    FakeComplete,
    UnregisterAll,
    GetMapItem,
    Equip,
    Sell,
    SellAll,
    ToBank,
    HouseToBank,
    ToInventory,
    Kill,
    TeleportTo,
}

/// <summary>A grabbed object.</summary>
/// <param name="Id">Its ID (item, quest, shop, monster or map item ID).</param>
/// <param name="Name">Its name (map items: "Map item").</param>
/// <param name="Text">The desktop's summary line ("[ID] name | ..."), also what the search looks in.</param>
/// <param name="IsJunk">Marked as junk (Skua's junk list).</param>
/// <param name="Handle">The Skua object behind it (opaque to the page; handed back to the backend).</param>
public sealed record GrabbedItem(int Id, string Name, string Text, bool IsJunk, object Handle);

/// <summary>A quantity to ask for.</summary>
public sealed record QuantityAsk(string Label, int Min, int Max, int Initial);

/// <summary>What the page asks before an action, or why the action is refused.</summary>
/// <param name="Refusal">The action is refused (the desktop's warning), or null.</param>
/// <param name="Question">The confirmation question, or null when the action needs none (it spends or moves nothing).</param>
/// <param name="Quantity">Ask for a quantity with the question, or null.</param>
public sealed record GrabberPlan(string? Refusal, string? Question, QuantityAsk? Quantity)
{
    public static GrabberPlan Refuse(string why) => new(why, null, null);
}

/// <summary>
/// Grabber (desktop GrabberView): grab the current game state by category with Skua's grabber service, and the
/// desktop Grabber's item actions. Shop, quest and bank actions shared with the Control page go through its backend;
/// the rules (AC items, gold, one sale at a time, equipped items) are the desktop's.
/// </summary>
public interface IGrabberBackend
{
    /// <summary>Grabs a category now (Skua's GrabberService.Grab). Blocking for the map items (saved map data only).</summary>
    /// <exception cref="Engine.EngineException">The game is not available.</exception>
    IReadOnlyList<GrabbedItem> Grab(GrabberCategory category);

    /// <summary>Checks an action on the selection: refused, or what to ask.</summary>
    GrabberPlan Plan(GrabberAction action, IReadOnlyList<GrabbedItem> items);

    /// <summary>Runs an action (after the page asked). Returns a line for the user.</summary>
    /// <exception cref="Engine.EngineException">Refused or failed.</exception>
    Task<string> RunAsync(GrabberAction action, IReadOnlyList<GrabbedItem> items, int quantity, IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>The desktop's Copy text (title line, one summary line per item).</summary>
    string CopyText(string title, IReadOnlyList<GrabbedItem> items);

    /// <summary>The desktop's Export text (every property of every item).</summary>
    string DetailedText(string title, IReadOnlyList<GrabbedItem> items);
}

/// <summary>What the Loader loads by ID (desktop Loader: Shop, Quest).</summary>
public enum LoaderKind
{
    Shop,
    Quest,
}

/// <summary>The Loader's copy formats.</summary>
public enum QuestCopyFormat
{
    /// <summary>Names joined with commas.</summary>
    Names,

    /// <summary>IDs joined with commas.</summary>
    Ids,

    /// <summary>One "// name | id" line per quest.</summary>
    NamesAndIds,
}

/// <summary>A quest of Skua's quest list (QuestData.json).</summary>
public sealed record QuestListEntry(int Id, string Name, int Level, bool Member, bool Once);

/// <summary>
/// Loader (desktop LoaderView): load a shop or quests by ID, Skua's quest list (QuestData.json) with search, copy,
/// load and "fake complete", and the list's update from the game. The input rules and text formats are the desktop's
/// (Skua.Core.Models.Quests.LoaderText). Blocking members wait like Skua's API: call them off the UI thread.
/// </summary>
public interface ILoaderBackend
{
    /// <summary>Digits, commas and spaces only (the desktop's Load check).</summary>
    bool IsIdList(string input);

    /// <summary>
    /// Loads the shop (one ID; through the Control page's shop load, which checks it arrived) or the quests (Skua's
    /// quest load, then waits for them). Returns a line for the user. Blocking.
    /// </summary>
    /// <exception cref="Engine.EngineException">Not in game, a bad ID, or nothing loaded.</exception>
    string Load(LoaderKind kind, string input);

    /// <summary>Loads the given quests (the list's "Load"). Blocking.</summary>
    string LoadQuests(IReadOnlyList<int> questIds);

    /// <summary>Skua's quest list from QuestData.json (empty when the file is missing).</summary>
    Task<IReadOnlyList<QuestListEntry>> GetQuestListAsync();

    /// <summary>The desktop's search over the list: name or ID contains the query.</summary>
    bool Matches(QuestListEntry quest, string query);

    /// <summary>Text to copy, in the desktop's formats.</summary>
    string CopyText(IReadOnlyList<QuestListEntry> quests, QuestCopyFormat format);

    /// <summary>
    /// Skua's client-side "fake complete": tells this client the quest line is done up to the quest (nothing is sent
    /// to the server). Blocking (loads the quest first).
    /// </summary>
    string FakeComplete(int questId);

    /// <summary>Asks the game for quests newer than the list's highest ID and saves them to QuestData.json (Skua's updater).</summary>
    Task<IReadOnlyList<QuestListEntry>> UpdateAsync(IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>The update range "startID, endID": null when valid, else why not.</summary>
    string? ParseRange(string input, out int startId, out int endId);

    /// <summary>Asks the game for the quests of a range and saves them to QuestData.json (Skua's updater).</summary>
    Task<IReadOnlyList<QuestListEntry>> UpdateRangeAsync(int startId, int endId, IProgress<string> progress, CancellationToken cancellationToken);
}

/// <summary>A saved fast travel destination (Skua's "FastTravels" setting, "name,map,cell,pad").</summary>
public sealed record TravelDestination(string Name, string Map, string Cell, string Pad);

/// <summary>Where the character is.</summary>
public sealed record TravelLocation(string Map, string Cell, string Pad);

/// <summary>Fast Travel (desktop FastTravelView): saved destinations and Skua's map service travel.</summary>
public interface IFastTravelBackend
{
    /// <summary>The saved destinations (the desktop's list: Skua's "FastTravels" setting).</summary>
    IReadOnlyList<TravelDestination> Load();

    /// <summary>Replaces the saved destinations.</summary>
    void Save(IReadOnlyList<TravelDestination> destinations);

    /// <summary>Skua's preset destinations ("DefaultFastTravels").</summary>
    IReadOnlyList<TravelDestination> Presets { get; }

    /// <summary>Map, cell and pad the character is in; null when not in game.</summary>
    TravelLocation? CurrentLocation();

    /// <summary>
    /// Travels the way the desktop's destination cards do (Skua's map service): joins the map (in the private room
    /// <paramref name="roomNumber"/> when <paramref name="usePrivateRoom"/>), then the cell and pad. Returns once the
    /// travel started; the join runs in the background.
    /// </summary>
    /// <exception cref="Engine.EngineException">Not in game.</exception>
    void Travel(TravelDestination destination, bool usePrivateRoom, int roomNumber);
}
