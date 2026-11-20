using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Rules;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.World;

public sealed partial class WorldModel
{
    /// <summary>Commands whose state lives in the connection, not the model; seen and understood, nothing to store.</summary>
    private static readonly HashSet<string> s_connectionXml = new(StringComparer.Ordinal) { "apiOK", "apiKO", "logKO", "logout" };

    private static readonly HashSet<string> s_messageXt = new(StringComparer.Ordinal)
    {
        "server", "warning", "notify", "chatm", "whisper", "popup", "serverf", "moderator", "wheel",
        "multiLoginWarning", "logoutWarning",
    };

    /// <summary>
    /// Applies one server packet. Returns false (and counts it, and raises <see cref="UnhandledPacket"/>) when no
    /// handler knows the command; such packets are never silently dropped.
    /// </summary>
    public bool Apply(InboundPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        bool handled;
        string key = KeyOf(packet);
        long version;
        lock (_gate)
        {
            _total++;
            try
            {
                handled = packet.Kind switch
                {
                    PacketKind.Xt => ApplyXt(packet),
                    PacketKind.Json => packet.Data is not null && ApplyJson(packet.Command ?? string.Empty, packet.Data),
                    PacketKind.Xml => ApplyXml(packet),
                    _ => false,
                };
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or InvalidCastException or ArgumentException)
            {
                // A packet shaped differently than expected must not stop the reader loop: count it as unhandled.
                handled = false;
                key += "!error";
                key = BoundedKey(_unhandled, key);
                if (_unhandledSamples.Count < MaxCounterKeys)
                    _unhandledSamples.TryAdd(key, $"{e.GetType().Name}: {e.Message} | {Truncate(packet.Text)}");
            }

            if (handled)
            {
                key = BoundedKey(_handled, key);
                _handled[key] = _handled.GetValueOrDefault(key) + 1;
            }
            else
            {
                key = BoundedKey(_unhandled, key);
                _unhandled[key] = _unhandled.GetValueOrDefault(key) + 1;
                if (_unhandledSamples.Count < MaxCounterKeys)
                    _unhandledSamples.TryAdd(key, Truncate(packet.Text));
            }
            _version++;
            version = _version;
        }

        if (!handled)
            UnhandledPacket?.Invoke(this, packet);
        Changed?.Invoke(this, new WorldChangedEventArgs(key, handled, version));
        return handled;
    }

    /// <summary>
    /// Records what an outgoing packet implies for local state, as the Flash client does before the server answers:
    /// a <c>moveToCell</c> moves the avatar at once; a <c>gar</c> stamps the skill's last use (seam for cooldowns).
    /// </summary>
    public void ApplyOutbound(string packet)
    {
        string? cmd = Packets.CommandOf(packet);
        if (cmd is null)
            return;
        string[] args = Packets.ArgsOf(packet);
        lock (_gate)
        {
            switch (cmd)
            {
                case "moveToCell" when args.Length >= 1:
                    string cell = args[0];
                    string pad = args.Length > 1 && args[1].Length > 0 ? args[1] : "Spawn";
                    _world["strFrame"] = cell;
                    _world["strPad"] = pad;
                    if (MyLeaf is JsonObject leaf)
                    {
                        leaf["strFrame"] = cell;
                        leaf["strPad"] = pad;
                    }
                    break;

                case "addLoadout":
                    ApplyLoadoutOutbound(cmd, args);
                    break;

                case "gar" when args.Length >= 2:
                    DateTimeOffset now = _time.GetUtcNow();
                    foreach (string target in args[1].Split(','))
                    {
                        int gt = target.IndexOf('>');
                        _skillLastSent[gt > 0 ? target[..gt] : target] = now;
                    }
                    break;

                default:
                    return;
            }
            _version++;
        }
    }

    /// <summary>Distinct command names counted per table; the server names them, so beyond this they share one bucket.</summary>
    public const int MaxCounterKeys = 512;

    /// <summary>The bucket for command names past <see cref="MaxCounterKeys"/>.</summary>
    public const string OverflowCounterKey = "(other)";

    private static string BoundedKey(Dictionary<string, long> table, string key)
    {
        return table.ContainsKey(key) || table.Count < MaxCounterKeys - 1 ? key : OverflowCounterKey;
    }

    private static string KeyOf(InboundPacket packet)
    {
        string kind = packet.Kind switch
        {
            PacketKind.Xt => "xt",
            PacketKind.Json => "json",
            PacketKind.Xml => "xml",
            _ => packet.DecompressionFailed ? "undecodable" : "unknown",
        };
        return packet.Command is null ? kind : $"{kind}:{packet.Command}";
    }

    private static string Truncate(string text)
    {
        return text.Length > MaxSampleLength ? text[..MaxSampleLength] : text;
    }

    // ---------------------------------------------------------------- XML (SmartFox system messages)
    private bool ApplyXml(InboundPacket p)
    {
        string? action = p.Command;
        if (action is null)
            return false;
        if (s_connectionXml.Contains(action))
            return true;
        switch (action)
        {
            case "joinOK":
                // SmartFox's own room. AQW's area id (curRoom) comes from moveToArea; kept apart on purpose.
                return true;

            case "uER":
                if (p.XmlUserId is not int uid || p.XmlUserName is not string name)
                    return false;
                AddAreaUser(uid, name);
                return true;

            case "userGone":
                if (p.XmlUserId is not int goneId)
                    return false;
                RemoveAreaUser(goneId, null);
                return true;

            default:
                return false;
        }
    }

    // ---------------------------------------------------------------- %xt% strings
    private bool ApplyXt(InboundPacket p)
    {
        IReadOnlyList<string> a = p.Args;
        string cmd = p.Command!;
        if (s_messageXt.Contains(cmd))
        {
            RecordMessage(cmd, a);
            return true;
        }

        switch (cmd)
        {
            case "loginResponse":
                if (a.Count >= 3 && a[0] == "true" && int.TryParse(a[1], out int uid))
                    SetIdentityCore(uid, a[2]);
                return true;

            case "uotls" when a.Count >= 2:
                UserTreeWrite(a[0], ParsePairs(a[1]));
                return true;

            case "mtls" when a.Count >= 2 && int.TryParse(a[0], out int monId):
                MonsterTreeWrite(monId, ParsePairs(a[1]));
                return true;

            case "respawnMon" when a.Count >= 1:
                foreach (string part in a[0].Split(','))
                {
                    if (!int.TryParse(part, out _) || MonTree[part] is not JsonObject leaf)
                        continue;
                    leaf["intState"] = 1;
                    if (leaf["intHPMax"] is JsonNode max)
                        leaf["intHP"] = max.DeepClone();
                    if (leaf["intMPMax"] is JsonNode mpMax)
                        leaf["intMP"] = mpMax.DeepClone();
                    leaf["auras"] = new JsonArray();
                }
                return true;

            case "exitArea" when a.Count >= 1:
                RemoveAreaUser(int.TryParse(a[0], out int goneUid) ? goneUid : null, a.Count > 1 ? a[1] : null);
                return true;

            case "spcs" when a.Count >= 2:
                // Game.as spcs: the monster in a slot becomes another (boss phases). New HP comes with mtls.
                if (MonTree[a[0]] is JsonObject slot && int.TryParse(a[1], out int newMonId))
                {
                    slot["MonID"] = newMonId;
                    slot["auras"] = new JsonArray();
                }
                return true;

            case "gtc" when a.Count >= 2:
                _pendingServerMove = new ServerMoveRequest(cmd, a[0], a[1], _time.GetUtcNow());
                return true;

            case "resTimed":
                _pendingServerMove = new ServerMoveRequest(cmd, a.Count > 0 ? a[0] : string.Empty, a.Count > 1 ? a[1] : string.Empty, _time.GetUtcNow());
                return true;

            case "dungeonMTC" when a.Count >= 2:
                if (a[0] == "true")
                {
                    string[] cp = a[1].Split(',');
                    _pendingServerMove = new ServerMoveRequest(cmd, cp[0], cp.Length > 1 ? cp[1] : "Spawn", _time.GetUtcNow());
                }
                return true;

            default:
                return false;
        }
    }

    private void RecordMessage(string cmd, IReadOnlyList<string> a)
    {
        string text;
        string? sender = null;
        switch (cmd)
        {
            case "chatm":
                text = a.Count > 0 ? a[0] : string.Empty;
                sender = a.Count > 1 ? a[1] : null;
                break;

            case "whisper":
                text = a.Count > 0 ? a[0] : string.Empty;
                sender = a.Count > 1 ? a[1] : null;
                break;

            default:
                text = string.Join(' ', a.Where(x => x.Length > 0));
                break;
        }
        RecordMessage(cmd, text, sender);
    }

    private void RecordMessage(string cmd, string text, string? sender)
    {
        _messages.Enqueue(new ServerMessageInfo(cmd, text, sender, _time.GetUtcNow()));
        while (_messages.Count > MaxMessages)
            _messages.Dequeue();
    }

    // "k:v,k:v" -> object, typed the way Game.userTreeWrite / monsterTreeWrite type them.
    internal static JsonObject ParsePairs(string csv)
    {
        JsonObject o = new();
        foreach (string pair in csv.Split(','))
        {
            int i = pair.IndexOf(':');
            if (i <= 0)
                continue;
            string key = pair[..i];
            o[key] = TypedValue(key, pair[(i + 1)..]);
        }
        return o;
    }

    private static JsonNode? TypedValue(string key, string value)
    {
        if (key.Equals("afk", StringComparison.OrdinalIgnoreCase))
            return value.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (IsIntKey(key))
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (long)Math.Truncate(d) : 0;
        return value;
    }

    private static bool IsIntKey(string key)
    {
        return key.Contains("int", StringComparison.OrdinalIgnoreCase)
            || key is "tx" or "ty" or "sp" or "MonID" or "MonMapID" or "entID" or "iLvl";
    }

    // Numeric strings in int-ish fields become numbers so path reads match what the AS3 client holds.
    private static JsonObject NormalizeLeaf(JsonObject source)
    {
        JsonObject leaf = new();
        foreach (KeyValuePair<string, JsonNode?> pair in source)
        {
            if (pair.Value is JsonValue v && IsIntKey(pair.Key) && v.GetValueKind() == System.Text.Json.JsonValueKind.String)
                leaf[pair.Key] = TypedValue(pair.Key, v.GetValue<string>());
            else
                leaf[pair.Key] = pair.Value?.DeepClone();
        }
        return leaf;
    }

    // Game.userTreeWrite
    private void UserTreeWrite(string name, JsonObject? values)
    {
        if (values is null || string.IsNullOrEmpty(name))
            return;
        string key = name.ToLowerInvariant();
        JsonObject leaf = LeafOrCreate(key);
        foreach (KeyValuePair<string, JsonNode?> pair in values)
        {
            JsonNode? value = pair.Value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? TypedValue(pair.Key, v.GetValue<string>())
                : pair.Value?.DeepClone();
            leaf[pair.Key] = value;
        }

        if (values.ContainsKey("strFrame") && !ContainsName(AreaUsers, key))
            AreaUsers.Add(key);

        if (key == _me)
        {
            if (values["strFrame"] is JsonNode frame)
                _world["strFrame"] = frame.DeepClone();
            if (values["strPad"] is JsonNode pad)
                _world["strPad"] = pad.DeepClone();
            if (leaf["intLevel"] is JsonNode level && values.ContainsKey("intLevel"))
                ObjData["intLevel"] = level.DeepClone();
        }
    }

    // Game.monsterTreeWrite
    private void MonsterTreeWrite(int monMapId, JsonObject? values)
    {
        if (values is null)
            return;
        if (MonTree[monMapId.ToString(CultureInfo.InvariantCulture)] is not JsonObject leaf)
            return;
        foreach (KeyValuePair<string, JsonNode?> pair in values)
        {
            leaf[pair.Key] = pair.Value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? TypedValue(pair.Key, v.GetValue<string>())
                : pair.Value?.DeepClone();
        }
        bool dead = values.ContainsKey("intHP") && JsonUtil.AsInt(leaf["intHP"]) <= 0;
        if (dead || (values.ContainsKey("intState") && JsonUtil.AsInt(leaf["intState"]) == 0))
            leaf["auras"] = new JsonArray();
    }

    private void AddAreaUser(int uid, string name)
    {
        string key = name.ToLowerInvariant();
        JsonObject leaf = LeafOrCreate(key);
        leaf["entID"] = uid;
        _uidToName[uid] = key;
        if (!ContainsName(AreaUsers, key))
            AreaUsers.Add(key);
    }

    private void RemoveAreaUser(int? uid, string? name)
    {
        string? key = name?.ToLowerInvariant();
        if (key is null && uid is int id && _uidToName.TryGetValue(id, out string? known))
            key = known;
        if (key is null || key == _me)
            return;
        UoTree.Remove(key);
        for (int i = AreaUsers.Count - 1; i >= 0; i--)
        {
            if (string.Equals(JsonUtil.AsString(AreaUsers[i]), key, StringComparison.OrdinalIgnoreCase))
                AreaUsers.RemoveAt(i);
        }
        if (uid is int u)
        {
            _uidToName.Remove(u);
            Avatars.Remove(u.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static bool ContainsName(JsonArray array, string name)
    {
        foreach (JsonNode? node in array)
        {
            if (string.Equals(JsonUtil.AsString(node), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- JSON extension messages
    private bool ApplyJson(string cmd, JsonObject d)
    {
        switch (cmd)
        {
            case "moveToArea":
                OnMoveToArea(d);
                return true;

            case "initUserData":
                ApplyInitUserData(d);
                return true;

            case "initUserDatas":
                if (d["a"] is JsonArray all)
                {
                    foreach (JsonNode? node in all)
                    {
                        if (node is JsonObject entry)
                            ApplyInitUserData(entry);
                    }
                }
                return true;

            case "uotls":
                UserTreeWrite(JsonUtil.AsString(d["unm"]) ?? string.Empty, d["o"] as JsonObject);
                return true;

            case "mtls":
                MonsterTreeWrite(JsonUtil.AsInt(d["id"]), d["o"] as JsonObject);
                return true;

            case "ct":
            case "cb":
                OnCombat(d);
                return true;

            case "sar":
            case "sars":
                RecordActionResult(d);
                return true;

            case "aura+":
            case "aura++":
            case "aura*":
            case "aura+p":
            case "aura-":
            case "aura--":
            case "aura=":
                ApplyAuraEvent(cmd, d);
                return true;

            case "clearAuras":
                if (MyLeaf is JsonObject me && me["auras"] is JsonArray auras)
                    me["auras"] = new JsonArray(auras.Where(x => x is JsonObject o && JsonUtil.AsBool(o["persist"])).Select(x => x!.DeepClone()).ToArray());
                return true;

            case "loadInventoryBig":
                MyAvatar["items"] = CloneItemArray(d["items"]);
                MyAvatar["houseitems"] = CloneItemArray(d["hitems"]);
                MyAvatar["factions"] = d["factions"] is JsonArray f ? (JsonArray)f.DeepClone() : new JsonArray();
                MyAvatar["iBankCount"] = JsonUtil.AsInt(d["bankCount"]);
                if (d["guild"] is JsonNode guild)
                    ObjData["guild"] = guild.DeepClone();
                _inventoryLoaded = true;
                return true;

            case "initInventory":
                MyAvatar["items"] = CloneItemArray(d["items"]);
                return true;

            case "loadHouseInventory":
                MyAvatar["houseitems"] = CloneItemArray(d["items"]);
                return true;

            case "loadBank":
                OnLoadBank(d);
                return true;

            case "bankFromInv":
                if (d["bSuccess"] is null || JsonUtil.AsBool(d["bSuccess"]))
                    MoveInventoryToBank(JsonUtil.AsInt(d["ItemID"]));
                return true;

            case "bankToInv":
                if (d["bSuccess"] is null || JsonUtil.AsBool(d["bSuccess"]))
                    MoveBankToInventory(JsonUtil.AsInt(d["ItemID"]));
                return true;

            case "bankSwapInv":
                MoveInventoryToBank(JsonUtil.AsInt(d["invItemID"]));
                MoveBankToInventory(JsonUtil.AsInt(d["bankItemID"]));
                return true;

            case "addItems":
                OnAddItems(d);
                return true;

            case "dropItem":
                OnDropItem(d);
                return true;

            case "getDrop":
                OnGetDrop(d);
                return true;

            case "turnIn":
                OnTurnIn(d);
                return true;

            case "removeItem":
                RemoveItem(d);
                return true;

            case "buyItem":
                OnBuyItem(d);
                return true;

            case "sellItem":
                OnSellItem(d);
                return true;

            case "equipItem":
            case "unequipItem":
                OnEquip(d, cmd == "equipItem");
                return true;

            case "getQuests":
            case "getQuests2":
                if (d["quests"] is JsonObject quests)
                {
                    foreach (KeyValuePair<string, JsonNode?> pair in quests)
                    {
                        if (pair.Value is not JsonObject q)
                            continue;
                        JsonObject copy = (JsonObject)q.DeepClone();
                        if (QuestTree[pair.Key] is JsonObject existing && existing["status"] is JsonNode status)
                            copy["status"] = status.DeepClone();
                        else if (int.TryParse(pair.Key, out int qid) && _acceptedQuests.Contains(qid))
                            copy["status"] = "p";
                        QuestTree[pair.Key] = copy;
                    }
                }
                return true;

            case "acceptQuest":
                OnAcceptQuest(d);
                return true;

            case "ccqr":
                OnQuestComplete(d);
                return true;

            case "updateQuest":
                _questValueUpdates[JsonUtil.AsInt(d["iIndex"])] = JsonUtil.AsInt(d["iValue"]);
                return true;

            case "addGoldExp":
                AddTo(ObjData, "intGold", d["intGold"], onlyPositive: true);
                AddTo(ObjData, "intExp", d["intExp"], onlyPositive: true);
                if (d["iCP"] is JsonNode cp)
                    AddTo(ObjData, "iCP", cp, onlyPositive: false);
                if (d["FactionID"] is JsonNode factionId && d["iRep"] is JsonNode rep)
                {
                    JsonObject? faction = FindById(Factions, "FactionID", JsonUtil.AsInt(factionId));
                    if (faction is not null)
                        AddTo(faction, "iRep", rep, onlyPositive: false);
                }
                return true;

            case "levelUp":
                CopyIfPresent(d, ObjData, "intLevel");
                CopyIfPresent(d, ObjData, "intExpToLevel");
                if (d["xp"] is JsonNode xp)
                    ObjData["intExp"] = xp.DeepClone();
                return true;

            case "balance":
                CopyIfPresent(d, ObjData, "intGold");
                CopyIfPresent(d, ObjData, "intCoins");
                CopyIfPresent(d, ObjData, "intExp");
                return true;

            case "stu":
                if (_me is not null)
                {
                    JsonObject leaf = LeafOrCreate(_me);
                    if (leaf["sta"] is not JsonObject sta)
                        leaf["sta"] = sta = new JsonObject();
                    JsonUtil.Merge(sta, d["sta"] as JsonObject);
                    CopyIfPresent(d, leaf, "wDPS");
                    CopyIfPresent(d, leaf, "mDPS");
                    CopyIfPresent(d, leaf, "tempSta");
                }
                return true;

            case "sAct":
                // Seam: the client keeps the potion slot (i1) across class changes; replaced wholesale here.
                if (d["actions"] is JsonObject actions)
                {
                    Actions["active"] = actions["active"] is JsonArray active ? (JsonArray)active.DeepClone() : new JsonArray();
                    Actions["passive"] = actions["passive"] is JsonArray passive ? (JsonArray)passive.DeepClone() : new JsonArray();
                }
                return true;

            case "updateClass":
                if (d["uid"] is JsonNode classUid && JsonUtil.AsInt(classUid) != _myUid)
                    return true;
                if (d["sClassName"] is JsonNode className)
                    ObjData["strClassName"] = className.DeepClone();
                CopyIfPresent(d, ObjData, "iCP");
                return true;

            case "loadShop":
                _world["shopinfo"] = d["shopinfo"] is JsonObject shop ? shop.DeepClone() : null;
                return true;

            case "loadFactions":
                MyAvatar["factions"] = d["factions"] is JsonArray factions ? (JsonArray)factions.DeepClone() : new JsonArray();
                return true;

            case "addFaction":
                if (d["faction"] is JsonObject added)
                    Factions.Add(added.DeepClone());
                return true;

            case "event":
                if (d["args"] is JsonObject eventArgs && eventArgs.ContainsKey("zoneSet"))
                    _zone = JsonUtil.AsString(eventArgs["zoneSet"]);
                return true;

            case "playerDeath":
                RecordActionResult(d);
                return true;

            case "multiLoginWarning":
            case "logoutWarning":
                RecordMessage(cmd, JsonUtil.AsString(d["msg"]) ?? cmd, null);
                return true;

            default:
                return ApplyLoadoutJson(cmd, d);
        }
    }

    private void OnMoveToArea(JsonObject d)
    {
        _lastArea = (JsonObject)d.DeepClone();
        int areaId = JsonUtil.AsInt(d["areaId"], JsonUtil.AsInt(_world["curRoom"], 1));
        string areaName = JsonUtil.AsString(d["areaName"]) ?? string.Empty;
        _world["curRoom"] = areaId;
        _world["areaId"] = areaId;
        _world["strAreaName"] = areaName;
        _world["strMapName"] = JsonUtil.AsString(d["strMapName"]) ?? areaName.Split('-')[0];
        _world["strMapFileName"] = JsonUtil.AsString(d["strMapFileName"]) ?? string.Empty;
        CopyIfPresent(d, _world, "intType");
        _world["intKillCount"] = JsonUtil.AsInt(d["intKillCount"]);

        // Players: a fresh tree, keeping only this character's own leaf (its auras outlive the map).
        JsonObject? mine = MyLeaf is JsonObject m ? (JsonObject)m.DeepClone() : null;
        if (mine is not null)
        {
            // A new map starts at its entrance unless the server places us elsewhere (uoBranch below).
            mine.Remove("strFrame");
            mine.Remove("strPad");
        }
        _world["strFrame"] = "Enter";
        _world["strPad"] = "Spawn";
        JsonObject uoTree = new();
        JsonArray areaUsers = new();
        Dictionary<int, string> uids = new();
        if (_me is not null && mine is not null)
            uoTree[_me] = mine;
        if (d["uoBranch"] is JsonArray branch)
        {
            foreach (JsonNode? node in branch)
            {
                if (node is not JsonObject entry)
                    continue;
                string name = (JsonUtil.AsString(entry["uoName"]) ?? JsonUtil.AsString(entry["strUsername"]) ?? string.Empty).ToLowerInvariant();
                if (name.Length == 0)
                    continue;
                JsonObject leaf = NormalizeLeaf(entry);
                if (uoTree[name] is JsonObject previous && previous["auras"] is JsonArray keep)
                    leaf["auras"] = keep.DeepClone();
                else
                    leaf["auras"] ??= new JsonArray();
                if (name == _me && uoTree[name] is JsonObject old && old["sta"] is JsonNode sta)
                    leaf["sta"] = sta.DeepClone();
                uoTree[name] = leaf;
                if (!ContainsName(areaUsers, name))
                    areaUsers.Add(name);
                if (JsonUtil.AsIntOrNull(entry["entID"]) is int entId)
                    uids[entId] = name;
            }
        }
        _world["uoTree"] = uoTree;
        _world["areaUsers"] = areaUsers;
        if (_me is not null)
            uids[_myUid] = _me;
        _uidToName.Clear();
        foreach (KeyValuePair<int, string> pair in uids)
            _uidToName[pair.Key] = pair.Value;
        foreach (string avatarKey in Avatars.Select(p => p.Key).ToList())
        {
            if (!int.TryParse(avatarKey, out int avatarUid) || !_uidToName.ContainsKey(avatarUid))
                Avatars.Remove(avatarKey);
        }

        if (MyLeaf is JsonObject me)
        {
            if (me["strFrame"] is JsonNode frame)
                _world["strFrame"] = frame.DeepClone();
            if (me["strPad"] is JsonNode pad)
                _world["strPad"] = pad.DeepClone();
        }

        // Monsters: definitions, placements, live leaves.
        _world["mondef"] = d["mondef"] is JsonArray mondef ? (JsonArray)mondef.DeepClone() : new JsonArray();
        _world["monmap"] = d["monmap"] is JsonArray monmap ? (JsonArray)monmap.DeepClone() : new JsonArray();
        JsonObject monTree = new();
        if (d["monBranch"] is JsonArray monBranch)
        {
            foreach (JsonNode? node in monBranch)
            {
                if (node is not JsonObject entry)
                    continue;
                JsonObject leaf = NormalizeLeaf(entry);
                leaf["auras"] = new JsonArray();
                monTree[JsonUtil.AsInt(entry["MonMapID"]).ToString(CultureInfo.InvariantCulture)] = leaf;
            }
        }
        foreach (JsonNode? node in MonMap)
        {
            if (node is JsonObject placement && monTree[JsonUtil.AsInt(placement["MonMapID"]).ToString(CultureInfo.InvariantCulture)] is JsonObject leaf)
                leaf["strFrame"] = JsonUtil.AsString(placement["strFrame"]);
        }
        _world["monTree"] = monTree;
        MyAvatar["target"] = null;
        _pendingServerMove = null;
    }

    private void ApplyInitUserData(JsonObject d)
    {
        JsonObject data = d["data"] as JsonObject ?? d;
        int? uid = JsonUtil.AsIntOrNull(d["uid"]) ?? JsonUtil.AsIntOrNull(data["uid"]);
        string? name = JsonUtil.AsString(data["strUsername"]);
        bool mine = (uid is not null && uid == _myUid && _myUid != 0)
            || (name is not null && _me is not null && name.Equals(_me, StringComparison.OrdinalIgnoreCase));
        if (mine)
        {
            // Game.applyInitUserData: merged into objData. Its intHP is always 0 and is not a live value: the leaf
            // (uoTree) is not touched.
            JsonUtil.Merge(ObjData, data);
            if (ObjData["eqp"] is null)
                ObjData["eqp"] = new JsonObject();
            _characterLoaded = true;
            return;
        }
        if (uid is null)
            return;
        string key = uid.Value.ToString(CultureInfo.InvariantCulture);
        Avatars[key] = new JsonObject { ["uid"] = uid.Value, ["objData"] = data.DeepClone() };
        if (name is not null)
        {
            _uidToName.TryAdd(uid.Value, name.ToLowerInvariant());
            if (UoTree[name.ToLowerInvariant()] is JsonObject leaf)
                leaf["entID"] ??= uid.Value;
        }
    }

    private void OnCombat(JsonObject d)
    {
        if (d["m"] is JsonObject monsters)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in monsters)
            {
                if (int.TryParse(pair.Key, out int id))
                    MonsterTreeWrite(id, pair.Value as JsonObject);
            }
        }
        if (d["p"] is JsonObject players)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in players)
                UserTreeWrite(pair.Key, pair.Value as JsonObject);
        }
        if (d["a"] is JsonArray auraEvents)
        {
            foreach (JsonNode? node in auraEvents)
            {
                if (node is JsonObject ev && JsonUtil.AsString(ev["cmd"]) is string auraCmd && auraCmd.StartsWith("aura", StringComparison.Ordinal))
                    ApplyAuraEvent(auraCmd, ev);
            }
        }
        foreach (string list in new[] { "sara", "sarsa" })
        {
            if (d[list] is JsonArray results)
            {
                foreach (JsonNode? node in results)
                {
                    if (node is JsonObject result)
                        RecordActionResult(result);
                }
            }
        }
    }

    private void RecordActionResult(JsonObject result)
    {
        _actionResults.Enqueue((JsonObject)result.DeepClone());
        while (_actionResults.Count > MaxActionResults)
            _actionResults.Dequeue();
    }

    // World.handleAuraEvent (the data half).
    private void ApplyAuraEvent(string cmd, JsonObject d)
    {
        string? tInf = JsonUtil.AsString(d["tInf"]);
        if (tInf is null)
            return;
        JsonObject? leaf = LeafOfSubject(tInf);
        if (leaf is null)
            return;
        string kind = JsonUtil.AsString(d["cmd"]) ?? cmd;
        string? caster = JsonUtil.AsString(d["cInf"]);
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        JsonArray auras = leaf["auras"] as JsonArray ?? (JsonArray)(leaf["auras"] = new JsonArray());
        JsonArray passives = leaf["passives"] as JsonArray ?? (JsonArray)(leaf["passives"] = new JsonArray());

        List<JsonObject> list = new();
        if (d["auras"] is JsonArray many)
            list.AddRange(many.OfType<JsonObject>());
        if (d["aura"] is JsonObject one)
            list.Add(one);

        foreach (JsonObject a in list)
        {
            string? nam = JsonUtil.AsString(a["nam"]);
            if (nam is null)
                continue;
            switch (kind)
            {
                case "aura+":
                case "aura++":
                case "aura*":
                    JsonObject? existing = FindAura(auras, nam, caster);
                    if (existing is null || kind == "aura*" || JsonUtil.AsBool(a["isNew"]))
                    {
                        JsonObject added = (JsonObject)a.DeepClone();
                        added["ts"] = now;
                        added["cInf"] = caster;
                        if (existing is not null && kind != "aura*")
                            auras.Remove(existing);
                        auras.Add(added);
                    }
                    else
                    {
                        JsonUtil.Merge(existing, a);
                        existing["ts"] = now;
                    }
                    break;

                case "aura+p":
                    JsonObject passive = (JsonObject)a.DeepClone();
                    passive["ts"] = now;
                    passive["cInf"] = caster;
                    passives.Add(passive);
                    break;

                case "aura=":
                    if (FindAura(auras, nam, null) is JsonObject stacked && a["stk"] is JsonNode stk)
                        stacked["stk"] = stk.DeepClone();
                    break;

                case "aura-":
                case "aura--":
                    if (FindAura(auras, nam, null) is JsonObject gone)
                        auras.Remove(gone);
                    if (FindAura(passives, nam, null) is JsonObject gonePassive)
                        passives.Remove(gonePassive);
                    break;
            }
        }
    }

    private static JsonObject? FindAura(JsonArray auras, string name, string? caster)
    {
        foreach (JsonNode? node in auras)
        {
            if (node is JsonObject a && JsonUtil.AsString(a["nam"]) == name && (caster is null || JsonUtil.AsString(a["cInf"]) is null || JsonUtil.AsString(a["cInf"]) == caster))
                return a;
        }
        return null;
    }

    // ---------------------------------------------------------------- items
    private static JsonArray CloneItemArray(JsonNode? node)
    {
        JsonArray result = new();
        if (node is not JsonArray array)
            return result;
        foreach (JsonNode? item in array)
        {
            if (item is JsonObject o)
            {
                JsonObject copy = (JsonObject)o.DeepClone();
                copy["iQty"] = JsonUtil.AsInt(o["iQty"]);
                result.Add(copy);
            }
        }
        return result;
    }

    private static JsonObject? FindById(JsonArray array, string field, long id)
    {
        foreach (JsonNode? node in array)
        {
            if (node is JsonObject o && JsonUtil.SameId(o[field], id))
                return o;
        }
        return null;
    }

    private static bool IsHouseItem(JsonObject item)
    {
        string? type = JsonUtil.AsString(item["sType"]);
        return JsonUtil.AsBool(item["bHouse"]) || type is "House" or "Floor Item" or "Wall Item";
    }

    // Adds a granted stack. When the server says what the stack is now (iQtyNow) that wins; otherwise iQty is added.
    private static void AddToStack(JsonArray bag, JsonObject grant)
    {
        int id = JsonUtil.AsInt(grant["ItemID"]);
        int delta = JsonUtil.AsInt(grant["iQty"], 1);
        int? now = JsonUtil.AsIntOrNull(grant["iQtyNow"]);
        JsonObject? have = FindById(bag, "ItemID", id);
        if (have is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in grant)
            {
                if (pair.Key is not "iQty" and not "iQtyNow")
                    have[pair.Key] = pair.Value?.DeepClone();
            }
            have["iQty"] = now ?? (JsonUtil.AsInt(have["iQty"]) + delta);
            return;
        }
        JsonObject added = (JsonObject)grant.DeepClone();
        added["iQty"] = now ?? delta;
        added.Remove("iQtyNow");
        bag.Add(added);
    }

    private static void TakeFromStack(JsonArray bag, JsonObject item, int qty, int? qtyNow)
    {
        int left = qtyNow ?? (JsonUtil.AsInt(item["iQty"]) - qty);
        if (left < 1)
            bag.Remove(item);
        else
            item["iQty"] = left;
    }

    private void OnAddItems(JsonObject d)
    {
        if (d["items"] is not JsonObject items)
            return;
        foreach (KeyValuePair<string, JsonNode?> pair in items)
        {
            if (pair.Value is not JsonObject grant || !int.TryParse(pair.Key, out int id))
                continue;
            JsonObject item = (JsonObject)grant.DeepClone();
            item["ItemID"] ??= id;
            bool bank = JsonUtil.AsBool(item["bBank"]);
            // Game.as: Number(bTemp) == 0 goes to the backpack, anything else to the temporary inventory. With no
            // bTemp at all a backpack stack update carries a CharItemID and a temp item never does.
            bool temp = item.ContainsKey("bTemp") ? JsonUtil.AsBool(item["bTemp"]) : !item.ContainsKey("CharItemID");
            if (bank)
            {
                AddToStack(BankItems, item);
            }
            else if (temp)
            {
                AddToStack(TempItems, item);
            }
            else
            {
                // Fill in the item's details from a known copy when this is only a stack update.
                if (!item.ContainsKey("sName") && KnownItem(id) is JsonObject known)
                {
                    JsonObject merged = (JsonObject)known.DeepClone();
                    JsonUtil.Merge(merged, item);
                    item = merged;
                }
                AddToStack(IsHouseItem(item) ? HouseItems : Items, item);
            }
        }
    }

    private JsonObject? KnownItem(int itemId)
    {
        return FindById(Items, "ItemID", itemId)
            ?? FindById(BankItems, "ItemID", itemId)
            ?? FindById(TempItems, "ItemID", itemId)
            ?? (_drops.TryGetValue(itemId, out DropState? drop) ? drop.Raw : null);
    }

    private void OnDropItem(JsonObject d)
    {
        if (d["items"] is not JsonObject items)
            return;
        DateTimeOffset now = _time.GetUtcNow();
        foreach (KeyValuePair<string, JsonNode?> pair in items)
        {
            if (pair.Value is not JsonObject offered || !int.TryParse(pair.Key, out int id))
                continue;
            int qty = JsonUtil.AsInt(offered["iQty"], 1);
            JsonObject raw = (JsonObject)offered.DeepClone();
            raw["ItemID"] ??= id;
            if (_drops.TryGetValue(id, out DropState? drop))
            {
                drop.Raw = raw;
                drop.Quantity += qty;
                drop.TimesOffered++;
                drop.LastOffered = now;
            }
            else
            {
                _drops[id] = new DropState(raw, qty, now);
                _dropOrder.Add(id);
            }
        }
    }

    private void OnGetDrop(JsonObject d)
    {
        int id = JsonUtil.AsInt(d["ItemID"]);
        if (!JsonUtil.AsBool(d["bSuccess"]))
            return;
        JsonObject item = _drops.TryGetValue(id, out DropState? drop) ? (JsonObject)drop.Raw.DeepClone() : new JsonObject { ["ItemID"] = id };
        if (drop is not null)
        {
            _drops.Remove(id);
            _dropOrder.Remove(id);
        }
        CopyIfPresent(d, item, "CharItemID");
        item["iQty"] = JsonUtil.AsInt(d["iQty"], 1);
        if (d["iQtyNow"] is JsonNode qtyNow)
            item["iQtyNow"] = qtyNow.DeepClone();
        else
            item.Remove("iQtyNow");
        item["bTemp"] = 0;
        if (JsonUtil.AsBool(d["bBank"]))
            AddToStack(BankItems, item);
        else
            AddToStack(IsHouseItem(item) ? HouseItems : Items, item);
    }

    private void OnTurnIn(JsonObject d)
    {
        _lastTurnIn = (JsonObject)d.DeepClone();
        string? sItems = JsonUtil.AsString(d["sItems"]);
        if (string.IsNullOrEmpty(sItems))
            return;
        foreach (string part in sItems.Split(','))
        {
            string[] kv = part.Split(':');
            if (kv.Length < 2 || !int.TryParse(kv[0], out int id) || !int.TryParse(kv[1], out int qty))
                continue;
            if (FindById(TempItems, "ItemID", id) is JsonObject temp)
                TakeFromStack(TempItems, temp, qty, null);
            else if (FindById(Items, "ItemID", id) is JsonObject inv)
                TakeFromStack(Items, inv, qty, null);
            else if (FindById(HouseItems, "ItemID", id) is JsonObject house)
                TakeFromStack(HouseItems, house, qty, null);
        }
    }

    private void RemoveItem(JsonObject d)
    {
        int charItemId = JsonUtil.AsInt(d["CharItemID"]);
        int qty = JsonUtil.AsInt(d["iQty"], 1);
        int? qtyNow = JsonUtil.AsIntOrNull(d["iQtyNow"]);
        JsonArray[] bags = JsonUtil.AsBool(d["bBank"]) ? new[] { BankItems } : new[] { Items, HouseItems };
        foreach (JsonArray bag in bags)
        {
            if (FindById(bag, "CharItemID", charItemId) is JsonObject item)
            {
                TakeFromStack(bag, item, qty, qtyNow);
                return;
            }
        }
    }

    private void OnBuyItem(JsonObject d)
    {
        if (!JsonUtil.AsBool(d["bitSuccess"]))
            return;
        int id = JsonUtil.AsInt(d["ItemID"]);
        JsonObject item = new() { ["ItemID"] = id };
        if (_world["shopinfo"] is JsonObject shop && shop["items"] is JsonArray shopItems && FindById(shopItems, "ItemID", id) is JsonObject listed)
            item = (JsonObject)listed.DeepClone();
        CopyIfPresent(d, item, "CharItemID");
        item["iQty"] = JsonUtil.AsInt(d["iQty"], 1);
        item.Remove("iQtyNow");
        if (d["iQtyNow"] is JsonNode qtyNow)
            item["iQtyNow"] = qtyNow.DeepClone();
        // Seam: the price is not deducted here; the next balance/initUserData carries the real gold.
        if (JsonUtil.AsBool(d["bBank"]))
            AddToStack(BankItems, item);
        else
            AddToStack(IsHouseItem(item) ? HouseItems : Items, item);
    }

    private void OnSellItem(JsonObject d)
    {
        int charItemId = JsonUtil.AsInt(d["CharItemID"]);
        int qty = JsonUtil.AsInt(d["iQty"], 1);
        int? qtyNow = JsonUtil.AsIntOrNull(d["iQtyNow"]);
        foreach (JsonArray bag in new[] { Items, HouseItems })
        {
            if (FindById(bag, "CharItemID", charItemId) is JsonObject item)
            {
                TakeFromStack(bag, item, qty, qtyNow);
                break;
            }
        }
        // Guess, not confirmed against Game.as: the refund is credited to gold, or coins when bCoins is set.
        if (d["intAmount"] is JsonNode amount)
            AddTo(ObjData, JsonUtil.AsBool(d["bCoins"]) ? "intCoins" : "intGold", amount, onlyPositive: true);
    }

    private void OnEquip(JsonObject d, bool equip)
    {
        int? uid = JsonUtil.AsIntOrNull(d["uid"]);
        int itemId = JsonUtil.AsInt(d["ItemID"]);
        string? slot = JsonUtil.AsString(d["strES"]);
        bool mine = uid is null || uid == _myUid;
        JsonObject objData;
        if (mine)
        {
            objData = ObjData;
            JsonObject? item = FindById(Items, "ItemID", itemId);
            slot ??= JsonUtil.AsString(item?["sES"]);
            if (item is not null)
            {
                if (equip)
                {
                    foreach (JsonNode? node in Items)
                    {
                        if (node is JsonObject other && other != item && JsonUtil.AsString(other["sES"]) == slot && JsonUtil.AsBool(other["bEquip"]))
                            other["bEquip"] = 0;
                    }
                }
                item["bEquip"] = equip ? 1 : 0;
            }
        }
        else
        {
            string key = uid!.Value.ToString(CultureInfo.InvariantCulture);
            if (Avatars[key] is not JsonObject avatar)
                Avatars[key] = avatar = new JsonObject { ["uid"] = uid.Value, ["objData"] = new JsonObject() };
            objData = avatar["objData"] as JsonObject ?? (JsonObject)(avatar["objData"] = new JsonObject());
        }

        if (slot is null)
            return;
        if (objData["eqp"] is not JsonObject eqp)
            objData["eqp"] = eqp = new JsonObject();
        if (equip)
        {
            eqp[slot] = new JsonObject
            {
                ["ItemID"] = itemId,
                ["sFile"] = d["sFile"]?.DeepClone(),
                ["sLink"] = d["sLink"]?.DeepClone(),
                ["sType"] = d["sType"]?.DeepClone(),
                ["sMeta"] = d["sMeta"]?.DeepClone(),
            };
        }
        else
        {
            eqp.Remove(slot);
        }
    }

    // Game.as "loadBank": with bitSuccess the items go to BankController.addItemsToBank; without it the game shows
    // "Error loading bank items!" and nothing is loaded.
    private void OnLoadBank(JsonObject d)
    {
        if (d["bitSuccess"] is JsonNode ok && !JsonUtil.AsBool(ok))
            return;
        if (d["items"] is JsonArray items)
            AddItemsToBankCore(items);
        _bankLoaded = true;
    }

    // BankController.addItemsToBank: keyed by ItemID (a known item is replaced), classes skipped. Under the lock.
    private void AddItemsToBankCore(JsonArray items)
    {
        foreach (JsonNode? node in items)
        {
            if (node is not JsonObject item || InventoryRules.IsClass(item))
                continue;
            JsonObject copy = (JsonObject)item.DeepClone();
            copy["iQty"] = JsonUtil.AsInt(item["iQty"]);
            JsonObject? existing = FindById(BankItems, "ItemID", JsonUtil.AsInt(item["ItemID"]));
            if (existing is not null)
                BankItems[BankItems.IndexOf(existing)] = copy;
            else
                BankItems.Add(copy);
        }
    }

    private void MoveInventoryToBank(int itemId)
    {
        foreach (JsonArray bag in new[] { Items, HouseItems })
        {
            if (FindById(bag, "ItemID", itemId) is not JsonObject item)
                continue;
            bag.Remove(item);
            item["bEquip"] = 0;
            if (FindById(BankItems, "ItemID", itemId) is JsonObject existing)
                BankItems.Remove(existing);
            BankItems.Add(item);
            if (!JsonUtil.AsBool(item["bCoins"]))
                MyAvatar["iBankCount"] = JsonUtil.AsInt(MyAvatar["iBankCount"]) + 1;
            return;
        }
    }

    private void MoveBankToInventory(int itemId)
    {
        if (FindById(BankItems, "ItemID", itemId) is not JsonObject item)
            return;
        BankItems.Remove(item);
        (IsHouseItem(item) ? HouseItems : Items).Add(item);
        if (!JsonUtil.AsBool(item["bCoins"]))
            MyAvatar["iBankCount"] = Math.Max(0, JsonUtil.AsInt(MyAvatar["iBankCount"]) - 1);
    }

    // ---------------------------------------------------------------- quests
    private void OnAcceptQuest(JsonObject d)
    {
        int id = JsonUtil.AsInt(d["QuestID"]);
        string key = id.ToString(CultureInfo.InvariantCulture);
        bool refused = d["bSuccess"] is JsonNode success && !JsonUtil.AsBool(success);
        if (refused)
        {
            _acceptedQuests.Remove(id);
            if (QuestTree[key] is JsonObject q)
                q["status"] = null;
            return;
        }
        _acceptedQuests.Add(id);
        if (QuestTree[key] is JsonObject quest && quest["status"] is null)
            quest["status"] = "p";
    }

    private void OnQuestComplete(JsonObject d)
    {
        _lastTurnIn = (JsonObject)d.DeepClone();
        if (!JsonUtil.AsBool(d["bSuccess"]))
            return;
        int id = JsonUtil.AsInt(d["QuestID"]);
        _acceptedQuests.Remove(id);
        if (QuestTree[id.ToString(CultureInfo.InvariantCulture)] is JsonObject quest)
            quest["status"] = null;
        // Seam: reaccept (litePreference bReaccept) and gold/xp from rewardObj are client rules; gold/xp arrive via addGoldExp.
    }

    // ---------------------------------------------------------------- helpers
    private static void AddTo(JsonObject target, string key, JsonNode? delta, bool onlyPositive)
    {
        if (delta is null)
            return;
        long add = JsonUtil.AsLong(delta);
        if (onlyPositive && add <= 0)
            return;
        target[key] = JsonUtil.AsLong(target[key]) + add;
    }

    private static void CopyIfPresent(JsonObject source, JsonObject target, string key)
    {
        if (source[key] is JsonNode value)
            target[key] = value.DeepClone();
    }
}
