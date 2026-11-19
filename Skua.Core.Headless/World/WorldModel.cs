using Skua.Core.Headless.Protocol;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.World;

/// <summary>
/// The game state as the Flash client would hold it, rebuilt from server packets.
/// </summary>
/// <remarks>
/// <para>
/// Storage mirrors the AS3 <c>world</c> object: <c>world.myAvatar.objData</c>, <c>world.uoTree</c>,
/// <c>world.monTree</c>, <c>world.questTree</c>, <c>world.bankinfo.items</c> and so on hold the server's own JSON
/// objects with the server's field names. <see cref="GetGameObject"/> walks that tree by path, so the next layer
/// can answer Skua's <c>getGameObject("world.myAvatar.objData.strUsername")</c> directly. Typed snapshots
/// (<see cref="GetPlayer"/>, <see cref="GetInventory"/> ...) are views over the same tree.
/// </para>
/// <para>
/// Threading: packets are applied by one reader loop (<see cref="Apply"/>); every read and write takes the same
/// lock, and everything returned is a copy, so any thread may read at any time.
/// </para>
/// <para>
/// Scope: this layer records what packets say. Client-side game rules (skill readiness, auto attack, quest bit
/// decoding of strQuests, faction/class rank math, inventory pools, the potion slot in the skill bar, map
/// scripts) are deliberately not implemented here; see the seams noted on each member.
/// </para>
/// </remarks>
public sealed partial class WorldModel
{
    private const int MaxMessages = 200;
    private const int MaxActionResults = 100;
    private const int MaxSampleLength = 500;

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, long> _handled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _unhandled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _unhandledSamples = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _uidToName = new();
    private readonly Dictionary<int, DropState> _drops = new();
    private readonly List<int> _dropOrder = new();
    private readonly HashSet<int> _acceptedQuests = new();
    private readonly Dictionary<int, int> _questValueUpdates = new();
    private readonly Dictionary<string, DateTimeOffset> _skillLastSent = new(StringComparer.Ordinal);
    private readonly Queue<ServerMessageInfo> _messages = new();
    private readonly Queue<JsonObject> _actionResults = new();

    private JsonObject _world = null!;
    private JsonObject? _lastArea;
    private string? _me;
    private int _myUid;
    private bool _connected;
    private bool _characterLoaded;
    private bool _inventoryLoaded;
    private bool _bankLoaded;
    private string? _zone;
    private ServerMoveRequest? _pendingServerMove;
    private JsonObject? _lastTurnIn;
    private long _version;
    private long _total;

    public WorldModel(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        ResetCore();
    }

    /// <summary>Raised after each applied packet, outside the lock, on the thread that applied it.</summary>
    public event EventHandler<WorldChangedEventArgs>? Changed;

    /// <summary>Raised for each packet the model has no handler for (also counted in <see cref="GetPacketCounters"/>).</summary>
    public event EventHandler<InboundPacket>? UnhandledPacket;

    /// <summary>Incremented on every change; cheap way to tell whether anything happened.</summary>
    public long Version
    {
        get { lock (_gate) return _version; }
    }

    /// <summary>The logged-in user's id (<c>sfc.myUserId</c>), 0 before login.</summary>
    public int MyUserId
    {
        get { lock (_gate) return _myUid; }
    }

    /// <summary>The logged-in user's name, lower case as the client keys it, or null before login.</summary>
    public string? MyUsername
    {
        get { lock (_gate) return _me; }
    }

    /// <summary>The current room id (<c>world.curRoom</c>), used as the room field of most commands.</summary>
    public int RoomId
    {
        get { lock (_gate) return JsonUtil.AsInt(_world["curRoom"], 1); }
    }

    /// <summary>The server's AFK flag on this character's leaf.</summary>
    public bool IsMeAfk()
    {
        lock (_gate) return MyLeaf is JsonObject leaf && JsonUtil.AsBool(leaf["afk"]);
    }

    public bool IsInventoryLoaded
    {
        get { lock (_gate) return _inventoryLoaded; }
    }

    /// <summary>
    /// The bank arrived this connection (the game's <c>myAvatar.bank != null</c>): over HTTP
    /// (<see cref="AddItemsToBank"/>) or as a socket <c>loadBank</c> reply.
    /// </summary>
    public bool IsBankLoaded
    {
        get { lock (_gate) return _bankLoaded; }
    }

    /// <summary><c>world.myAvatar.objData.CharID</c> from initUserData (0 before it arrives): the game's <c>ccid</c>.</summary>
    public int CharId
    {
        get { lock (_gate) return JsonUtil.AsInt(ObjData["CharID"]); }
    }

    /// <summary>
    /// Game.onBankComplete for the <c>api/char/bank</c> reply: BankController.addItemsToBank (each item by ItemID,
    /// replacing one already known; classes are never kept in the bank), then <c>myAvatar.bank = []</c> (loaded). The
    /// used-slot count (<c>iBankCount</c>) is not touched: the game takes it from loadInventoryBig's bankCount.
    /// </summary>
    /// <returns>How many items the bank now holds.</returns>
    public int AddItemsToBank(JsonArray items)
    {
        int count = 0;
        Write((_, _) =>
        {
            AddItemsToBankCore(items);
            _bankLoaded = true;
            count = BankItems.Count;
        }, "bank");
        return count;
    }

    /// <summary>Clears everything (new connection).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            ResetCore();
            _version++;
        }
        Changed?.Invoke(this, new WorldChangedEventArgs("reset", true, Version));
    }

    /// <summary>Sets who we are, from the loginResponse.</summary>
    public void SetIdentity(int userId, string username)
    {
        lock (_gate)
        {
            SetIdentityCore(userId, username);
            _version++;
        }
    }

    /// <summary>Socket state, reported at <c>sfc.isConnected</c>.</summary>
    public void SetConnected(bool connected)
    {
        lock (_gate)
        {
            _connected = connected;
            _version++;
        }
    }

    private void ResetCore()
    {
        _world = new JsonObject
        {
            ["strMapName"] = string.Empty,
            ["strAreaName"] = string.Empty,
            ["strMapFileName"] = string.Empty,
            ["strFrame"] = "Enter",
            ["strPad"] = "Spawn",
            ["curRoom"] = 1,
            ["areaUsers"] = new JsonArray(),
            ["uoTree"] = new JsonObject(),
            ["monTree"] = new JsonObject(),
            ["mondef"] = new JsonArray(),
            ["monmap"] = new JsonArray(),
            ["questTree"] = new JsonObject(),
            ["shopinfo"] = null,
            ["bankinfo"] = new JsonObject { ["items"] = new JsonArray() },
            ["actions"] = new JsonObject { ["active"] = new JsonArray(), ["passive"] = new JsonArray() },
            ["avatars"] = new JsonObject(),
            ["myAvatar"] = new JsonObject
            {
                ["uid"] = 0,
                ["pnm"] = string.Empty,
                ["objData"] = new JsonObject(),
                ["items"] = new JsonArray(),
                ["tempitems"] = new JsonArray(),
                ["houseitems"] = new JsonArray(),
                ["factions"] = new JsonArray(),
                ["iBankCount"] = 0,
                ["target"] = null,
            },
        };
        _lastArea = null;
        _me = null;
        _myUid = 0;
        _connected = false;
        _characterLoaded = false;
        _inventoryLoaded = false;
        _bankLoaded = false;
        _zone = null;
        _pendingServerMove = null;
        _lastTurnIn = null;
        _uidToName.Clear();
        _drops.Clear();
        _dropOrder.Clear();
        _acceptedQuests.Clear();
        _questValueUpdates.Clear();
        _skillLastSent.Clear();
        _messages.Clear();
        _actionResults.Clear();
        _handled.Clear();
        _unhandled.Clear();
        _unhandledSamples.Clear();
        _total = 0;
    }

    private void SetIdentityCore(int userId, string username)
    {
        _myUid = userId;
        _me = username.ToLowerInvariant();
        MyAvatar["uid"] = userId;
        MyAvatar["pnm"] = _me;
        _uidToName[userId] = _me;
        JsonObject leaf = LeafOrCreate(_me);
        leaf["entID"] = userId;
        leaf["uoName"] = _me;
        if (ObjData["strUsername"] is null)
            ObjData["strUsername"] = username;
    }

    // ---------------------------------------------------------------- tree accessors (call under _gate)
    private JsonObject MyAvatar => (JsonObject)_world["myAvatar"]!;
    private JsonObject ObjData => (JsonObject)MyAvatar["objData"]!;
    private JsonArray Items => (JsonArray)MyAvatar["items"]!;
    private JsonArray TempItems => (JsonArray)MyAvatar["tempitems"]!;
    private JsonArray HouseItems => (JsonArray)MyAvatar["houseitems"]!;
    private JsonArray Factions => (JsonArray)MyAvatar["factions"]!;
    private JsonObject UoTree => (JsonObject)_world["uoTree"]!;
    private JsonObject MonTree => (JsonObject)_world["monTree"]!;
    private JsonArray MonDef => (JsonArray)_world["mondef"]!;
    private JsonArray MonMap => (JsonArray)_world["monmap"]!;
    private JsonObject QuestTree => (JsonObject)_world["questTree"]!;
    private JsonArray BankItems => (JsonArray)((JsonObject)_world["bankinfo"]!)["items"]!;
    private JsonObject Actions => (JsonObject)_world["actions"]!;
    private JsonArray AreaUsers => (JsonArray)_world["areaUsers"]!;
    private JsonObject Avatars => (JsonObject)_world["avatars"]!;
    private JsonObject? MyLeaf => _me is null ? null : UoTree[_me] as JsonObject;

    private JsonObject LeafOrCreate(string name)
    {
        string key = name.ToLowerInvariant();
        if (UoTree[key] is JsonObject leaf)
            return leaf;
        leaf = new JsonObject { ["uoName"] = key, ["auras"] = new JsonArray() };
        UoTree[key] = leaf;
        return leaf;
    }

    // ---------------------------------------------------------------- object-path reads

    /// <summary>
    /// The JSON text of the value at <paramref name="path"/> (for example <c>world.myAvatar.objData.strUsername</c>,
    /// <c>world.monsters.0.dataLeaf.intHP</c>, <c>world.questTree.1000.sName</c>, <c>sfc.myUserId</c>), or null when
    /// the path does not exist. An existing property holding null gives the text <c>null</c>.
    /// </summary>
    public string? GetGameObject(string path)
    {
        lock (_gate)
        {
            return TryResolve(path, out JsonNode? node) ? JsonUtil.ToJson(node) : null;
        }
    }

    /// <summary>Like <see cref="GetGameObject"/> but returns a detached copy of the node.</summary>
    public bool TryGetGameObjectNode(string path, out JsonNode? node)
    {
        lock (_gate)
        {
            if (!TryResolve(path, out JsonNode? found))
            {
                node = null;
                return false;
            }
            node = found?.DeepClone();
            return true;
        }
    }

    /// <summary>True when the path does not exist or holds null (the AS3 <c>isNull</c>).</summary>
    public bool IsNull(string path)
    {
        lock (_gate)
        {
            return !TryResolve(path, out JsonNode? node) || node is null;
        }
    }

    /// <summary>The set of top-level object-path roots <see cref="GetGameObject"/> understands.</summary>
    public static IReadOnlyList<string> PathRoots { get; } = new[] { "world", "sfc", "headless" };

    // Resolution never mutates the tree. Virtual members (world.myAvatar.dataLeaf, world.monsters, sfc.*,
    // headless.*) are built as detached nodes.
    private bool TryResolve(string path, out JsonNode? node)
    {
        node = null;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string[] parts = SplitPath(path);
        JsonNode? current;
        int i = 1;
        switch (parts[0])
        {
            case "world":
                current = _world;
                break;

            case "sfc":
                current = new JsonObject
                {
                    ["isConnected"] = _connected,
                    ["myUserId"] = _myUid,
                    ["myUserName"] = _me,
                };
                break;

            case "headless":
                current = BuildHeadlessNode();
                break;

            default:
                return false;
        }

        for (; i < parts.Length; i++)
        {
            string part = parts[i];
            if (ReferenceEquals(current, _world) && part == "monsters")
            {
                current = BuildMonstersArray();
                continue;
            }
            if (ReferenceEquals(current, MyAvatar) && part == "dataLeaf")
            {
                current = MyLeaf;
                if (current is null)
                    return false;
                continue;
            }

            switch (current)
            {
                case JsonObject obj:
                    if (!obj.TryGetPropertyValue(part, out current))
                        return false;
                    break;

                case JsonArray array:
                    if (!int.TryParse(part, out int index) || index < 0 || index >= array.Count)
                        return false;
                    current = array[index];
                    break;

                default:
                    return false;
            }
        }

        if (ReferenceEquals(current, _world))
        {
            JsonObject copy = (JsonObject)_world.DeepClone();
            copy["monsters"] = BuildMonstersArray();
            ((JsonObject)copy["myAvatar"]!)["dataLeaf"] = MyLeaf?.DeepClone();
            node = copy;
            return true;
        }
        if (ReferenceEquals(current, MyAvatar))
        {
            JsonObject copy = (JsonObject)MyAvatar.DeepClone();
            copy["dataLeaf"] = MyLeaf?.DeepClone();
            node = copy;
            return true;
        }

        node = current;
        return true;
    }

    private static string[] SplitPath(string path)
    {
        // Accept world.questTree["1000"] / world.items[0] as well as dotted paths.
        if (path.Contains('['))
        {
            path = path.Replace("[\"", ".", StringComparison.Ordinal).Replace("\"]", string.Empty, StringComparison.Ordinal)
                .Replace("['", ".", StringComparison.Ordinal).Replace("']", string.Empty, StringComparison.Ordinal)
                .Replace("[", ".", StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
        }
        return path.Split('.', StringSplitOptions.RemoveEmptyEntries);
    }

    private JsonArray BuildMonstersArray()
    {
        JsonArray result = new();
        foreach ((JsonObject def, JsonObject placement, JsonObject? leaf) in LiveMonsterSlots())
        {
            JsonObject objData = (JsonObject)def.DeepClone();
            JsonUtil.Merge(objData, placement);
            result.Add(new JsonObject
            {
                ["objData"] = objData,
                ["dataLeaf"] = leaf?.DeepClone(),
            });
        }
        return result;
    }

    // Non-AS3 state that has no home in the game's world object.
    private JsonObject BuildHeadlessNode()
    {
        JsonArray drops = new();
        foreach (int id in _dropOrder)
        {
            DropState d = _drops[id];
            JsonObject raw = (JsonObject)d.Raw.DeepClone();
            raw["iQty"] = d.Quantity;
            drops.Add(raw);
        }
        JsonArray accepted = new();
        foreach (int id in _acceptedQuests.Order())
            accepted.Add(id);
        JsonObject questValues = new();
        foreach (KeyValuePair<int, int> pair in _questValueUpdates)
            questValues[pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = pair.Value;
        return new JsonObject
        {
            ["drops"] = drops,
            ["acceptedQuests"] = accepted,
            ["questValueUpdates"] = questValues,
            ["zone"] = _zone,
            ["characterLoaded"] = _characterLoaded,
            ["inventoryLoaded"] = _inventoryLoaded,
            ["bankLoaded"] = _bankLoaded,
            ["lastTurnIn"] = _lastTurnIn?.DeepClone(),
        };
    }

    // World.initMonsters: a monmap slot can list several candidate monsters (a cluster); the live one is the MonID
    // its monTree leaf names. One entry per MonMapID.
    private IEnumerable<(JsonObject Def, JsonObject Placement, JsonObject? Leaf)> LiveMonsterSlots()
    {
        HashSet<int> seen = new();
        List<(JsonObject, JsonObject, JsonObject?)> fallback = new();
        foreach (JsonNode? node in MonMap)
        {
            if (node is not JsonObject placement)
                continue;
            int monMapId = JsonUtil.AsInt(placement["MonMapID"]);
            int monId = JsonUtil.AsInt(placement["MonID"]);
            JsonObject? leaf = MonTree[monMapId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
            int? liveMonId = leaf is null ? null : JsonUtil.AsIntOrNull(leaf["MonID"]);
            JsonObject def = FindMonDef(monId) ?? new JsonObject { ["MonID"] = monId };
            if (liveMonId is not null && liveMonId != monId)
            {
                fallback.Add((def, placement, leaf));
                continue;
            }
            if (seen.Add(monMapId))
                yield return (def, placement, leaf);
        }
        foreach ((JsonObject def, JsonObject placement, JsonObject? leaf) in fallback)
        {
            // A slot whose leaf names a MonID missing from monmap: keep the first candidate so it is not lost.
            if (seen.Add(JsonUtil.AsInt(placement["MonMapID"])))
                yield return (def, placement, leaf);
        }
    }

    private JsonObject? FindMonDef(int monId)
    {
        foreach (JsonNode? node in MonDef)
        {
            if (node is JsonObject def && JsonUtil.AsInt(def["MonID"]) == monId)
                return def;
        }
        return null;
    }

    // ---------------------------------------------------------------- typed snapshots

    /// <summary>The logged-in character, or null before the loginResponse.</summary>
    public PlayerInfo? GetPlayer()
    {
        lock (_gate)
        {
            if (_me is null)
                return null;
            JsonObject o = ObjData;
            JsonObject leaf = MyLeaf ?? new JsonObject();
            return new PlayerInfo(
                _myUid,
                JsonUtil.AsString(o["strUsername"]) ?? _me,
                JsonUtil.AsIntOrNull(o["intLevel"]) ?? JsonUtil.AsInt(leaf["intLevel"]),
                JsonUtil.AsLong(o["intGold"]),
                JsonUtil.AsLong(o["intCoins"]),
                JsonUtil.AsLong(o["intExp"]),
                JsonUtil.AsLong(o["intExpToLevel"]),
                JsonUtil.AsString(o["strClassName"]) ?? string.Empty,
                JsonUtil.AsLong(o["iCP"]),
                JsonUtil.AsInt(leaf["intHP"]),
                JsonUtil.AsInt(leaf["intHPMax"]),
                JsonUtil.AsInt(leaf["intMP"]),
                JsonUtil.AsInt(leaf["intMPMax"]),
                JsonUtil.AsInt(leaf["intState"], 1),
                JsonUtil.AsBool(leaf["afk"]),
                JsonUtil.AsString(_world["strFrame"]) ?? "Enter",
                JsonUtil.AsString(_world["strPad"]) ?? "Spawn",
                JsonUtil.AsInt(o["iBagSlots"]),
                JsonUtil.AsInt(o["iBankSlots"]),
                JsonUtil.AsInt(o["iHouseSlots"]),
                JsonUtil.AsInt(o["intAccessLevel"]),
                JsonUtil.AsInt(o["iUpgDays"]),
                _characterLoaded,
                _inventoryLoaded,
                (JsonObject)o.DeepClone(),
                (JsonObject)leaf.DeepClone());
        }
    }

    public AreaInfo GetArea()
    {
        lock (_gate)
        {
            return new AreaInfo(
                JsonUtil.AsInt(_world["curRoom"], 1),
                JsonUtil.AsString(_world["strMapName"]) ?? string.Empty,
                JsonUtil.AsString(_world["strAreaName"]) ?? string.Empty,
                JsonUtil.AsString(_world["strMapFileName"]) ?? string.Empty,
                JsonUtil.AsString(_world["strFrame"]) ?? "Enter",
                JsonUtil.AsString(_world["strPad"]) ?? "Spawn",
                AreaUsers.Count,
                LiveMonsterSlots().Count(),
                (JsonObject?)_lastArea?.DeepClone());
        }
    }

    public IReadOnlyList<ItemInfo> GetInventory()
    {
        lock (_gate) return ItemsOf(Items, temp: false);
    }

    public IReadOnlyList<ItemInfo> GetTempInventory()
    {
        lock (_gate) return ItemsOf(TempItems, temp: true);
    }

    public IReadOnlyList<ItemInfo> GetHouseInventory()
    {
        lock (_gate) return ItemsOf(HouseItems, temp: false);
    }

    /// <summary>Bank items known so far (the bank is only sent on <c>loadBank</c>; see <see cref="IsBankLoaded"/>).</summary>
    public IReadOnlyList<ItemInfo> GetBank()
    {
        lock (_gate) return ItemsOf(BankItems, temp: false);
    }

    private static List<ItemInfo> ItemsOf(JsonArray array, bool temp)
    {
        List<ItemInfo> list = new(array.Count);
        foreach (JsonNode? node in array)
        {
            if (node is JsonObject o)
                list.Add(ItemInfo.From(o, temp));
        }
        return list;
    }

    /// <summary>Every quest in the quest tree plus every accepted quest whose data is not loaded.</summary>
    public IReadOnlyList<QuestInfo> GetQuests()
    {
        lock (_gate)
        {
            List<QuestInfo> list = new();
            HashSet<int> listed = new();
            foreach (KeyValuePair<string, JsonNode?> pair in QuestTree)
            {
                if (pair.Value is not JsonObject q || !int.TryParse(pair.Key, out int id))
                    continue;
                list.Add(QuestFrom(id, q));
                listed.Add(id);
            }
            foreach (int id in _acceptedQuests)
            {
                if (!listed.Contains(id))
                    list.Add(QuestFrom(id, null));
            }
            return list;
        }
    }

    public QuestInfo? GetQuest(int questId)
    {
        lock (_gate)
        {
            JsonObject? q = QuestTree[questId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
            if (q is null && !_acceptedQuests.Contains(questId))
                return null;
            return QuestFrom(questId, q);
        }
    }

    public IReadOnlyCollection<int> GetAcceptedQuestIds()
    {
        lock (_gate) return _acceptedQuests.Order().ToArray();
    }

    /// <summary>
    /// Raw <c>updateQuest</c> values (slot index to value) received this session. Seam: the client folds these into
    /// the base-36 <c>strQuests*</c> strings; that decoding is left to the next layer, which can combine this with
    /// <see cref="GetQuestStrings"/>.
    /// </summary>
    public IReadOnlyDictionary<int, int> GetQuestValueUpdates()
    {
        lock (_gate) return new Dictionary<int, int>(_questValueUpdates);
    }

    /// <summary>The raw <c>strQuests</c>, <c>strQuests2</c> ... fields of objData, as the server sent them.</summary>
    public IReadOnlyDictionary<string, string> GetQuestStrings()
    {
        lock (_gate)
        {
            Dictionary<string, string> result = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonNode?> pair in ObjData)
            {
                if (pair.Key.StartsWith("strQuests", StringComparison.Ordinal) && JsonUtil.AsString(pair.Value) is string s)
                    result[pair.Key] = s;
            }
            return result;
        }
    }

    private QuestInfo QuestFrom(int id, JsonObject? q)
    {
        return new QuestInfo(
            id,
            JsonUtil.AsString(q?["sName"]) ?? string.Empty,
            q is not null,
            JsonUtil.AsString(q?["status"]),
            _acceptedQuests.Contains(id),
            JsonUtil.AsInt(q?["iSlot"], -1),
            JsonUtil.AsInt(q?["iValue"]),
            JsonUtil.AsBool(q?["bOnce"]),
            JsonUtil.AsBool(q?["bUpg"]),
            JsonUtil.AsInt(q?["iLvl"]),
            (JsonObject?)q?.DeepClone());
    }

    /// <summary>Monster slots of the current map with their live state.</summary>
    public IReadOnlyList<MonsterInfo> GetMonsters()
    {
        lock (_gate)
        {
            List<MonsterInfo> list = new();
            foreach ((JsonObject def, JsonObject placement, JsonObject? leaf) in LiveMonsterSlots())
                list.Add(MonsterFrom(def, placement, leaf));
            return list;
        }
    }

    public MonsterInfo? GetMonster(int monMapId)
    {
        lock (_gate)
        {
            foreach ((JsonObject def, JsonObject placement, JsonObject? leaf) in LiveMonsterSlots())
            {
                if (JsonUtil.AsInt(placement["MonMapID"]) == monMapId)
                    return MonsterFrom(def, placement, leaf);
            }
            return null;
        }
    }

    private static MonsterInfo MonsterFrom(JsonObject def, JsonObject placement, JsonObject? leaf)
    {
        return new MonsterInfo(
            JsonUtil.AsInt(placement["MonMapID"]),
            JsonUtil.AsIntOrNull(leaf?["MonID"]) ?? JsonUtil.AsInt(placement["MonID"]),
            JsonUtil.AsString(def["strMonName"]) ?? string.Empty,
            JsonUtil.AsIntOrNull(leaf?["iLvl"]) ?? JsonUtil.AsIntOrNull(def["intLevel"]) ?? 0,
            JsonUtil.AsString(def["sRace"]) ?? string.Empty,
            JsonUtil.AsString(leaf?["strFrame"]) ?? JsonUtil.AsString(placement["strFrame"]) ?? string.Empty,
            JsonUtil.AsInt(leaf?["intHP"]),
            JsonUtil.AsInt(leaf?["intHPMax"]),
            JsonUtil.AsInt(leaf?["intState"], leaf is null ? 0 : 1),
            (JsonObject)def.DeepClone(),
            (JsonObject)placement.DeepClone(),
            (JsonObject?)leaf?.DeepClone());
    }

    /// <summary>Players in the current room, including this character.</summary>
    public IReadOnlyList<AreaUserInfo> GetAreaUsers()
    {
        lock (_gate)
        {
            List<AreaUserInfo> list = new();
            foreach (KeyValuePair<string, JsonNode?> pair in UoTree)
            {
                if (pair.Value is not JsonObject leaf)
                    continue;
                int? uid = JsonUtil.AsIntOrNull(leaf["entID"]);
                JsonObject? objData = null;
                if (uid is not null && Avatars[uid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject avatar)
                    objData = avatar["objData"] as JsonObject;
                bool isMe = pair.Key == _me;
                if (isMe)
                    objData = ObjData;
                list.Add(new AreaUserInfo(
                    JsonUtil.AsString(leaf["strUsername"]) ?? pair.Key,
                    uid,
                    JsonUtil.AsString(leaf["strFrame"]) ?? string.Empty,
                    JsonUtil.AsString(leaf["strPad"]) ?? string.Empty,
                    JsonUtil.AsInt(leaf["intHP"]),
                    JsonUtil.AsInt(leaf["intHPMax"]),
                    JsonUtil.AsInt(leaf["intState"], 1),
                    JsonUtil.AsInt(leaf["intLevel"]),
                    isMe,
                    (JsonObject)leaf.DeepClone(),
                    (JsonObject?)objData?.DeepClone()));
            }
            return list;
        }
    }

    /// <summary>
    /// The skill bar as sent (<c>world.actions</c>). <see cref="SkillInfo.LastSent"/> is when this client last sent a
    /// <c>gar</c> for that ref: a seam for the readiness/cooldown rules of the next layer, which are not applied here.
    /// </summary>
    public IReadOnlyList<SkillInfo> GetSkills()
    {
        lock (_gate)
        {
            List<SkillInfo> list = new();
            AddSkills(list, Actions["active"] as JsonArray, passive: false);
            AddSkills(list, Actions["passive"] as JsonArray, passive: true);
            return list;
        }
    }

    private void AddSkills(List<SkillInfo> list, JsonArray? array, bool passive)
    {
        if (array is null)
            return;
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject a)
                continue;
            string skillRef = JsonUtil.AsString(a["ref"]) ?? string.Empty;
            list.Add(new SkillInfo(
                i,
                skillRef,
                JsonUtil.AsString(a["nam"]) ?? string.Empty,
                JsonUtil.AsString(a["typ"]) ?? string.Empty,
                JsonUtil.AsString(a["tgt"]) ?? string.Empty,
                JsonUtil.AsInt(a["cd"]),
                JsonUtil.AsInt(a["mp"]),
                JsonUtil.AsBool(a["auto"]),
                passive,
                _skillLastSent.TryGetValue(skillRef, out DateTimeOffset at) ? at : null,
                (JsonObject)a.DeepClone()));
        }
    }

    /// <summary>This character's stats (<c>world.myAvatar.dataLeaf.sta</c>, from <c>stu</c>).</summary>
    public JsonObject? GetStats()
    {
        lock (_gate) return (JsonObject?)(MyLeaf?["sta"] as JsonObject)?.DeepClone();
    }

    /// <summary>Auras on a subject: <c>p:&lt;uid&gt;</c> or <c>m:&lt;MonMapID&gt;</c>.</summary>
    public IReadOnlyList<AuraInfo> GetAuras(string subject)
    {
        lock (_gate)
        {
            JsonObject? leaf = LeafOfSubject(subject);
            List<AuraInfo> list = new();
            if (leaf is null)
                return list;
            AddAuras(list, subject, leaf["auras"] as JsonArray, passive: false);
            AddAuras(list, subject, leaf["passives"] as JsonArray, passive: true);
            return list;
        }
    }

    /// <summary>Auras on this character.</summary>
    public IReadOnlyList<AuraInfo> GetMyAuras()
    {
        return GetAuras($"p:{MyUserId}");
    }

    private static void AddAuras(List<AuraInfo> list, string subject, JsonArray? array, bool passive)
    {
        if (array is null)
            return;
        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject a)
                continue;
            long ts = JsonUtil.AsLong(a["ts"]);
            list.Add(new AuraInfo(
                subject,
                JsonUtil.AsString(a["nam"]) ?? string.Empty,
                JsonUtil.AsDoubleOrNull(a["val"]),
                JsonUtil.AsDoubleOrNull(a["dur"]),
                JsonUtil.AsIntOrNull(a["stk"]) ?? 1,
                passive,
                JsonUtil.AsBool(a["persist"]),
                JsonUtil.AsString(a["cInf"]),
                DateTimeOffset.FromUnixTimeMilliseconds(ts),
                (JsonObject)a.DeepClone()));
        }
    }

    /// <summary>Drops offered and not picked up, oldest first.</summary>
    public IReadOnlyList<DropInfo> GetDrops()
    {
        lock (_gate)
        {
            List<DropInfo> list = new(_dropOrder.Count);
            foreach (int id in _dropOrder)
            {
                DropState d = _drops[id];
                list.Add(new DropInfo(id, JsonUtil.AsString(d.Raw["sName"]) ?? string.Empty, d.Quantity, d.TimesOffered, d.LastOffered, (JsonObject)d.Raw.DeepClone()));
            }
            return list;
        }
    }

    public ShopInfo? GetShop()
    {
        lock (_gate)
        {
            if (_world["shopinfo"] is not JsonObject shop)
                return null;
            List<ShopItemInfo> items = new();
            if (shop["items"] is JsonArray array)
            {
                foreach (JsonNode? node in array)
                {
                    if (node is not JsonObject it)
                        continue;
                    items.Add(new ShopItemInfo(
                        JsonUtil.AsInt(it["ItemID"]),
                        JsonUtil.AsInt(it["ShopItemID"]),
                        JsonUtil.AsString(it["sName"]) ?? string.Empty,
                        JsonUtil.AsInt(it["iCost"]),
                        JsonUtil.AsBool(it["bCoins"]),
                        JsonUtil.AsBool(it["bUpg"]),
                        JsonUtil.AsInt(it["iQty"], 1),
                        (JsonObject)it.DeepClone()));
                }
            }
            return new ShopInfo(JsonUtil.AsInt(shop["ShopID"]), JsonUtil.AsString(shop["sName"]) ?? string.Empty, items, (JsonObject)shop.DeepClone());
        }
    }

    public IReadOnlyList<FactionInfo> GetFactions()
    {
        lock (_gate)
        {
            List<FactionInfo> list = new();
            foreach (JsonNode? node in Factions)
            {
                if (node is JsonObject f)
                    list.Add(new FactionInfo(JsonUtil.AsInt(f["FactionID"]), JsonUtil.AsString(f["sName"]) ?? string.Empty, JsonUtil.AsLong(f["iRep"]), (JsonObject)f.DeepClone()));
            }
            return list;
        }
    }

    /// <summary>Recent chat/server lines, oldest first.</summary>
    public IReadOnlyList<ServerMessageInfo> GetRecentMessages()
    {
        lock (_gate) return _messages.ToArray();
    }

    /// <summary>Recent action results (<c>sara</c>/<c>sarsa</c>/<c>sar</c>/<c>sars</c> entries), oldest first. Seam for combat rules.</summary>
    public IReadOnlyList<JsonObject> GetRecentActionResults()
    {
        lock (_gate) return _actionResults.Select(o => (JsonObject)o.DeepClone()).ToArray();
    }

    /// <summary>The last position change the server asked for (gtc/resTimed/dungeonMTC), not yet acted on.</summary>
    public ServerMoveRequest? GetPendingServerMove()
    {
        lock (_gate) return _pendingServerMove;
    }

    /// <summary>The zone set by the last <c>event</c> packet (<c>args.zoneSet</c>), if any.</summary>
    public string? Zone
    {
        get { lock (_gate) return _zone; }
    }

    public PacketCounters GetPacketCounters()
    {
        lock (_gate)
        {
            return new PacketCounters(
                new Dictionary<string, long>(_handled),
                new Dictionary<string, long>(_unhandled),
                new Dictionary<string, string>(_unhandledSamples),
                _total);
        }
    }

    private JsonObject? LeafOfSubject(string subject)
    {
        int colon = subject.IndexOf(':');
        if (colon <= 0 || !int.TryParse(subject.AsSpan(colon + 1), out int id))
            return null;
        if (subject[0] == 'm')
            return MonTree[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
        if (subject[0] == 'p')
            return _uidToName.TryGetValue(id, out string? name) ? UoTree[name] as JsonObject : null;
        return null;
    }

    private sealed class DropState
    {
        public DropState(JsonObject raw, int quantity, DateTimeOffset at)
        {
            Raw = raw;
            Quantity = quantity;
            TimesOffered = 1;
            LastOffered = at;
        }

        public JsonObject Raw { get; set; }

        public int Quantity { get; set; }

        public int TimesOffered { get; set; }

        public DateTimeOffset LastOffered { get; set; }
    }
}
