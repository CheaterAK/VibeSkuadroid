using Skua.Core.Headless.Maps;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Rules;
using Skua.Core.Headless.World;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Client;

/// <summary>The character as the client rules see it: copies taken once, so a decision reads one consistent state.</summary>
public sealed class CharacterSnapshot : IQuestCharacter
{
    internal CharacterSnapshot(JsonObject objData, JsonArray items, JsonArray temp, JsonArray house, JsonArray factions)
    {
        ObjData = objData;
        Items = items.OfType<JsonObject>().ToList();
        Temp = temp.OfType<JsonObject>().ToList();
        House = house.OfType<JsonObject>().ToList();
        Factions = factions.OfType<JsonObject>().ToList();
    }

    /// <summary>objData with quest values and ranks applied.</summary>
    public JsonObject ObjData { get; }

    public IReadOnlyList<JsonObject> Items { get; }

    public IReadOnlyList<JsonObject> Temp { get; }

    public IReadOnlyList<JsonObject> House { get; }

    public IReadOnlyList<JsonObject> Factions { get; }

    public JsonObject? FindItem(int itemId)
    {
        return Items.FirstOrDefault(x => JsonUtil.AsInt(x["ItemID"]) == itemId) ?? House.FirstOrDefault(x => JsonUtil.AsInt(x["ItemID"]) == itemId);
    }

    public JsonObject? FindHeld(int itemId)
    {
        return FindItem(itemId) ?? Temp.FirstOrDefault(x => JsonUtil.AsInt(x["ItemID"]) == itemId);
    }

    public int HeldQuantity(int itemId) => JsonUtil.AsInt(FindHeld(itemId)?["iQty"]);

    public bool IsEquipped(int itemId) => JsonUtil.AsBool(FindItem(itemId)?["bEquip"]);

    public bool HasItem(int itemId) => FindItem(itemId) is not null;

    public int ClassPoints(int itemId) => FindItem(itemId) is JsonObject it ? JsonUtil.AsInt(it["iQty"]) : -1;

    public long Reputation(int factionId) => JsonUtil.AsLong(Factions.FirstOrDefault(f => JsonUtil.AsInt(f["FactionID"]) == factionId)?["iRep"]);
}

// Quests, items, shops, movement and the views of stored data with the client's derived fields.
public sealed partial class GameClient
{
    // ---------------------------------------------------------------- derived views

    /// <summary>
    /// objData as the Flash client holds it: <c>updateQuest</c> values folded into <c>strQuests*</c>
    /// (World.setQuestValue) and the class rank fields <c>iRank</c>, <c>iCurCP</c>, <c>iCPToRank</c> (Avatar.updateRep).
    /// Changes <paramref name="objData"/> (pass a copy).
    /// </summary>
    public JsonObject DecorateObjData(JsonObject objData)
    {
        IReadOnlyDictionary<int, int> updates = World.Read((_, _) => new Dictionary<int, int>(World.QuestValueUpdatesUnsafe));
        QuestRules.ApplyUpdates(objData, updates);
        if (objData["iCP"] is not null)
        {
            RankProgress p = RankRules.Progress(JsonUtil.AsLong(objData["iCP"]));
            objData["iRank"] = p.Rank;
            objData["iCurCP"] = p.PointsInRank;
            objData["iCPToRank"] = p.PointsToNextRank;
        }
        return objData;
    }

    /// <summary>Factions with <c>iRank</c>, <c>iRepToRank</c>, <c>iSpillRep</c> (Avatar.initFaction). Changes the array.</summary>
    public static JsonArray DecorateFactions(JsonArray factions)
    {
        foreach (JsonObject f in factions.OfType<JsonObject>())
        {
            long rep = JsonUtil.AsLong(f["iRep"]);
            RankProgress p = RankRules.Progress(rep);
            f["iRep"] = rep;
            f["iRank"] = p.Rank;
            f["iRepToRank"] = p.PointsToNextRank;
            f["iSpillRep"] = p.PointsInRank;
        }
        return factions;
    }

    /// <summary>
    /// Avatar.updateArmorRep: the equipped class item's quantity is its class points, so it follows objData.iCP
    /// (kills add class points to iCP only). Changes the array.
    /// </summary>
    public static JsonArray DecorateItems(JsonArray items, JsonObject objData)
    {
        if (JsonUtil.AsLongOrNull(objData["iCP"]) is not long cp)
            return items;
        foreach (JsonObject it in items.OfType<JsonObject>())
        {
            if (JsonUtil.AsString(it["sES"]) == "ar" && JsonUtil.AsBool(it["bEquip"]) && JsonUtil.AsString(it["sType"]) == "Class")
                it["iQty"] = cp;
        }
        return items;
    }

    /// <summary>A consistent copy of the character for the rules.</summary>
    public CharacterSnapshot Character()
    {
        (JsonObject objData, JsonArray items, JsonArray temp, JsonArray house, JsonArray factions) = World.Read((w, _) =>
        {
            JsonObject my = (JsonObject)w["myAvatar"]!;
            return (
                (JsonObject)my["objData"]!.DeepClone(),
                (JsonArray)my["items"]!.DeepClone(),
                (JsonArray)my["tempitems"]!.DeepClone(),
                (JsonArray)my["houseitems"]!.DeepClone(),
                (JsonArray)my["factions"]!.DeepClone());
        });
        DecorateObjData(objData);
        DecorateItems(items, objData);
        DecorateFactions(factions);
        return new CharacterSnapshot(objData, items, temp, house, factions);
    }

    /// <summary>
    /// <c>world.questTree</c> with the client's status: "p" in progress, "c" when every turn-in item is held
    /// (World.checkAllQuestStatus). Changes the object.
    /// </summary>
    public JsonObject DecorateQuestTree(JsonObject tree, CharacterSnapshot? character = null)
    {
        character ??= Character();
        IReadOnlySet<int> accepted = World.Read((_, _) => new HashSet<int>(World.AcceptedQuestsUnsafe));
        DateTimeOffset now = _time.GetUtcNow();
        foreach (KeyValuePair<string, JsonNode?> pair in tree)
        {
            if (pair.Value is not JsonObject q || !int.TryParse(pair.Key, out int id))
                continue;
            bool inProgress = accepted.Contains(id) || JsonUtil.AsString(q["status"]) is "p" or "c";
            q["status"] = QuestRules.Status(q, inProgress, character, now);
        }
        return tree;
    }

    // ---------------------------------------------------------------- quests

    /// <summary>World.getQuestValue (with the slot updates of this session).</summary>
    public int GetQuestValue(int slot) => QuestRules.GetQuestValue(Character().ObjData, slot);

    /// <summary>World.getAchievement.</summary>
    public int GetAchievement(string field, int index) => QuestRules.GetAchievement(Character().ObjData, field, index);

    /// <summary>World.isQuestInProgress.</summary>
    public bool IsQuestInProgress(int questId)
    {
        return World.Read((_, _) => World.AcceptedQuestsUnsafe.Contains(questId));
    }

    /// <summary>World.maximumQuestTurnIns.</summary>
    public int MaximumQuestTurnIns(int questId)
    {
        JsonObject? quest = World.GetQuest(questId)?.Raw;
        return QuestRules.MaximumTurnIns(quest, Character());
    }

    /// <summary>
    /// World.showQuests / showQuestList: asks for the quests not loaded yet, never the same one twice while its
    /// answer is pending (the server drops requests that come too fast and scripts retry every ~100 ms).
    /// </summary>
    public void ShowQuests(string ids)
    {
        List<int> missing = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : -1)
            .Where(id => id > 0 && World.GetQuest(id) is not { Loaded: true })
            .Distinct()
            .ToList();
        RequestQuests(missing);
    }

    private void RequestQuests(IEnumerable<int> ids)
    {
        long now = Now;
        List<int> fresh = new();
        lock (_gate)
        {
            foreach (int id in ids)
            {
                string key = id.ToString(CultureInfo.InvariantCulture);
                if (_questAsked.TryGetValue(key, out long at) && now - at < Options.QuestRequestWait.TotalMilliseconds)
                    continue;
                _questAsked[key] = now;
                fresh.Add(id);
            }
        }
        if (fresh.Count > 0)
            Send(Packets.GetQuests(World.RoomId, fresh));
    }

    private void OnQuestsLoaded(JsonObject d)
    {
        if (d["quests"] is not JsonObject quests)
            return;
        lock (_gate)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in quests)
            {
                _questAsked.Remove(pair.Key);
                if (int.TryParse(pair.Key, out int id))
                    _pendingAccept.Remove(id);
            }
        }
    }

    /// <summary>World.acceptQuest: loads it first when needed, then asks the server.</summary>
    public void AcceptQuest(int questId)
    {
        if (World.GetQuest(questId) is not { Loaded: true })
        {
            RequestQuests(new[] { questId });
            lock (_gate)
                _pendingAccept.Add(questId);
        }
        SendXt("acceptQuest", null, questId);
    }

    private void OnQuestAccepted(JsonObject d)
    {
        int id = JsonUtil.AsInt(d["QuestID"]);
        lock (_gate)
            _pendingAccept.Remove(id);
    }

    // ccqr: with the lite option "re-accept", a quest that can be done again is accepted again.
    private void OnQuestCompleted(JsonObject d)
    {
        if (!JsonUtil.AsBool(d["bSuccess"]) || !LiteFlag("bReaccept"))
            return;
        int id = JsonUtil.AsInt(d["QuestID"]);
        JsonObject? quest = World.GetQuest(id)?.Raw;
        if (quest is null || JsonUtil.AsInt(quest["bOnce"]) > 0 || QuestRules.AcceptRefusal(quest, Character()) is not null)
            return;
        AcceptQuest(id);
    }

    /// <summary>World.tryQuestComplete as the game sends it.</summary>
    public void TryQuestComplete(int questId, int itemId = -1, bool special = false, int quantity = 1)
    {
        SendXt("tryQuestComplete", null, questId, itemId, special, quantity, "wvz");
    }

    /// <summary>World.getMapItem (throttled).</summary>
    public void GetMapItem(int mapItemId)
    {
        if (Locks.TryAcquire("getMapItem"))
            Send(Packets.GetMapItem(World.RoomId, mapItemId));
    }

    // ---------------------------------------------------------------- items

    /// <summary>World.sendEquipItemRequest: true when the request went out.</summary>
    public bool EquipItem(int itemId)
    {
        CharacterSnapshot c = Character();
        if (c.FindItem(itemId) is null || c.IsEquipped(itemId))
            return false;
        if (!Locks.TryAcquire("equipItem"))
            return false;
        return SendXt("equipItem", null, itemId);
    }

    /// <summary>World.sendUnequipItemRequest.</summary>
    public bool UnequipItem(int itemId)
    {
        if (!Character().IsEquipped(itemId) || !Locks.TryAcquire("unequipItem"))
            return false;
        return SendXt("unequipItem", null, itemId);
    }

    /// <summary>
    /// World.equipUseableItem: the consumable goes into the potion slot (i1) and is marked equipped among consumables;
    /// the server is told with <c>geia</c>.
    /// </summary>
    public void EquipUsableItem(int itemId, string? description)
    {
        lock (_gate)
        {
            foreach (JsonObject slot in _active.OfType<JsonObject>().Where(a => JsonUtil.AsString(a["ref"]) == "i1"))
            {
                slot["sArg1"] = itemId.ToString(CultureInfo.InvariantCulture);
                slot["sArg2"] = description ?? string.Empty;
            }
        }
        string? link = null;
        string? meta = null;
        World.Write((w, _) =>
        {
            foreach (JsonObject it in ((JsonArray)((JsonObject)w["myAvatar"]!)["items"]!).OfType<JsonObject>())
            {
                if (!string.Equals(JsonUtil.AsString(it["sType"]), "Item", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(JsonUtil.AsString(it["sLink"]), "none", StringComparison.OrdinalIgnoreCase))
                    continue;
                bool chosen = JsonUtil.AsInt(it["ItemID"]) == itemId;
                it["bEquip"] = chosen ? 1 : 0;
                if (chosen)
                {
                    link = JsonUtil.AsString(it["sLink"]);
                    meta = JsonUtil.AsString(it["sMeta"]);
                }
            }
        }, "equipUseableItem");
        if (link is not null)
            SendXt("geia", null, link, meta ?? string.Empty, itemId);
    }

    /// <summary>World.sendSellItemRequest.</summary>
    public bool SellItem(int itemId, int quantity, int charItemId)
    {
        if (!Locks.TryAcquire("sellItem"))
            return false;
        return SendXt("sellItem", null, itemId, quantity, charItemId);
    }

    /// <summary>World.toggleBank: opens or closes the bank; the first open loads it.</summary>
    /// <remarks>
    /// The game loads it with Game.getBank (HTTP api/char/bank with the login token). The request is sent as
    /// <c>loadBank All</c>, the packet Skua's Bank.Load sends too; HeadlessGame answers that packet the game's way
    /// (AqwSession.LoadBankAsync) and only sends it to the server if HTTP fails.
    /// </remarks>
    public void ToggleBank()
    {
        bool open;
        lock (_gate)
        {
            open = _popupLabel != "Bank";
            _popupLabel = open ? "Bank" : "Init";
        }
        if (open && !World.IsBankLoaded)
            Send(Packets.LoadBank(World.RoomId));
    }

    // ---------------------------------------------------------------- shops

    /// <summary>World.sendLoadShopRequest: not again for the shop already loaded (unless it is limited), throttled.</summary>
    public void LoadShop(int shopId)
    {
        ShopInfo? shop = World.GetShop();
        if (shop is not null && shop.ShopId == shopId && !JsonUtil.AsBool(shop.Raw["bLimited"]))
            return;
        if (Locks.TryAcquire("loadShop"))
            Send(Packets.LoadShop(World.RoomId, shopId));
    }

    /// <summary>World.sendLoadHairShopRequest.</summary>
    public void LoadHairShop(int shopId)
    {
        if (Locks.TryAcquire("loadHairShop"))
            SendXt("loadHairShop", null, shopId);
    }

    /// <summary>World.sendLoadEnhShopRequest.</summary>
    public void LoadEnhShop(int shopId)
    {
        if (Locks.TryAcquire("loadEnhShop"))
            SendXt("loadEnhShop", null, shopId);
    }

    private JsonObject? FindShopItem(Func<JsonObject, bool> match)
    {
        ShopInfo? shop = World.GetShop();
        return shop?.Raw["items"] is JsonArray items ? items.OfType<JsonObject>().FirstOrDefault(match) : null;
    }

    /// <summary>skua.swf Shop.getShopItemByID (<paramref name="shopItemId"/> -1 = any).</summary>
    public JsonObject? ShopItem(int itemId, int shopItemId)
    {
        return FindShopItem(x => JsonUtil.AsInt(x["ItemID"]) == itemId && (shopItemId == -1 || JsonUtil.AsInt(x["ShopItemID"]) == shopItemId));
    }

    /// <summary>skua.swf Shop.getShopItem (by name, case-insensitive).</summary>
    public JsonObject? ShopItem(string name)
    {
        JsonObject? hit = FindShopItem(x => string.Equals(JsonUtil.AsString(x["sName"]), name, StringComparison.OrdinalIgnoreCase));
        return hit is null ? null : ShopItem(JsonUtil.AsInt(hit["ItemID"]), JsonUtil.AsInt(hit["ShopItemID"]));
    }

    /// <summary>
    /// skua.swf buyItemByName/ByID: <paramref name="quantity"/> -1 uses World.sendBuyItemRequest (with its checks),
    /// otherwise sendBuyItemRequestWithQuantity. Returns why nothing was sent, or null.
    /// </summary>
    public string? BuyItem(JsonObject? item, int quantity = -1)
    {
        if (item is null)
            return "Item is not in the loaded shop.";
        ShopInfo? shop = World.GetShop();
        if (shop is null)
            return "No shop is loaded.";
        if (quantity == -1)
        {
            if (!Locks.TryAcquire("buyItem"))
                return "Buying too fast.";
            CharacterSnapshot c = Character();
            string? refusal = ShopRules.BuyRefusal(item, shop.Raw, c.ObjData, c.Reputation, slot => QuestRules.GetQuestValue(c.ObjData, slot));
            if (refusal is not null)
                return refusal;
            lock (_gate)
                _shopBuyItem = item;
            SendXt("buyItem", null, JsonUtil.AsInt(item["ItemID"]), shop.ShopId, JsonUtil.AsInt(item["ShopItemID"]));
            return null;
        }
        lock (_gate)
            _shopBuyItem = item;
        SendXt("buyItem", null, JsonUtil.AsInt(item["ItemID"]), shop.ShopId, JsonUtil.AsInt(item["ShopItemID"]), quantity);
        return null;
    }

    /// <summary>skua.swf Shop.getUnmetPurchaseRequirements.</summary>
    public List<string> UnmetPurchaseRequirements(int itemId, int shopItemId, int quantity)
    {
        CharacterSnapshot c = Character();
        return ShopRules.UnmetPurchaseRequirements(
            ShopItem(itemId, shopItemId),
            quantity,
            id => c.FindHeld(id) is JsonObject o ? (JsonUtil.AsInt(o["iQty"]), JsonUtil.AsString(o["sName"])) : null,
            slot => QuestRules.GetQuestValue(c.ObjData, slot));
    }

    /// <summary>World.maximumShopBuys.</summary>
    public int MaximumShopBuys(JsonObject? item)
    {
        CharacterSnapshot c = Character();
        return ShopRules.MaximumShopBuys(item, c.ObjData, c.FindItem);
    }

    // ---------------------------------------------------------------- inventory pools

    /// <summary>loadInventoryBig.iMiscSlots (InvCat.MISC_SLOTS), default 100.</summary>
    public int MiscSlots
    {
        get { lock (_gate) return _miscSlots; }
    }

    /// <summary>skua.swf Inventory.inventoryHasSpaceFor.</summary>
    public bool HasSpaceFor(JsonObject? item, int quantity = 1, bool purchase = false, JsonObject? outgoing = null)
    {
        CharacterSnapshot c = Character();
        return InventoryRules.HasSpaceFor(item, c.Items, c.House, JsonUtil.AsInt(c.ObjData["iBagSlots"]), JsonUtil.AsInt(c.ObjData["iHouseSlots"]), MiscSlots, quantity, purchase, outgoing);
    }

    // ---------------------------------------------------------------- movement and the rest

    /// <summary>
    /// World.moveToCell: enters a cell (the model follows at once, as the client does), drops the target and, unless
    /// <paramref name="clientOnly"/>, tells the server. Refused while a kill lock (objLock) closes the cell.
    /// </summary>
    public void MoveToCell(string cell, string pad, bool clientOnly = false)
    {
        lock (_gate)
            SetTargetCore(null);
        if (clientOnly)
        {
            World.Write((w, me) =>
            {
                w["strFrame"] = cell;
                w["strPad"] = pad;
                if (me is not null && ((JsonObject)w["uoTree"]!)[me] is JsonObject leaf)
                {
                    leaf["strFrame"] = cell;
                    leaf["strPad"] = pad;
                }
            }, "moveToCell");
            return;
        }
        Send(Packets.MoveToCell(World.RoomId, cell, pad));
        // The model also moves when the packet is written (WorldModel.ApplyOutbound); do it now so a read right after sees it.
        World.ApplyOutbound(Packets.MoveToCell(World.RoomId, cell, pad));
    }

    /// <summary>skua.swf World.jumpCorrectRoom: moveToCell, with the pad corrected to one the cell has (when the cells are known).</summary>
    public void JumpCorrectRoom(string cell, string pad, bool autoCorrect = true, bool clientOnly = false)
    {
        MapCells? cells = Cells;
        string chosen = autoCorrect && cells is not null ? cells.CorrectPad(cell, pad) : pad;
        MoveToCell(cell, chosen, clientOnly);
    }

    public void SetSpawnPoint(string cell, string pad)
    {
        lock (_gate)
        {
            _spawnCell = cell;
            _spawnPad = pad;
        }
    }

    /// <summary>World.goto: to another player (only when idle).</summary>
    public void GoTo(string name)
    {
        if (World.GetPlayer() is { State: 1 })
            Send(Packets.Xt("cmd", 1, "goto", name.ToLowerInvariant()));
    }

    /// <summary>World.reloadCurrentMap: the map counts as loading for a moment.</summary>
    public void ReloadCurrentMap()
    {
        long generation;
        lock (_gate)
        {
            _mapLoading = true;
            generation = ++_mapGeneration;
            _mapTimer?.Dispose();
            _mapTimer = _time.CreateTimer(_ =>
            {
                lock (_gate)
                {
                    if (_mapGeneration == generation)
                        _mapLoading = false;
                }
            }, null, TimeSpan.FromMilliseconds(800), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>World.rest: only when idle, throttled.</summary>
    public void Rest()
    {
        if (World.GetPlayer() is not { State: 1 } || !Locks.TryAcquire("rest"))
            return;
        Send(Packets.Rest());
    }

    /// <summary>World.resurrect: asks the server to bring the dead character back.</summary>
    public void Resurrect()
    {
        if (World.GetPlayer() is { State: 0 })
            Send(Packets.Respawn(World.RoomId, World.MyUserId));
    }

    /// <summary>World.aggroAllMon: every monster in this cell.</summary>
    public void AggroAllMonsters()
    {
        string cell = World.GetArea().Cell;
        List<int> ids = World.GetMonsters().Where(m => m.Live is not null && JsonUtil.AsString(m.Live["strFrame"]) == cell).Select(m => m.MonMapId).ToList();
        if (ids.Count > 0)
            Send(Packets.AggroMonsters(World.RoomId, ids));
    }
}
