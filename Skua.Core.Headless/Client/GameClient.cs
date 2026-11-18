using Skua.Core.Headless.Maps;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Rules;
using Skua.Core.Headless.World;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Client;

/// <summary>Writes a packet to the game server (fire and forget). Returns false when there is no connection.</summary>
public interface IPacketSender
{
    bool Send(string packet);
}

/// <summary>Settings for <see cref="GameClient"/>.</summary>
public sealed class GameClientOptions
{
    /// <summary>How long a map counts as loading after <c>moveToArea</c> (the Flash client loads the map file).</summary>
    public TimeSpan MapLoadTime { get; set; } = TimeSpan.FromMilliseconds(900);

    /// <summary>A quest asked for is not asked for again within this time while its answer is pending (VWorld.getQuests).</summary>
    public TimeSpan QuestRequestWait { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Extra time added to the auto attack's own cooldown when the server never answers a swing.</summary>
    public TimeSpan AutoAttackRetrySlack { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Act on server-requested moves (<c>gtc</c>, <c>dungeonMTC</c>, <c>resTimed</c>) by entering that cell, as the Flash client does.</summary>
    public bool FollowServerMoves { get; set; } = true;
}

/// <summary>Who the character has targeted.</summary>
/// <param name="Kind">'m' monster (Id = MonMapID) or 'p' player (Id = uid, Name = lower-case name).</param>
public sealed record TargetRef(char Kind, int Id, string? Name)
{
    public string Subject => Kind == 'm' ? $"m:{Id}" : $"p:{Id}";
}

/// <summary>A message from the game client layer.</summary>
public sealed class GameClientLogEventArgs : EventArgs
{
    public GameClientLogEventArgs(string message)
    {
        Message = message;
    }

    public string Message { get; }
}

/// <summary>
/// The parts of the AQW Flash client the server does not do: targeting, skill readiness and cooldowns (haste,
/// global cooldown), turning a skill into a <c>gar</c> with the right targets, the auto attack loop, the skill bar
/// (potion slot kept across class changes), client action throttles, quest progress and completion checks, shop and
/// inventory checks, HUD aura stacks, map loading state and cells, and following server-requested moves.
/// </summary>
/// <remarks>
/// <para>
/// Sits on a <see cref="WorldModel"/> (what the server said) and an <see cref="IPacketSender"/>; everything it decides
/// is testable without a socket. Feed it every received packet after the model applied it (<see cref="OnInbound"/>)
/// and every sent packet (<see cref="OnOutbound"/>).
/// </para>
/// <para>
/// Ported from Seraph app/lib/skua/swfworld.js (VWorld, VGame, SwfWorld), MIT, a stand-in for skua.swf plus the game
/// file that follows the decompiled World.as / Game.as; skua.swf's own semantics come from this repository's
/// Skua.AS3 sources. Thread safe: one lock for the client state; never called back while holding the model's lock.
/// </para>
/// </remarks>
public sealed partial class GameClient : IDisposable
{
    private readonly object _gate = new();
    private readonly IPacketSender _sender;
    private readonly TimeProvider _time;
    private readonly MapCellCache? _maps;
    private readonly Dictionary<string, long> _questAsked = new(StringComparer.Ordinal);
    private readonly HashSet<int> _pendingAccept = new();
    private readonly List<JsonObject?> _active = new();
    private readonly List<JsonObject> _passive = new();
    private TargetRef? _target;
    private ITimer? _autoTimer;
    private ITimer? _mapTimer;
    private int _actionId;
    private long _gcdStart;
    private (JsonObject Skill, long PrevTs, long PrevGcd, long At)? _lastAct;
    private bool _newClass;
    private bool _mapLoading;
    private long _mapGeneration;
    private MapCells? _cells;
    private string _spawnCell = "Enter";
    private string _spawnPad = "Spawn";
    private JsonObject? _shopBuyItem;
    private string _popupLabel = "Init";
    private int _walkSpeed = 8;
    private int _miscSlots = InventoryRules.DefaultMiscSlots;
    private readonly List<string> _partyMembers = new();
    private string _partyOwner = string.Empty;
    private int _partyId;
    private int _scale = 1;
    private int _x;
    private int _y;
    private bool _disposed;

    public GameClient(WorldModel world, IPacketSender sender, TimeProvider? time = null, MapCellCache? maps = null, GameClientOptions? options = null)
    {
        World = world;
        _sender = sender;
        _time = time ?? TimeProvider.System;
        _maps = maps;
        Options = options ?? new GameClientOptions();
        Locks = new ActionLocks(_time);
        LitePreferences = DefaultLitePreferences();
    }

    /// <summary>Things worth a line in a log (refused actions, server moves followed).</summary>
    public event EventHandler<GameClientLogEventArgs>? Log;

    public WorldModel World { get; }

    public GameClientOptions Options { get; }

    /// <summary>World.lock.</summary>
    public ActionLocks Locks { get; }

    /// <summary>AQW 5.0 HUD aura stacks.</summary>
    public HudAuraTable HudAuras { get; } = new();

    /// <summary><c>litePreference.data</c> (lock it when reading or writing).</summary>
    public JsonObject LitePreferences { get; }

    /// <summary>The cells and pads of the current map, once known.</summary>
    public MapCells? Cells
    {
        get { lock (_gate) return _cells; }
    }

    /// <summary>World.mapLoadInProgress.</summary>
    public bool MapLoadInProgress
    {
        get { lock (_gate) return _mapLoading; }
    }

    public TargetRef? Target
    {
        get { lock (_gate) return _target; }
    }

    public bool AutoAttacking
    {
        get { lock (_gate) return _autoTimer is not null; }
    }

    /// <summary>World.WALKSPEED.</summary>
    public int WalkSpeed
    {
        get { lock (_gate) return _walkSpeed; }
        set { lock (_gate) _walkSpeed = value; }
    }

    /// <summary>World.SCALE.</summary>
    public int Scale
    {
        get { lock (_gate) return _scale; }
        set { lock (_gate) _scale = value; }
    }

    /// <summary>The avatar's clip position (pMC.x / pMC.y); there is no walking, so it is where it was last told to go.</summary>
    public (int X, int Y) Position
    {
        get { lock (_gate) return (_x, _y); }
    }

    /// <summary>ui.mcPopup.currentLabel (the bank toggles it).</summary>
    public string PopupLabel
    {
        get { lock (_gate) return _popupLabel; }
        set { lock (_gate) _popupLabel = value; }
    }

    /// <summary>World.spawnPoint.</summary>
    public (string Cell, string Pad) SpawnPoint
    {
        get { lock (_gate) return (_spawnCell, _spawnPad); }
    }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>A new connection starts from an empty client, as a relaunched game would. Lite preferences survive.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            CancelAutoAttackCore();
            _mapTimer?.Dispose();
            _mapTimer = null;
            _target = null;
            _active.Clear();
            _passive.Clear();
            _questAsked.Clear();
            _pendingAccept.Clear();
            _lastAct = null;
            _gcdStart = 0;
            _actionId = 0;
            _newClass = false;
            _mapLoading = false;
            _cells = null;
            _shopBuyItem = null;
            _popupLabel = "Init";
            _spawnCell = "Enter";
            _spawnPad = "Spawn";
            _x = 0;
            _y = 0;
            _partyMembers.Clear();
            _partyOwner = string.Empty;
            _partyId = 0;
        }
        Locks.Reset();
        HudAuras.Clear();
    }

    private static JsonObject DefaultLitePreferences()
    {
        // Seraph's LITE_DEFAULTS: display options off, the two untarget options on (as Skua sets them).
        JsonObject data = new();
        foreach (string key in new[]
        {
            "bAuras", "bBattlepet", "bCachePlayers", "bCharSelect", "bChatFilter", "bChatUI", "bColorSets", "bCustomDrops", "bDebugger",
            "bDisChatScroll", "bDraggable", "bFreezeMons", "bHideMons", "bHideNames", "bHidePlayers", "bHideUI", "bMonsType", "bQuestLog",
            "bQuestNotif", "bQuestPin", "bReaccept", "bSkillCD", "bSmoothBG",
        })
        {
            data[key] = false;
        }
        foreach (string key in new[]
        {
            "bDisAuraAnim", "bDisDmgDisplay", "bDisDmgStrobe", "bDisGround", "bDisHealBubble", "bDisMonAnim", "bDisQPopup", "bDisQTracker",
            "bDisSelfMAnim", "bDisSkillAnim", "bDisSoundFX", "bDisWepAnim", "bUntargetDead", "bUntargetSelf",
        })
        {
            data[key] = true;
        }
        data["dOptions"] = new JsonObject();
        data["blackList"] = new JsonArray();
        return data;
    }

    private bool LiteFlag(string name)
    {
        lock (LitePreferences)
            return JsonUtil.AsBool(LitePreferences[name]);
    }

    private void RaiseLog(string message)
    {
        Log?.Invoke(this, new GameClientLogEventArgs(message));
    }

    private bool Send(string packet)
    {
        return _sender.Send(packet);
    }

    private bool SendXt(string cmd, int? room, params object?[] args)
    {
        return Send(Packets.Xt(cmd, room ?? World.RoomId, args));
    }

    // ---------------------------------------------------------------- packets

    /// <summary>A received packet, after <see cref="World"/> applied it.</summary>
    public void OnInbound(InboundPacket p)
    {
        try
        {
            switch (p.Kind)
            {
                case PacketKind.Xt:
                    OnInboundXt(p.Command ?? string.Empty, p.Args);
                    break;

                case PacketKind.Json when p.Data is not null:
                    OnInboundJson(p.Command ?? string.Empty, p.Data);
                    break;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or InvalidCastException or ArgumentException or KeyNotFoundException)
        {
            RaiseLog($"Client rules failed on {p.Command}: {e.Message}");
        }
    }

    private void OnInboundXt(string cmd, IReadOnlyList<string> a)
    {
        switch (cmd)
        {
            case "uotls" when a.Count >= 2:
                OnUserTreeWrite(a[0], WorldModel.ParsePairs(a[1]));
                break;

            case "mtls" when a.Count >= 2 && int.TryParse(a[0], out int monId):
                OnMonsterTreeWrite(monId, WorldModel.ParsePairs(a[1]));
                break;

            case "respawnMon":
                break;

            case "spcs" when a.Count >= 1 && int.TryParse(a[0], out int slot):
                lock (_gate)
                {
                    if (_target is { Kind: 'm' } t && t.Id == slot)
                        SetTargetCore(null);
                }
                HudAuras.ClearSubject($"m:{slot}", keepPersistent: false);
                break;

            case "exitArea" when a.Count >= 1 && int.TryParse(a[0], out int uid):
                lock (_gate)
                {
                    if (_target is { Kind: 'p' } t && t.Id == uid)
                        SetTargetCore(null);
                }
                break;

            case "gtc" when a.Count >= 2:
                FollowServerMove(cmd, a[0], a[1]);
                break;

            case "dungeonMTC" when a.Count >= 2 && a[0] == "true":
                string[] cp = a[1].Split(',');
                FollowServerMove(cmd, cp[0], cp.Length > 1 ? cp[1] : "Spawn");
                break;

            case "resTimed":
                (string spawnCell, string spawnPad) = SpawnPoint;
                if (a.Count > 1 && a[0].Length > 0)
                    FollowServerMove(cmd, a[0], a[1]);
                else
                    FollowServerMove(cmd, spawnCell, spawnPad);
                break;

            case "warning":
            case "server":
                if (string.Join(' ', a).Contains("Last action was too soon", StringComparison.OrdinalIgnoreCase))
                    ActionRefused();
                break;
        }
    }

    private void OnInboundJson(string cmd, JsonObject d)
    {
        switch (cmd)
        {
            case "moveToArea":
                OnMoveToArea(d);
                break;

            case "uotls":
                OnUserTreeWrite(JsonUtil.AsString(d["unm"]) ?? string.Empty, d["o"] as JsonObject);
                break;

            case "mtls":
                OnMonsterTreeWrite(JsonUtil.AsInt(d["id"]), d["o"] as JsonObject);
                break;

            case "ct":
            case "cb":
                if (d["m"] is JsonObject monsters)
                {
                    foreach (KeyValuePair<string, JsonNode?> pair in monsters)
                    {
                        if (int.TryParse(pair.Key, out int id))
                            OnMonsterTreeWrite(id, pair.Value as JsonObject);
                    }
                }
                if (d["p"] is JsonObject players)
                {
                    foreach (KeyValuePair<string, JsonNode?> pair in players)
                        OnUserTreeWrite(pair.Key, pair.Value as JsonObject);
                }
                if (d["a"] is JsonArray auraEvents)
                {
                    foreach (JsonObject ev in auraEvents.OfType<JsonObject>())
                        OnAuraEvent(JsonUtil.AsString(ev["cmd"]) ?? string.Empty, ev);
                }
                foreach (string list in new[] { "sara", "sarsa" })
                {
                    if (d[list] is JsonArray results)
                    {
                        foreach (JsonObject r in results.OfType<JsonObject>())
                            OnActionResult(r, list == "sara");
                    }
                }
                break;

            case "sar":
                OnActionResult(d, sar: true);
                break;

            case "sars":
                OnActionResult(d, sar: false);
                break;

            case "aura+":
            case "aura++":
            case "aura-":
            case "aura--":
            case "aura=":
                OnAuraEvent(cmd, d);
                break;

            case "clearAuras":
                HudAuras.ClearSubject($"p:{World.MyUserId}", keepPersistent: true);
                break;

            case "auSnap":
                if (JsonUtil.AsString(d["unm"]) is string unm && d["au"] is JsonArray au)
                {
                    DescribeAuras(d["auDesc"] as JsonObject);
                    int? snapUid = World.GetAreaUsers().FirstOrDefault(u => u.Name.Equals(unm, StringComparison.OrdinalIgnoreCase))?.UserId;
                    if (snapUid is int su)
                        HudAuras.Seed($"p:{su}", au, Now);
                }
                break;

            case "sAct":
                OnSkillBar(d);
                break;

            case "seia":
                OnSeia(d);
                break;

            case "updateClass":
                if (JsonUtil.AsIntOrNull(d["uid"]) is not int classUid || classUid == World.MyUserId)
                {
                    lock (_gate)
                        _newClass = true;
                }
                break;

            case "acceptQuest":
                OnQuestAccepted(d);
                break;

            case "getQuests":
            case "getQuests2":
                OnQuestsLoaded(d);
                break;

            case "ccqr":
                OnQuestCompleted(d);
                break;

            case "buyItem":
                lock (_gate)
                    _shopBuyItem = null;
                break;

            case "pa":
            case "pr":
            case "pl":
            case "pk":
                OnParty(cmd, d);
                break;

            case "loadInventoryBig":
                if (JsonUtil.AsIntOrNull(d["iMiscSlots"]) is int misc)
                {
                    lock (_gate)
                        _miscSlots = misc;
                }
                break;

            case "loadBank":
                lock (_gate)
                {
                    if (_popupLabel != "Bank")
                        _popupLabel = "Bank";
                }
                break;
        }
    }

    /// <summary>A packet written to the socket (whoever sent it).</summary>
    public void OnOutbound(string packet)
    {
        string? cmd = Packets.CommandOf(packet);
        if (cmd is null)
            return;
        string[] a = Packets.ArgsOf(packet);
        switch (cmd)
        {
            case "acceptQuest" when a.Length >= 1 && int.TryParse(a[0], out _):
                Locks.Stamp("acceptQuest");
                break;

            case "tryQuestComplete":
                Locks.Stamp("tryQuestComplete");
                break;

            case "loadShop":
                Locks.Stamp("loadShop");
                break;

            case "buyItem" when a.Length >= 3:
                lock (_gate)
                {
                    if (_shopBuyItem is null || JsonUtil.AsInt(_shopBuyItem["ShopItemID"]).ToString(CultureInfo.InvariantCulture) != a[2])
                        _shopBuyItem = FindShopItem(x => JsonUtil.AsString(x["ItemID"]) == a[0] && JsonUtil.AsString(x["ShopItemID"]) == a[2]);
                }
                break;

            case "cmd" when a.Length >= 1 && a[0] == "tfer":
                Locks.Stamp("tfer");
                break;
        }
    }

    // Game.as party packets: pa adds the names in ul (the leader is told only of the newcomer), pr names the new leader,
    // pr/pl/pk remove unm; leaving yourself clears the party. Ported from Seraph swfworld.js (MIT).
    private void OnParty(string cmd, JsonObject d)
    {
        string? me = World.MyUsername;
        lock (_gate)
        {
            if (cmd == "pa")
            {
                if (d["ul"] is JsonArray ul)
                {
                    foreach (string name in ul.Select(x => (JsonUtil.AsString(x) ?? string.Empty).ToLowerInvariant()))
                    {
                        if (name.Length > 0 && name != me && !_partyMembers.Contains(name))
                            _partyMembers.Add(name);
                    }
                }
                if (JsonUtil.AsString(d["owner"]) is string owner)
                    _partyOwner = owner;
                if (JsonUtil.AsIntOrNull(d["pid"]) is int pid)
                    _partyId = pid;
                return;
            }
            if (cmd == "pr" && JsonUtil.AsString(d["owner"]) is string newOwner)
                _partyOwner = newOwner;
            string gone = (JsonUtil.AsString(d["unm"]) ?? string.Empty).ToLowerInvariant();
            if (gone.Length == 0)
                return;
            if (gone == me)
            {
                _partyMembers.Clear();
                _partyId = 0;
                _partyOwner = string.Empty;
            }
            else
            {
                _partyMembers.Remove(gone);
            }
        }
    }

    /// <summary>World.partyMembers / partyOwner / partyID.</summary>
    public (IReadOnlyList<string> Members, string Owner, int Id) Party
    {
        get
        {
            lock (_gate)
                return (_partyMembers.ToArray(), _partyOwner, _partyId);
        }
    }

    private void DescribeAuras(JsonObject? descriptions)
    {
        if (descriptions is null)
            return;
        foreach (KeyValuePair<string, JsonNode?> pair in descriptions)
        {
            if (JsonUtil.AsString(pair.Value) is string text)
                HudAuras.SetDescription(pair.Key, text);
        }
    }

    private void OnMoveToArea(JsonObject d)
    {
        long generation;
        lock (_gate)
        {
            CancelAutoAttackCore();
            _target = null;
            _mapLoading = true;
            _cells = null;
            generation = ++_mapGeneration;
            _mapTimer?.Dispose();
            _mapTimer = _time.CreateTimer(_ =>
            {
                lock (_gate)
                {
                    if (_mapGeneration == generation)
                        _mapLoading = false;
                }
            }, null, Options.MapLoadTime, Timeout.InfiniteTimeSpan);
        }

        // HUD auras: players and monsters arrive with their full lists.
        HudAuras.ClearSubjects("m:");
        DescribeAuras(d["auDesc"] as JsonObject);
        long now = Now;
        if (d["uoBranch"] is JsonArray users)
        {
            foreach (JsonObject u in users.OfType<JsonObject>())
            {
                if (JsonUtil.AsIntOrNull(u["entID"]) is int uid)
                    HudAuras.Seed($"p:{uid}", u["au"] as JsonArray, now);
            }
        }
        if (d["monBranch"] is JsonArray monsters)
        {
            foreach (JsonObject m in monsters.OfType<JsonObject>())
            {
                if (m["au"] is JsonArray au)
                    HudAuras.Seed($"m:{JsonUtil.AsInt(m["MonMapID"])}", au, now);
            }
        }

        string mapFile = JsonUtil.AsString(d["strMapFileName"]) ?? string.Empty;
        if (_maps is not null && mapFile.Length > 0)
        {
            _ = _maps.GetAsync(mapFile).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    lock (_gate)
                    {
                        if (_mapGeneration == generation)
                            _cells = t.Result;
                    }
                    if (!t.Result.HasCells)
                        RaiseLog($"No cells for {mapFile}: {t.Result.Source}.");
                }
            }, TaskScheduler.Default);
        }
    }

    // Game.userTreeWrite (the client half).
    private void OnUserTreeWrite(string name, JsonObject? values)
    {
        if (values is null || name.Length == 0)
            return;
        string? me = World.MyUsername;
        int? state = values.ContainsKey("intState") ? JsonUtil.AsIntOrNull(values["intState"]) : null;
        bool mine = me is not null && name.Equals(me, StringComparison.OrdinalIgnoreCase);
        if (state is int s && s != 2)
        {
            int? uid = mine ? World.MyUserId : World.GetAreaUsers().FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.UserId;
            if (uid is int u)
                HudAuras.ClearSubject($"p:{u}", keepPersistent: false);
        }
        if (mine && state == 0)
        {
            lock (_gate)
            {
                SetTargetCore(null);
                CancelAutoAttackCore();
            }
        }
    }

    // Game.monsterTreeWrite (the client half).
    private void OnMonsterTreeWrite(int monMapId, JsonObject? values)
    {
        if (values is null)
            return;
        bool dead = values.ContainsKey("intHP") && JsonUtil.AsInt(values["intHP"]) <= 0;
        bool leftCombat = values.ContainsKey("intState") && JsonUtil.AsInt(values["intState"]) != 2;
        if (dead || leftCombat)
            HudAuras.ClearSubject($"m:{monMapId}", keepPersistent: false);
    }

    private void OnAuraEvent(string cmd, JsonObject ev)
    {
        string kind = JsonUtil.AsString(ev["cmd"]) ?? cmd;
        string? tInf = JsonUtil.AsString(ev["tInf"]);
        if (tInf is null)
            return;
        bool dead = false;
        if (tInf.StartsWith("m:", StringComparison.Ordinal) && int.TryParse(tInf.AsSpan(2), out int id))
            dead = World.GetMonster(id) is { Live: not null, State: 0 };
        HudAuras.Apply(tInf, kind, ev, Now, dead);
    }

    private void FollowServerMove(string why, string cell, string pad)
    {
        if (!Options.FollowServerMoves || cell.Length == 0)
            return;
        RaiseLog($"Server move ({why}): entering {cell}/{pad}.");
        MoveToCell(cell, pad.Length > 0 ? pad : "Spawn");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            CancelAutoAttackCore();
            _mapTimer?.Dispose();
            _mapTimer = null;
        }
    }
}
