using Skua.Core.Headless.Client;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Rules;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Flash;

// callGameFunction targets (functions on the game's Game / World / Avatar / SmartFoxClient objects) and the
// linked-object (lnk*) and function-caller (fc*) registries of skua.swf's RemoteRegistry.as.
public sealed partial class HeadlessFlashUtil
{
    private Dictionary<string, (BridgeSupport, Func<object?[], object?>)> BuildFunctions()
    {
        Dictionary<string, (BridgeSupport, Func<object?[], object?>)> f = new(StringComparer.Ordinal);
        // Handlers return the function's result as a JsonNode (serialised by the caller), or Void for no result.
        void Add(string name, Func<object?[], object?> h) => f[name] = (BridgeSupport.Implemented, h);
        void NoOp(string name) => f[name] = (BridgeSupport.NoOp, _ => Void);
        void Impossible(string name, string why) => f[name] = (BridgeSupport.Impossible, a =>
        {
            Gaps.Record(FlashGapKind.Impossible, "fn:" + name, a, why);
            return Void;
        });
        object? Do(Action action)
        {
            action();
            return Void;
        }

        // ---- Game (the login screen and the socket)
        Add("login", a => Do(() => Game.Login(As3.Str(a, 0), As3.Str(a, 1))));
        Add("logout", _ => Do(Game.Logout));
        Add("connectTo", a => Do(() =>
        {
            GameServer? server = Game.FindServerByIp(As3.Str(a, 0), a.Length > 1 ? As3.Int(a, 1) : null);
            if (server is null)
                Gaps.Record(FlashGapKind.Missing, "fn:connectTo", a, "IP not in the server list of the last login; not connected");
            else
                Game.ConnectTo(server);
        }));
        Add("gotoAndPlay", a => Do(() =>
        {
            if (As3.Str(a, 0) == "Login")
                Game.ShowLoginScreen();
        }));
        NoOp("showTracking");
        NoOp("showInterface");
        NoOp("menuClose");
        Impossible("openArmorCustomize", "opens an interactive Flash panel");
        Add("toggleItemEquip", a => JsonValue.Create(ToggleItemEquip(As3.ToNode(a.Length > 0 ? a[0] : null) as JsonObject ?? As3.JsonArg(a, 0))));
        Add("loaderInfo.applicationDomain.hasDefinition", a => JsonValue.Create(As3.Str(a, 0) == "InvCat"));
        Add("loaderInfo.applicationDomain.getDefinition", a =>
        {
            if (As3.Str(a, 0) != "InvCat")
            {
                Gaps.Record(FlashGapKind.Impossible, "fn:loaderInfo.applicationDomain.getDefinition", a, "game classes other than InvCat are not available");
                return Void;
            }
            return new JsonObject { ["MISC_SLOTS"] = Game.Client.MiscSlots, ["MISC_TYPES"] = new JsonArray(InventoryRules.MiscTypes.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) };
        });

        // ---- SmartFoxClient
        Add("sfc.disconnect", _ => Do(Game.Disconnect));
        Add("sfc.sendString", a => Do(() => Game.SendRaw(As3.Str(a, 0))));
        Add("sfc.sendXtString", a => Do(() => Game.SendRaw(As3.Str(a, 0))));
        Add("sfc.sendJson", a => Do(() => Game.SendRaw(a.Length > 0 && a[0] is string s ? s : As3.ToNode(a.Length > 0 ? a[0] : null)?.ToJsonString() ?? string.Empty)));
        Add("sfc.sendXtMessage", a => Do(() =>
        {
            // sendXtMessage(ext, cmd, args, type, room)
            JsonArray? args = a.Length > 2 ? As3.ToNode(a[2]) as JsonArray : null;
            string list = args is null ? string.Empty : string.Join('%', args.Select(x => JsonUtil.AsString(x) ?? string.Empty));
            Game.SendRaw($"%xt%{As3.Str(a, 0, "zm")}%{As3.Str(a, 1)}%{As3.Int(a, 4, Game.World.RoomId)}%{list}%");
        }));
        Add("sfc.sendXml", a =>
        {
            Gaps.Record(FlashGapKind.Missing, "fn:sfc.sendXml", a, "SmartFox XML from scripts is not forwarded");
            return Void;
        });

        // ---- World: combat
        Add("world.approachTarget", _ => Do(Game.Client.ApproachTarget));
        Add("world.cancelTarget", _ => Do(Game.Client.CancelTarget));
        Add("world.cancelAutoAttack", _ => Do(Game.Client.CancelAutoAttack));
        Add("world.exitCombat", _ => Do(Game.Client.ExitCombat));
        Add("world.startAutoAttack", _ => Do(Game.Client.StartAutoAttack));
        Add("world.testAction", a =>
        {
            JsonObject? skill = As3.ToNode(a.Length > 0 ? a[0] : null) as JsonObject ?? As3.JsonArg(a, 0);
            string? skillRef = JsonUtil.AsString(skill?["ref"]);
            if (skillRef is null || !Game.Client.TestSkill(skillRef))
                Gaps.Record(FlashGapKind.Missing, "fn:world.testAction", a, "argument is not a skill of the bar; nothing done");
            return Void;
        });
        Add("world.aggroAllMon", _ => Do(Game.Client.AggroAllMonsters));
        Add("world.aggroMons", a => Do(() =>
        {
            List<int> ids = (As3.ToNode(a.Length > 0 ? a[0] : null) as JsonArray)?.Select(x => JsonUtil.AsInt(x)).ToList() ?? new();
            if (ids.Count > 0)
                Game.SendRaw(Protocol.Packets.AggroMonsters(Game.World.RoomId, ids));
        }));
        Add("world.rest", _ => Do(Game.Client.Rest));
        Add("world.resurrect", _ => Do(Game.Client.Resurrect));

        // ---- World: movement and map
        Add("world.moveToCell", a => Do(() => Game.Client.MoveToCell(As3.Str(a, 0), As3.Str(a, 1, "Spawn"), As3.Bool(a, 2))));
        Add("world.setSpawnPoint", a => Do(() => Game.Client.SetSpawnPoint(As3.Str(a, 0), As3.Str(a, 1))));
        Add("world.goto", a => Do(() => Game.Client.GoTo(As3.Str(a, 0))));
        Add("world.reloadCurrentMap", _ => Do(Game.Client.ReloadCurrentMap));
        Add("world.loadMap", _ => Do(Game.Client.ReloadCurrentMap));
        Add("world.getMapItem", a => Do(() => Game.Client.GetMapItem(As3.Int(a, 0))));
        Add("world.moveRequest", a => Do(() =>
        {
            JsonObject? o = As3.ToNode(a.Length > 0 ? a[0] : null) as JsonObject;
            Game.Client.MoveRequest(JsonUtil.AsInt(o?["tx"]), JsonUtil.AsInt(o?["ty"]), JsonUtil.AsInt(o?["sp"]));
        }));
        NoOp("world.setUserData");   // redraws name plates

        // ---- World: quests
        Add("world.showQuests", a => Do(() => Game.Client.ShowQuests(As3.Str(a, 0))));
        Add("world.showQuestList", a => Do(() => Game.Client.ShowQuests(As3.Str(a, 1))));
        Add("world.getQuests", a => Do(() => Game.Client.ShowQuests(As3.ToNode(a.Length > 0 ? a[0] : null) is JsonArray ids ? string.Join(',', ids.Select(x => JsonUtil.AsString(x))) : As3.Str(a, 0))));
        Add("world.acceptQuest", a => Do(() => Game.Client.AcceptQuest(As3.Int(a, 0))));
        Add("world.tryQuestComplete", a => Do(() => Game.Client.TryQuestComplete(As3.Int(a, 0), As3.Int(a, 1, -1), As3.Bool(a, 2), As3.Int(a, 3, 1))));
        Add("world.isQuestInProgress", a => JsonValue.Create(Game.Client.IsQuestInProgress(As3.Int(a, 0))));
        Add("world.getQuestValue", a => JsonValue.Create(Game.Client.GetQuestValue(As3.Int(a, 0))));
        Add("world.getAchievement", a => JsonValue.Create(Game.Client.GetAchievement(As3.Str(a, 0), As3.Int(a, 1))));
        Add("world.maximumQuestTurnIns", a => JsonValue.Create(Game.Client.MaximumQuestTurnIns(As3.Int(a, 0))));
        Add("world.abandonQuest", a => Do(() => Game.World.AbandonQuest(As3.Int(a, 0))));
        Add("world.completeQuest", a => Do(() => Game.World.AbandonQuest(As3.Int(a, 0))));
        Add("world.checkAllQuestStatus", _ => Void);   // quest status is computed on every read
        Add("world.updateQuestProgress", _ => Void);

        // ---- World: items, bank and shops
        Add("world.sendEquipItemRequest", a => JsonValue.Create(Game.Client.EquipItem(ItemId(a))));
        Add("world.sendUnequipItemRequest", a => Do(() => Game.Client.UnequipItem(ItemId(a))));
        Add("world.equipUseableItem", a => Do(() =>
        {
            JsonObject? o = As3.ToNode(a.Length > 0 ? a[0] : null) as JsonObject;
            Game.Client.EquipUsableItem(JsonUtil.AsInt(o?["ItemID"]), JsonUtil.AsString(o?["sDesc"]));
        }));
        Add("world.sendSellItemRequest", a => Do(() =>
        {
            JsonObject? o = As3.ToNode(a.Length > 0 ? a[0] : null) as JsonObject ?? As3.JsonArg(a, 0);
            Game.Client.SellItem(JsonUtil.AsInt(o?["ItemID"]), JsonUtil.AsInt(o?["iQty"], 1), JsonUtil.AsInt(o?["CharItemID"]));
        }));
        Add("world.toggleBank", _ => Do(Game.Client.ToggleBank));
        Add("world.sendLoadBankRequest", _ => Do(() => Game.SendRaw(Protocol.Packets.LoadBank(Game.World.RoomId))));
        Add("world.sendLoadShopRequest", a => Do(() => Game.Client.LoadShop(As3.Int(a, 0))));
        Add("world.sendLoadHairShopRequest", a => Do(() => Game.Client.LoadHairShop(As3.Int(a, 0))));
        Add("world.sendLoadEnhShopRequest", a => Do(() => Game.Client.LoadEnhShop(As3.Int(a, 0))));
        Add("world.sendBuyItemRequest", a => Do(() => ReportBuy(Game.Client.BuyItem(ShopItemArg(As3.ToNode(a.Length > 0 ? a[0] : null) as JsonObject)))));
        Add("world.sendBuyItemRequestWithQuantity", a => Do(() =>
        {
            JsonObject? o = As3.ToNode(a.Length > 0 ? a[0] : null) as JsonObject;
            if (o is not null && JsonUtil.AsBool(o["accept"]))
                ReportBuy(Game.Client.BuyItem(ShopItemArg(o["iSel"] as JsonObject), JsonUtil.AsInt(o["iQty"], 1)));
        }));
        Add("world.maximumShopBuys", a => JsonValue.Create(Game.Client.MaximumShopBuys(ShopItemArg(As3.ToNode(a.Length > 0 ? a[0] : null) as JsonObject ?? As3.JsonArg(a, 0)))));

        // ---- Avatar
        Add("world.myAvatar.pMC.artLoaded", _ => JsonValue.Create(Game.IsInGame && Game.World.IsInventoryLoaded));
        Add("world.myAvatar.getCPByID", a => JsonValue.Create(Game.Client.Character().ClassPoints(As3.Int(a, 0))));
        Add("world.myAvatar.getRep", a =>
        {
            CharacterSnapshot c = Game.Client.Character();
            int id = As3.Int(a, 0);
            if (id == 0 && a.Length > 0 && a[0] is string name && !int.TryParse(name, out _))
                return JsonValue.Create(JsonUtil.AsLong(c.Factions.FirstOrDefault(x => string.Equals(JsonUtil.AsString(x["sName"]), name, StringComparison.OrdinalIgnoreCase))?["iRep"]));
            return JsonValue.Create(c.Reputation(id));
        });
        Add("world.myAvatar.getItemByID", a => Game.Client.Character().FindItem(As3.Int(a, 0))?.DeepClone());
        Add("world.myAvatar.isItemEquipped", a => JsonValue.Create(Game.Client.Character().IsEquipped(As3.Int(a, 0))));
        Add("world.myAvatar.isItemInInventory", a => JsonValue.Create(Game.Client.Character().HasItem(As3.Int(a, 0))));
        Add("world.myAvatar.isItemInBank", a => JsonValue.Create(Game.World.FindBankItem(As3.Int(a, 0)) is not null));
        Add("world.myAvatar.isEmailVerified", _ =>
        {
            JsonObject od = Game.Client.Character().ObjData;
            return JsonValue.Create(JsonUtil.AsInt(od["intActivationFlag"]) == 5 || JsonUtil.AsInt(od["iEmailStatus"]) > 2);
        });
        Add("world.myAvatar.addItem", a => Do(() =>
        {
            if (As3.ToNode(a.Length > 0 ? a[0] : null) is JsonObject item)
                Game.World.AddItemLocally(item);
        }));
        Add("world.myAvatar.intPetID", a =>
        {
            // A property, not a function, in the game; scripts call it as one. The equipped pet's item id.
            Gaps.Record(FlashGapKind.Approximated, "fn:world.myAvatar.intPetID", a, "read as a property: the equipped pet's ItemID");
            JsonObject? pet = Game.Client.Character().Items.FirstOrDefault(i => JsonUtil.AsString(i["sES"]) == "pe" && JsonUtil.AsBool(i["bEquip"]));
            return JsonValue.Create(JsonUtil.AsInt(pet?["ItemID"]));
        });
        NoOp("world.myAvatar.loadMovieAtES");
        NoOp("world.myAvatar.unloadMovieAtES");
        NoOp("world.toggleMonsters");
        NoOp("world.showMonsters");
        NoOp("world.toggleQuestLog");
        Add("world.myAvatar.isUpgraded", _ => JsonValue.Create(JsonUtil.AsInt(Game.Client.Character().ObjData["iUpgDays"], -1) >= 0));

        // ---- objects that only exist as display clips
        Impossible("world.getAvatarByUserName", "returns a display object (Avatar with its clip); not serialisable");
        Impossible("world.getMonster", "returns a display object (Monster with its clip); not serialisable");
        Impossible("world.setTarget", "takes a display object; use attackMonster*/selectMonster*");
        return f;
    }

    /// <summary>Marks a function that returns nothing (AS3 void: <c>JSON.stringify(undefined)</c>).</summary>
    private static readonly object Void = new();

    private static int ItemId(object?[] a)
    {
        JsonNode? n = As3.ToNode(a.Length > 0 ? a[0] : null);
        return n is JsonObject o ? JsonUtil.AsInt(o["ItemID"]) : JsonUtil.AsInt(n);
    }

    // A shop item passed by value: use the loaded shop's entry (the game passes its own object).
    private JsonObject? ShopItemArg(JsonObject? item)
    {
        if (item is null)
            return null;
        return Game.Client.ShopItem(JsonUtil.AsInt(item["ItemID"]), JsonUtil.AsIntOrNull(item["ShopItemID"]) ?? -1) ?? item;
    }

    // Game.toggleItemEquip, as CoreBots uses it for consumables.
    private bool ToggleItemEquip(JsonObject? o)
    {
        if (o is null || Game.World.GetPlayer() is not { State: 1 })
            return false;
        int id = JsonUtil.AsInt(o["ItemID"]);
        bool item = string.Equals(JsonUtil.AsString(o["sType"]), "item", StringComparison.OrdinalIgnoreCase);
        string es = JsonUtil.AsString(o["sES"]) ?? string.Empty;
        if (JsonUtil.AsBool(o["bEquip"]))
        {
            if (es is "Weapon" or "ar")
                return false;
            if (!item)
                Game.Client.UnequipItem(id);
            return true;
        }
        CharacterSnapshot c = Game.Client.Character();
        if (JsonUtil.AsBool(o["bUpg"]) && JsonUtil.AsInt(c.ObjData["iUpgDays"], -1) < 0)
            return false;
        if (JsonUtil.AsInt(o["EnhLvl"]) > JsonUtil.AsInt(c.ObjData["intLevel"]))
            return false;
        if (!item && es is not ("mi" or "co" or "pe" or "am") && JsonUtil.AsInt(o["EnhID"]) <= 0)
            return false;
        if (!item)
            return Game.Client.EquipItem(id);
        Game.Client.EquipUsableItem(id, JsonUtil.AsString(o["sDesc"]));
        return true;
    }

    /// <summary>Main.callGameFunction: the function's result as JSON, or undefined for void.</summary>
    private object? CallGameFunctionCore(string path, object?[] args)
    {
        if (!_functions.TryGetValue(path, out (BridgeSupport Support, Func<object?[], object?> Handler) fn))
        {
            Gaps.Record(FlashGapKind.Missing, "fn:" + path, args, "game function not implemented; answered undefined");
            return null;
        }
        object? result = fn.Handler(args);
        if (ReferenceEquals(result, Void))
            return null;
        return result is JsonNode node ? node.ToJsonString() : result is null ? "null" : result;
    }

    // ---------------------------------------------------------------- RemoteRegistry

    private sealed class Link
    {
        public Link(string? path, JsonNode? detached)
        {
            Path = path;
            Detached = detached;
        }

        /// <summary>The object path this link reads live (null for a detached value).</summary>
        public string? Path { get; }

        /// <summary>A value held by the link itself (ObjectParent: results of fcCallFlash / lnkCall).</summary>
        public JsonNode? Detached { get; set; }

        public Dictionary<string, int> Children { get; } = new(StringComparer.Ordinal);

        public string? FunctionName { get; set; }

        public List<JsonNode?> Args { get; } = new();
    }

    private readonly object _linkGate = new();
    private readonly Dictionary<int, Link> _links = new();
    private int _nextLink;

    private int NewLink(Link link)
    {
        lock (_linkGate)
        {
            int id = _nextLink++;
            _links[id] = link;
            return id;
        }
    }

    private Link? GetLink(int id)
    {
        lock (_linkGate)
            return _links.GetValueOrDefault(id);
    }

    private int LinkCreate(string path) => NewLink(new Link(path, null));

    // LinkedObject.getChild: cached per parent and path. A child of a detached value is detached too.
    private int LinkChild(int id, string path)
    {
        Link? parent = GetLink(id);
        if (parent is null)
        {
            Gaps.Record(FlashGapKind.Missing, "lnkGetChild", new object?[] { id, path }, "no such link (AS3 would throw); answered -1");
            return -1;
        }
        lock (_linkGate)
        {
            if (parent.Children.TryGetValue(path, out int cached) && _links.ContainsKey(cached))
                return cached;
        }
        Link child = parent.Path is not null
            ? new Link(parent.Path + "." + path, null)
            : new Link(null, Walk(Lookup.Of(parent.Detached), path.Split('.'), 0).Node?.DeepClone());
        int childId = NewLink(child);
        lock (_linkGate)
            parent.Children[path] = childId;
        return childId;
    }

    private void LinkDestroy(int id)
    {
        lock (_linkGate)
        {
            if (!_links.Remove(id, out Link? link))
                return;
            foreach (int child in link.Children.Values)
                _links.Remove(child);
        }
    }

    private void LinkDeleteChild(int id, string path)
    {
        Link? parent = GetLink(id);
        if (parent is null)
            return;
        int child;
        lock (_linkGate)
        {
            if (!parent.Children.Remove(path, out child))
                return;
        }
        LinkDestroy(child);
    }

    private JsonNode? LinkValue(Link link)
    {
        return link.Path is not null ? Resolve(link.Path).Node : link.Detached;
    }

    private object? LinkGetValue(int id)
    {
        Link? link = GetLink(id);
        if (link is null)
            return null;
        if (link.Path is not null)
        {
            Lookup l = Resolve(link.Path);
            return As3.Stringify(l.Node, l.Defined);
        }
        return As3.Stringify(link.Detached, true);
    }

    private void LinkSetValue(int id, object? value)
    {
        Link? link = GetLink(id);
        if (link is null)
            return;
        if (link.Path is not null)
            Assign(link.Path, As3.ToNode(value));
        else
            link.Detached = As3.ToNode(value);
    }

    // LinkedObject.call: calls a function of the linked object; the result is wrapped in a new (detached) link and,
    // since JSON.stringify of a LinkedObject has no public fields, the callback answers "{}".
    private object? LinkCall(int id, string function, object?[] args)
    {
        Link? link = GetLink(id);
        if (link?.Path is null)
        {
            Gaps.Record(FlashGapKind.Missing, "lnkCall", args, "only links to object paths can be called");
            return null;
        }
        object? result = CallGameFunctionCore(link.Path + "." + function, args);
        NewLink(new Link(null, result is string s ? TryParse(s) : null));
        return "{}";
    }

    private int FcCreate(int objId, string name)
    {
        Link? owner = GetLink(objId);
        Link fc = new(owner?.Path, null) { FunctionName = name };
        if (owner?.Path is null)
            Gaps.Record(FlashGapKind.Missing, "fcCreate", new object?[] { objId, name }, "function caller on a link that is not an object path");
        return NewLink(fc);
    }

    private void FcPush(int id, object?[] args)
    {
        Link? fc = GetLink(id);
        if (fc is null)
            return;
        lock (_linkGate)
            fc.Args.AddRange(args.Select(As3.ToNode));
    }

    private void FcPushLinked(int id, object? argIds)
    {
        Link? fc = GetLink(id);
        if (fc is null || As3.ToNode(argIds) is not JsonArray ids)
            return;
        List<JsonNode?> values = ids.Select(x => GetLink(JsonUtil.AsInt(x)) is Link l ? LinkValue(l)?.DeepClone() : null).ToList();
        lock (_linkGate)
            fc.Args.AddRange(values);
    }

    private void FcClear(int id)
    {
        Link? fc = GetLink(id);
        if (fc is null)
            return;
        lock (_linkGate)
            fc.Args.Clear();
    }

    private object? FcCall(int id, bool asLink)
    {
        Link? fc = GetLink(id);
        if (fc?.FunctionName is null || fc.Path is null)
        {
            Gaps.Record(FlashGapKind.Missing, asLink ? "fcCallFlash" : "fcCall", new object?[] { id }, "no such function caller");
            return null;
        }
        object?[] args;
        lock (_linkGate)
            args = fc.Args.Select(x => (object?)x).ToArray();
        object? result = CallGameFunctionCore(fc.Path + "." + fc.FunctionName, args);
        if (!asLink)
            return result;
        return NewLink(new Link(null, result is string s ? TryParse(s) : null)).ToString(CultureInfo.InvariantCulture);
    }

    private static JsonNode? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(json);
        }
    }
}
