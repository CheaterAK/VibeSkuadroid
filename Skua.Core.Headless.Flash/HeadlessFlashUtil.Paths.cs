using Skua.Core.Headless.Client;
using Skua.Core.Headless.Maps;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Flash;

// The game's object graph as paths: Main._getObjectA over the world model, the client rules and the login screen.
public sealed partial class HeadlessFlashUtil
{
    /// <summary>The result of walking a path: a value (possibly JSON null), or undefined (the last property is missing).</summary>
    internal readonly record struct Lookup(bool Defined, JsonNode? Node)
    {
        public static Lookup Undefined => new(false, null);

        public static Lookup Null => new(true, null);

        public static Lookup Of(JsonNode? node) => new(true, node);

        public bool IsNullish => !Defined || Node is null;
    }

    /// <summary>Roots of the game object the bridge models (the display ones are stored, not drawn).</summary>
    private static readonly HashSet<string> s_displayRoots = new(StringComparer.Ordinal)
    {
        "ui", "stage", "userPreference", "mcExtSWF", "params", "uoPref", "chatF", "mcO", "MsgBox", "cDropsUI", "mixer",
    };

    /// <summary>Top-level fields of <c>world</c> answered from the model.</summary>
    private static readonly HashSet<string> s_modelWorldKeys = new(StringComparer.Ordinal)
    {
        "strMapName", "strAreaName", "strMapFileName", "strFrame", "strPad", "curRoom", "areaId", "intType", "intKillCount",
        "areaUsers", "uoTree", "monTree", "mondef", "monmap", "monsters", "questTree", "shopinfo", "bankinfo", "avatars", "myAvatar", "objInfo",
    };

    private static readonly HashSet<string> s_modelAvatarKeys = new(StringComparer.Ordinal)
    {
        "uid", "pnm", "objData", "items", "tempitems", "houseitems", "factions", "iBankCount", "dataLeaf",
    };

    private readonly object _treeGate = new();
    private readonly JsonObject _clientTree;
    private readonly JsonObject _pMC = new() { ["pname"] = new JsonObject { ["ti"] = new JsonObject { ["text"] = string.Empty, ["textColor"] = 0xFFFFFF }, ["tg"] = new JsonObject { ["text"] = string.Empty, ["textColor"] = 0xFFFFFF } } };
    private readonly JsonObject _worldExtra = new() { ["visible"] = true };
    private readonly HashSet<string> _modules = new(StringComparer.Ordinal);
    private string? _areaTitleOverride;

    private static JsonObject BuildClientTree()
    {
        return new JsonObject
        {
            ["ui"] = new JsonObject
            {
                ["mcFPS"] = new JsonObject { ["visible"] = false },
                ["mcPortrait"] = new JsonObject { ["strName"] = new JsonObject { ["text"] = string.Empty } },
                ["mcPopup"] = new JsonObject(),
                ["mcInterface"] = new JsonObject { ["areaList"] = new JsonObject { ["title"] = new JsonObject { ["t1"] = new JsonObject() } } },
                ["dropStack"] = new JsonObject { ["numChildren"] = 0 },
                ["monsterIcon"] = new JsonObject { ["redX"] = new JsonObject { ["visible"] = false } },
            },
            ["stage"] = new JsonObject { ["frameRate"] = 30, ["quality"] = "HIGH" },
            ["userPreference"] = new JsonObject { ["data"] = new JsonObject { ["bDeathAd"] = true } },
            ["mcExtSWF"] = new JsonObject { ["numChildren"] = 0 },
            ["params"] = new JsonObject { ["sBG"] = string.Empty },
            ["uoPref"] = new JsonObject { ["bParty"] = true, ["bDuel"] = true, ["bGoto"] = true, ["bFriend"] = true, ["bWhisper"] = true, ["bTT"] = true, ["bPet"] = true },
        };
    }

    private static string[] Split(string path) => path.Split('.');

    // ---------------------------------------------------------------- reads

    /// <summary>Main._getObjectS(game, path).</summary>
    internal Lookup Resolve(string path, bool recordGaps = true)
    {
        if (string.IsNullOrEmpty(path))
            return Lookup.Undefined;
        string[] parts = Split(path);
        return parts[0] switch
        {
            "world" => Game.IsInGame ? ResolveWorld(parts, 1, recordGaps, path) : Walk(Lookup.Null, parts, 1),
            "sfc" => Walk(Lookup.Of(SfcNode()), parts, 1),
            "litePreference" => ResolveLite(parts),
            "mcLogin" => Walk(Lookup.Of(LoginScreenNode()), parts, 1),
            "mcConnDetail" => Walk(Lookup.Of(ConnDetailNode()), parts, 1),
            "serialCmd" => Walk(Lookup.Of(new JsonObject { ["servers"] = Game.ServerListJson() }), parts, 1),
            "serialCmdMode" or "connectingServer" => Walk(Lookup.Of(JsonValue.Create(false)), parts, 1),
            _ when s_displayRoots.Contains(parts[0]) => ResolveDisplay(parts, recordGaps, path),
            _ => Unmodelled(path, recordGaps),
        };
    }

    private Lookup Unmodelled(string path, bool record)
    {
        if (record)
            Gaps.Record(FlashGapKind.Missing, "path:" + path, null, "object path not modelled; answered undefined");
        return Lookup.Undefined;
    }

    /// <summary>obj[part] for each part, as AS3: a null/undefined intermediate gives null; a missing last property, undefined.</summary>
    internal static Lookup Walk(Lookup start, IReadOnlyList<string> parts, int from)
    {
        Lookup current = start;
        for (int i = from; i < parts.Count; i++)
        {
            if (current.IsNullish)
                return Lookup.Null;
            string part = parts[i];
            current = Child(current.Node!, part);
        }
        return current;
    }

    private static Lookup Child(JsonNode node, string part)
    {
        switch (node)
        {
            case JsonObject o:
                return o.TryGetPropertyValue(part, out JsonNode? v) ? Lookup.Of(v) : Lookup.Undefined;

            case JsonArray a:
                if (part == "length")
                    return Lookup.Of(a.Count);
                return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < a.Count ? Lookup.Of(a[index]) : Lookup.Undefined;

            case JsonValue sv when part == "length" && sv.TryGetValue(out string? s):
                return Lookup.Of(s.Length);

            default:
                return Lookup.Undefined;
        }
    }

    private JsonObject SfcNode()
    {
        return new JsonObject
        {
            ["isConnected"] = Game.IsConnected,
            ["myUserId"] = Game.World.MyUserId,
            ["myUserName"] = Game.World.MyUsername,
        };
    }

    private Lookup ResolveLite(string[] parts)
    {
        lock (Game.Client.LitePreferences)
            return Walk(Lookup.Of(new JsonObject { ["data"] = Game.Client.LitePreferences.DeepClone() }), parts, 1);
    }

    private JsonObject LoginScreenNode()
    {
        int servers = Game.Servers.Count;
        bool kicked = Game.Kicked;
        string? modal = Game.ModalMessage;
        return new JsonObject
        {
            ["visible"] = Game.LoginScreenVisible,
            ["sl"] = Game.State is HeadlessGameState.ServerList or HeadlessGameState.Connecting || (Game.LoginScreenVisible && servers > 0)
                ? new JsonObject { ["iList"] = new JsonObject { ["numChildren"] = servers } }
                : new JsonObject { ["iList"] = null },
            ["warning"] = new JsonObject { ["visible"] = kicked, ["strMessage"] = new JsonObject { ["text"] = kicked ? modal ?? string.Empty : string.Empty } },
            ["ModalStack"] = new JsonObject { ["numChildren"] = modal is null ? 0 : 1 },
        };
    }

    private JsonObject ConnDetailNode()
    {
        string? detail = Game.ConnectionDetail;
        return new JsonObject
        {
            ["stage"] = detail is null ? null : new JsonObject(),
            ["txtDetail"] = new JsonObject { ["text"] = detail ?? string.Empty },
        };
    }

    private Lookup ResolveDisplay(string[] parts, bool recordGaps, string path)
    {
        // Values the game derives: the bank popup's label and the area title.
        if (parts.Length >= 3 && parts[0] == "ui" && parts[1] == "mcPopup" && parts[2] == "currentLabel")
            return Walk(Lookup.Of(JsonValue.Create(Game.Client.PopupLabel)), parts, 3);
        if (path.StartsWith("ui.mcInterface.areaList.title.t1.text", StringComparison.Ordinal))
            return Walk(Lookup.Of(JsonValue.Create(_areaTitleOverride ?? $"Map: {Game.World.GetArea().AreaName}")), parts, 6);
        Lookup found;
        lock (_treeGate)
            found = Walk(Lookup.Of(_clientTree), parts, 0);
        if (found.Node is not null)
            return Lookup.Of(found.Node.DeepClone());
        // Not stored: the Flash display tree has more than the bridge keeps. Answer as AS3 would (null/undefined) and log it.
        if (recordGaps)
            Gaps.Record(FlashGapKind.Missing, "path:" + path, null, "display object not modelled; answered " + (found.Defined ? "null" : "undefined"));
        return found;
    }

    private Lookup ResolveWorld(string[] parts, int i, bool recordGaps, string path)
    {
        if (parts.Length == i)
            return Lookup.Of(new JsonObject());   // the World object exists (isNull "world"); it is never serialised whole
        string key = parts[i];
        switch (key)
        {
            case "myAvatar":
                return ResolveMyAvatar(parts, i + 1, recordGaps, path);
            case "actions":
                return Walk(Lookup.Of(Game.Client.ActionsJson()), parts, i + 1);
            case "lock":
                return Walk(Lookup.Of(Game.Client.Locks.ToJson()), parts, i + 1);
            case "map":
                return Walk(Lookup.Of(MapNode()), parts, i + 1);
            case "mapLoadInProgress":
                return Walk(Lookup.Of(JsonValue.Create(Game.Client.MapLoadInProgress)), parts, i + 1);
            case "WALKSPEED":
                return Walk(Lookup.Of(JsonValue.Create(Game.Client.WalkSpeed)), parts, i + 1);
            case "SCALE":
                return Walk(Lookup.Of(JsonValue.Create(Game.Client.Scale)), parts, i + 1);
            case "GCD":
                return Walk(Lookup.Of(JsonValue.Create(Rules.SkillRules.GlobalCooldown(JsonUtil.AsDoubleOrNull(Game.World.GetStats()?["$tha"]).GetValueOrDefault()))), parts, i + 1);
            case "spawnPoint":
                (string cell, string pad) = Game.Client.SpawnPoint;
                return Walk(Lookup.Of(new JsonObject { ["strFrame"] = cell, ["strPad"] = pad }), parts, i + 1);
            case "rootClass":
                return parts.Length == i + 1 ? Lookup.Of(new JsonObject()) : Resolve(string.Join('.', parts.Skip(i + 1)), recordGaps);
            case "questTree":
                return Walk(Lookup.Of(QuestTreeNode()), parts, i + 1);
            case "players":
                // Neither the game's World (Game4000r9.swf) nor skua.swf has a "players" member, so on Windows this is
                // undefined and Skua's party-health skill rule (AdvancedSkillCommand) finds no one. Answered the same.
                return Lookup.Undefined;
            case "visible":
                lock (_treeGate)
                    return Walk(Lookup.Of(_worldExtra[key]?.DeepClone()), parts, i + 1);
            case "partyMembers":
                return Walk(Lookup.Of(new JsonArray(Game.Client.Party.Members.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray())), parts, i + 1);
            case "partyOwner":
                return Walk(Lookup.Of(JsonValue.Create(Game.Client.Party.Owner)), parts, i + 1);
            case "partyID":
                return Walk(Lookup.Of(JsonValue.Create(Game.Client.Party.Id)), parts, i + 1);
            case "strUsername":
                Gaps.Record(FlashGapKind.Approximated, "path:world.strUsername", null, "answered with this character's name");
                return Walk(Lookup.Of(JsonValue.Create(Game.World.GetPlayer()?.Username)), parts, i + 1);
            case "invTree":
                Gaps.Record(FlashGapKind.Approximated, "path:world.invTree", null, "built from backpack, house, temporary items and offered drops");
                return Walk(Lookup.Of(InvTreeNode()), parts, i + 1);
            case "ldr_map" when parts.Length == i + 3 && parts[i + 1] == "contentLoaderInfo" && parts[i + 2] == "url":
                Gaps.Record(FlashGapKind.Approximated, "path:world.ldr_map.contentLoaderInfo.url", null, "the map file's game.aq.com URL");
                return Lookup.Of(JsonValue.Create(new Uri(Game.Session.Api.Options.BaseUri, "gamefiles/maps/" + Game.World.GetArea().MapFileName).ToString()));
        }
        if (!s_modelWorldKeys.Contains(key))
        {
            lock (_treeGate)
            {
                if (_worldExtra.ContainsKey(key))
                    return Walk(Lookup.Of(_worldExtra[key]?.DeepClone()), parts, i + 1);
            }
            return Unmodelled(path, recordGaps);
        }
        return ModelPath(parts);
    }

    private Lookup ResolveMyAvatar(string[] parts, int i, bool recordGaps, string path)
    {
        if (parts.Length == i)
        {
            // The Avatar object: present in game. Serialising it whole only happens for isNull checks in Skua.
            Game.World.TryGetGameObjectNode("world.myAvatar", out JsonNode? avatar);
            return Lookup.Of(DecorateAvatar(avatar as JsonObject ?? new JsonObject()));
        }
        string key = parts[i];
        switch (key)
        {
            case "target":
                return Walk(Lookup.Of(Game.Client.TargetJson()), parts, i + 1);
            case "pMC":
                JsonObject pmc;
                lock (_treeGate)
                    pmc = (JsonObject)_pMC.DeepClone();
                (int x, int y) = Game.Client.Position;
                pmc["x"] = x;
                pmc["y"] = y;
                return Walk(Lookup.Of(pmc), parts, i + 1);
            case "objData":
                Game.World.TryGetGameObjectNode("world.myAvatar.objData", out JsonNode? objData);
                return Walk(Lookup.Of(Game.Client.DecorateObjData(objData as JsonObject ?? new JsonObject())), parts, i + 1);
            case "items":
                Game.World.TryGetGameObjectNode("world.myAvatar.items", out JsonNode? items);
                Game.World.TryGetGameObjectNode("world.myAvatar.objData", out JsonNode? od);
                return Walk(Lookup.Of(GameClient.DecorateItems(items as JsonArray ?? new JsonArray(), od as JsonObject ?? new JsonObject())), parts, i + 1);
            case "factions":
                Game.World.TryGetGameObjectNode("world.myAvatar.factions", out JsonNode? factions);
                return Walk(Lookup.Of(GameClient.DecorateFactions(factions as JsonArray ?? new JsonArray())), parts, i + 1);
        }
        if (!s_modelAvatarKeys.Contains(key))
            return Unmodelled(path, recordGaps);
        return ModelPath(parts);
    }

    private JsonObject DecorateAvatar(JsonObject avatar)
    {
        if (avatar["objData"] is JsonObject od)
            Game.Client.DecorateObjData(od);
        if (avatar["items"] is JsonArray items && avatar["objData"] is JsonObject od2)
            GameClient.DecorateItems(items, od2);
        if (avatar["factions"] is JsonArray factions)
            GameClient.DecorateFactions(factions);
        avatar["target"] = Game.Client.TargetJson();
        return avatar;
    }

    private Lookup ModelPath(string[] parts)
    {
        // The model walks dotted paths; ask it for the longest prefix it has, then walk the rest as AS3 would.
        for (int n = parts.Length; n >= 2; n--)
        {
            string prefix = string.Join('.', parts.Take(n));
            if (Game.World.TryGetGameObjectNode(prefix, out JsonNode? node))
                return Walk(Lookup.Of(node), parts, n);
        }
        return Lookup.Undefined;
    }

    // World.invTree: item id -> the item as last seen (held ones with their quantity, offered drops at 0).
    private JsonObject InvTreeNode()
    {
        JsonObject tree = new();
        foreach (Skua.Core.Headless.World.DropInfo d in Game.World.GetDrops())
        {
            JsonObject o = (JsonObject)d.Raw.DeepClone();
            o["iQty"] = 0;
            tree[d.ItemId.ToString(CultureInfo.InvariantCulture)] = o;
        }
        Client.CharacterSnapshot c = Game.Client.Character();
        foreach (JsonObject it in c.Temp.Concat(c.House).Concat(c.Items))
            tree[JsonUtil.AsInt(it["ItemID"]).ToString(CultureInfo.InvariantCulture)] = it.DeepClone();
        return tree;
    }

    private JsonObject QuestTreeNode()
    {
        Game.World.TryGetGameObjectNode("world.questTree", out JsonNode? tree);
        return Game.Client.DecorateQuestTree(tree as JsonObject ?? new JsonObject());
    }

    private JsonObject MapNode()
    {
        MapCells? cells = Game.Client.Cells;
        JsonArray labels = new();
        if (cells is not null)
        {
            foreach (string c in cells.Cells)
                labels.Add(new JsonObject { ["name"] = c });
        }
        return new JsonObject
        {
            ["currentScene"] = new JsonObject { ["labels"] = labels, ["name"] = "Scene 1" },
            ["currentLabel"] = Game.World.GetArea().Cell,
            ["isPlaying"] = false,
        };
    }

    /// <summary>getGameObjectS: the Game class's statics.</summary>
    internal Lookup ResolveStatic(string path)
    {
        string[] parts = Split(path);
        JsonObject statics = new()
        {
            ["loginInfo"] = new JsonObject { ["strUsername"] = Game.Username, ["strPassword"] = Game.Password },
            ["objLogin"] = Game.LoginObject(),
            ["serverIP"] = Game.ServerIp,
        };
        if (!statics.ContainsKey(parts[0]))
        {
            Gaps.Record(FlashGapKind.Missing, "static:" + path, null, "Game static not modelled; answered undefined");
            return Lookup.Undefined;
        }
        return Walk(Lookup.Of(statics), parts, 0);
    }

    // ---------------------------------------------------------------- writes

    /// <summary>setGameObject: <c>obj[last] = value</c> on the object at the path's parent. False when there is no such object.</summary>
    internal bool Assign(string path, JsonNode? value)
    {
        string[] parts = Split(path);
        if (parts.Length < 2)
            return RecordWrite(path, "cannot replace a root object");
        switch (parts[0])
        {
            case "litePreference" when parts.Length >= 3 && parts[1] == "data":
                lock (Game.Client.LitePreferences)
                    return AssignInto(Game.Client.LitePreferences, parts, 2, value, path);

            case "world":
                return AssignWorld(parts, value, path);

            case "mcLogin":
                // The login screen is the game's; skua only toggles visibility while switching servers.
                return RecordWrite(path, "login screen is driven by the headless lifecycle");

            case "ui" when parts.Length >= 3 && parts[1] == "mcPopup" && parts[2] == "currentLabel":
                Game.Client.PopupLabel = JsonUtil.AsString(value) ?? "Init";
                return true;

            case "ui" when path == "ui.mcInterface.areaList.title.t1.text":
                lock (_treeGate)
                    _areaTitleOverride = JsonUtil.AsString(value);
                return true;

            default:
                if (!s_displayRoots.Contains(parts[0]))
                    return RecordWrite(path, "object path not modelled");
                lock (_treeGate)
                    return AssignInto(_clientTree, parts, 0, value, path, create: true);
        }
    }

    private bool AssignWorld(string[] parts, JsonNode? value, string path)
    {
        // As AS3: with no world, obj[varName] = value throws and the Windows bridge answers the default. Not a gap.
        if (!Game.IsInGame)
            return false;
        string key = parts[1];
        switch (key)
        {
            case "WALKSPEED":
                Game.Client.WalkSpeed = JsonUtil.AsInt(value, 8);
                return true;
            case "SCALE":
                Game.Client.Scale = JsonUtil.AsInt(value, 1);
                return true;
            case "rootClass" when parts.Length > 2:
                return Assign(string.Join('.', parts.Skip(2)), value);
            case "visible":
                lock (_treeGate)
                    _worldExtra["visible"] = value?.DeepClone();
                return true;
            case "actions" when parts.Length == 5 && parts[2] == "active" && int.TryParse(parts[3], out int index):
                return Game.Client.SetSkillField(index, parts[4], value) || RecordWrite(path, "no such skill");
            case "myAvatar" when parts.Length >= 3 && parts[2] == "pMC":
                if (parts.Length == 4 && parts[3] is "x" or "y")
                    return true;   // the clip is not drawn; walkTo moves it
                lock (_treeGate)
                    return AssignInto(_pMC, parts, 3, value, path, create: true);
            case "myAvatar" when parts.Length == 3 && parts[2] == "target":
                if (value is null)
                {
                    Game.Client.SetTarget(null);
                    return true;
                }
                return RecordWrite(path, "a target can only be cleared by assignment");
        }
        if (Game.World.SetGameObject(path, value))
            return true;
        lock (_treeGate)
        {
            if (parts.Length == 2)
            {
                _worldExtra[key] = value?.DeepClone();
                Gaps.Record(FlashGapKind.Approximated, "set:" + path, null, "unknown world field stored as written");
                return true;
            }
        }
        return RecordWrite(path, "no such object in the model");
    }

    private bool AssignInto(JsonObject root, string[] parts, int from, JsonNode? value, string path, bool create = false)
    {
        JsonNode? current = root;
        for (int i = from; i < parts.Length - 1; i++)
        {
            if (current is JsonObject o)
            {
                if (!o.TryGetPropertyValue(parts[i], out JsonNode? next) || next is null)
                {
                    if (!create)
                        return RecordWrite(path, "no such object");
                    o[parts[i]] = next = new JsonObject();
                }
                current = next;
            }
            else if (current is JsonArray a && int.TryParse(parts[i], out int index) && index < a.Count)
            {
                current = a[index];
            }
            else
            {
                return RecordWrite(path, "no such object");
            }
        }
        string last = parts[^1];
        if (current is JsonObject target)
        {
            target[last] = value?.DeepClone();
            return true;
        }
        if (current is JsonArray arr && int.TryParse(last, out int at) && at >= 0)
        {
            while (arr.Count <= at)
                arr.Add(null);
            arr[at] = value?.DeepClone();
            return true;
        }
        return RecordWrite(path, "no such object");
    }

    private bool RecordWrite(string path, string why)
    {
        Gaps.Record(FlashGapKind.Missing, "set:" + path, null, why + "; nothing written");
        return false;
    }

    /// <summary>Display modules (skua.swf Modules): stored, nothing to draw.</summary>
    internal bool ModuleEnabled(string name)
    {
        lock (_treeGate)
            return _modules.Contains(name);
    }
}
