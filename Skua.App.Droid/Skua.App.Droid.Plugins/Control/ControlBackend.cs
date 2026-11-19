using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Droid.Core.Control;
using Skua.App.Droid.Core.Engine;
using Skua.Core.Headless;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Commands;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Maps;
using Skua.Core.Headless.Rules;
using Skua.Core.Headless.World;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;

namespace Skua.App.Droid.Plugins.Control;

/// <summary>
/// The Control page's game side on the engine's Skua session: live reads from the headless world model and client
/// rules (<see cref="HeadlessGame"/>), manual actions through Skua's script API (<see cref="IScriptInterface"/>), the
/// client rules and <see cref="GameCommands"/>. Never builds a packet itself.
/// </summary>
public sealed partial class ControlBackend : IControlBackend
{
    /// <summary>Pads offered for a cell whose pads the map file did not tell.</summary>
    public static IReadOnlyList<string> CommonPads { get; } = ["Spawn", "Left", "Right"];

    /// <summary>Least time between two moves to a cell from this page (the game has no throttle for moveToCell).</summary>
    public static readonly TimeSpan JumpGap = TimeSpan.FromMilliseconds(500);

    /// <summary>Least time between two /goto from this page (as the game's tfer throttle).</summary>
    public static readonly TimeSpan GotoGap = TimeSpan.FromSeconds(3);

    /// <summary>How long a quest load waits for the server.</summary>
    public static readonly TimeSpan QuestLoadWait = TimeSpan.FromSeconds(6);

    private readonly Func<IScriptInterface?> _bot;
    private readonly Func<IServiceProvider?> _services;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, long> _last = new(StringComparer.Ordinal);

    /// <param name="bot">The session's script interface (null while it does not exist).</param>
    /// <param name="services">The session's services (the headless game is one of them), or null.</param>
    /// <param name="time">Clock for the page's own spacing of actions.</param>
    public ControlBackend(Func<IScriptInterface?> bot, Func<IServiceProvider?> services, TimeProvider? time = null)
    {
        _bot = bot;
        _services = services;
        _time = time ?? TimeProvider.System;
    }

    private HeadlessGame? Game
    {
        get
        {
            try
            {
                return _services()?.GetService<HeadlessGame>();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    // ------------------------------------------------------------------ reads

    public ControlState Read()
    {
        try
        {
            HeadlessGame? game = Game;
            if (game is null || game.Session.Connection is not { IsReady: true })
                return ControlState.Offline;
            WorldModel w = game.World;
            if (!w.IsCharacterLoaded || w.GetPlayer() is not PlayerInfo me)
                return ControlState.Offline;
            AreaInfo area = w.GetArea();
            if (area.RoomId == 0 && string.IsNullOrEmpty(area.AreaName))
                return ControlState.Offline;
            IReadOnlyList<MonsterInfo> monsters = w.GetMonsters();
            IReadOnlyList<AreaUserInfo> users = w.GetAreaUsers();
            List<AreaUserInfo> others = users.Where(u => !u.IsMe).ToList();
            string myCell = string.IsNullOrEmpty(area.Cell) ? me.Cell : area.Cell;
            string myPad = string.IsNullOrEmpty(area.Pad) ? me.Pad : area.Pad;

            MapCells? file = game.Client.Cells;
            bool fromFile = file is { HasCells: true };
            List<string> names = fromFile ? file!.Cells.ToList() : new List<string> { "Enter" };
            void Add(string? cell)
            {
                if (!string.IsNullOrWhiteSpace(cell) && !names.Contains(cell, StringComparer.Ordinal))
                    names.Add(cell);
            }
            foreach (MonsterInfo m in monsters.OrderBy(m => m.MonMapId))
                Add(m.Cell);
            foreach (AreaUserInfo u in others)
                Add(u.Cell);
            Add(myCell);

            List<ControlCell> cells = names.Select(n => new ControlCell(
                n,
                monsters.Count(m => m.Cell == n && m.IsAlive),
                monsters.Count(m => m.Cell == n),
                others.Count(u => u.Cell == n),
                n == myCell)).ToList();

            List<string> pads = fromFile && file!.Pads.TryGetValue(myCell, out IReadOnlyList<string>? known) && known.Count > 0
                ? known.ToList()
                : CommonPads.ToList();
            if (!string.IsNullOrEmpty(myPad) && !pads.Contains(myPad, StringComparer.Ordinal))
                pads.Add(myPad);

            TargetRef? target = game.Client.Target;
            List<ControlMonster> here = monsters
                .Where(m => m.Cell == myCell)
                .OrderBy(m => m.MonMapId)
                .Select(m => new ControlMonster(m.MonMapId, m.Name, m.Level, m.Hp, m.HpMax, m.State, target is { Kind: 'm' } && target.Id == m.MonMapId))
                .ToList();
            List<ControlPlayer> roomPlayers = others
                .Select(ToPlayer)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? targetText = target switch
            {
                { Kind: 'm' } => monsters.FirstOrDefault(m => m.MonMapId == target.Id) is MonsterInfo t ? $"{t.Name} (#{t.MonMapId})" : $"monster #{target.Id}",
                { Kind: 'p' } => target.Name ?? $"player #{target.Id}",
                _ => null,
            };

            List<ControlSkill> skills = w.GetSkills()
                .Where(s => !s.Passive && s.Index is >= 1 and <= 5)
                .Select(s => new ControlSkill(s.Index, s.Name.Length > 0 ? s.Name : s.Ref == "i1" ? "Potion" : s.Ref))
                .ToList();

            return new ControlState(
                true,
                string.IsNullOrEmpty(area.MapName) ? area.AreaName.Split('-')[0] : area.MapName,
                area.AreaName,
                myCell,
                myPad,
                cells,
                fromFile ? CellSource.MapFile : CellSource.RoomData,
                pads,
                here,
                roomPlayers.Where(p => p.Cell == myCell).ToList(),
                roomPlayers,
                ReadDrops(),
                skills,
                targetText,
                me.IsAlive,
                me.InCombat,
                me.Hp,
                me.HpMax,
                w.GetShop() is not null,
                w.IsBankLoaded,
                w.Version);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or NullReferenceException or KeyNotFoundException)
        {
            // A logout or relogin replaced the state under the read; the next read sees the new one.
            return ControlState.Offline;
        }
    }

    private static ControlPlayer ToPlayer(AreaUserInfo u)
    {
        bool afk = JsonUtil.AsBool(u.Leaf["afk"]);
        int level = u.Level > 0 ? u.Level : JsonUtil.AsInt(u.ObjData?["intLevel"]);
        return new ControlPlayer(u.Name, level, u.Cell, u.Pad, u.Hp, u.HpMax, u.State, afk);
    }

    private List<ControlDrop> ReadDrops()
    {
        try
        {
            return _bot()?.Drops.CurrentDropInfos.Select(d => new ControlDrop(d.ID, d.Name.Trim(), d.Quantity)).ToList() ?? [];
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException)
        {
            return [];
        }
    }

    public ControlShop? ReadShop()
    {
        HeadlessGame? game = Game;
        if (game?.World.GetShop() is not ShopInfo shop)
            return null;
        CharacterSnapshot c = game.Client.Character();
        int QuestValue(int slot) => QuestRules.GetQuestValue(c.ObjData, slot);
        List<ControlShopItem> items = new();
        foreach (ShopItemInfo i in shop.Items)
        {
            JsonObject raw = i.Raw;
            List<string> unmet = ShopRules.UnmetPurchaseRequirements(
                raw,
                -1,
                id => c.FindHeld(id) is JsonObject o ? (JsonUtil.AsInt(o["iQty"]), JsonUtil.AsString(o["sName"])) : null,
                QuestValue);
            string? refusal = ShopRules.BuyRefusal(raw, shop.Raw, c.ObjData, c.Reputation, QuestValue);
            items.Add(new ControlShopItem(
                i.ItemId,
                i.ShopItemId,
                i.Name,
                i.Cost,
                i.Coins,
                i.Member,
                Math.Max(1, i.Quantity),
                Math.Max(1, JsonUtil.AsInt(raw["iStk"], 1)),
                ShopRules.MaximumShopBuys(raw, c.ObjData, c.FindItem),
                unmet,
                refusal));
        }
        return new ControlShop(shop.ShopId, shop.Name, items);
    }

    public ControlQuest? ReadQuest(int questId)
    {
        HeadlessGame? game = Game;
        if (game?.World.GetQuest(questId) is not { Loaded: true, Raw: JsonObject raw } quest)
            return null;
        CharacterSnapshot c = game.Client.Character();
        JsonObject o = c.ObjData;

        List<string> requirements = new();
        int level = JsonUtil.AsInt(raw["iLvl"]);
        if (level > 1)
            requirements.Add($"Level {level} (you: {JsonUtil.AsInt(o["intLevel"])})");
        if (JsonUtil.AsBool(raw["bUpg"]))
            requirements.Add("Members only");
        int faction = JsonUtil.AsInt(raw["FactionID"]);
        if (faction > 1)
        {
            string factionName = c.Factions.FirstOrDefault(f => JsonUtil.AsInt(f["FactionID"]) == faction) is JsonObject f
                ? JsonUtil.AsString(f["sName"]) ?? $"faction #{faction}"
                : $"faction #{faction}";
            requirements.Add($"{JsonUtil.AsLong(raw["iReqRep"]):N0} reputation with {factionName} (you: {c.Reputation(faction):N0})");
        }
        int classId = JsonUtil.AsInt(raw["iClass"]);
        if (classId > 0)
            requirements.Add($"{JsonUtil.AsInt(raw["iReqCP"]):N0} class points in class #{classId}");
        if (raw["oReqd"] is JsonObject reqd)
        {
            foreach (KeyValuePair<string, JsonNode?> r in reqd)
            {
                string name = JsonUtil.AsString((r.Value as JsonObject)?["sName"]) ?? $"item #{r.Key}";
                bool held = int.TryParse(r.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && c.HasItem(id);
                requirements.Add($"Have {name}{(held ? " (have)" : " (missing)")}");
            }
        }
        bool once = JsonUtil.AsBool(raw["bOnce"]);
        if (once)
            requirements.Add("Once per character");

        List<ControlQuestItem> progress = new();
        if (raw["turnin"] is JsonArray turnin)
        {
            foreach (JsonObject t in turnin.OfType<JsonObject>())
            {
                int id = JsonUtil.AsInt(t["ItemID"]);
                string name = JsonUtil.AsString((raw["oItems"] as JsonObject)?[id.ToString(CultureInfo.InvariantCulture)]?["sName"]) ?? $"Item #{id}";
                progress.Add(new ControlQuestItem(id, name, c.HeldQuantity(id), Math.Max(1, JsonUtil.AsInt(t["iQty"], 1))));
            }
        }

        bool accepted = quest.Accepted || quest.Status is "p" or "c";
        bool completable = accepted && QuestRules.IsCompletable(raw, c, _time.GetUtcNow());
        return new ControlQuest(
            questId,
            string.IsNullOrEmpty(quest.Name) ? $"Quest {questId}" : quest.Name,
            JsonUtil.AsString(raw["sDesc"]) ?? string.Empty,
            accepted,
            completable,
            once,
            requirements,
            QuestRules.AcceptRefusal(raw, c),
            progress,
            Rewards(raw),
            QuestRules.MaximumTurnIns(raw, c));
    }

    private static List<string> Rewards(JsonObject raw)
    {
        List<string> rewards = new();
        long gold = JsonUtil.AsLong(raw["iGold"]);
        long exp = JsonUtil.AsLong(raw["iExp"]);
        if (gold > 0)
            rewards.Add($"{gold:N0} gold");
        if (exp > 0)
            rewards.Add($"{exp:N0} XP");
        // oRewards nests the reward items (itemsS, itemsR, ...); "reward" lists them with quantity and drop rate.
        Dictionary<int, string> names = new();
        void Walk(JsonNode? node, int depth)
        {
            if (depth > 4)
                return;
            if (node is JsonObject obj)
            {
                if (JsonUtil.AsIntOrNull(obj["ItemID"]) is int id && JsonUtil.AsString(obj["sName"]) is string name)
                    names.TryAdd(id, name);
                foreach (KeyValuePair<string, JsonNode?> child in obj)
                    Walk(child.Value, depth + 1);
            }
            else if (node is JsonArray array)
            {
                foreach (JsonNode? child in array)
                    Walk(child, depth + 1);
            }
        }
        Walk(raw["oRewards"], 0);
        if (raw["reward"] is JsonArray list && list.Count > 0)
        {
            foreach (JsonObject r in list.OfType<JsonObject>())
            {
                int id = JsonUtil.AsInt(r["ItemID"]);
                int qty = Math.Max(1, JsonUtil.AsInt(r["iQty"], 1));
                double rate = JsonUtil.AsDoubleOrNull(r["iRate"]) ?? 100;
                string name = names.GetValueOrDefault(id) ?? $"Item #{id}";
                rewards.Add($"{qty} x {name}{(rate < 100 ? $" ({rate.ToString("0.##", CultureInfo.InvariantCulture)}%)" : string.Empty)}");
            }
        }
        else
        {
            rewards.AddRange(names.Values.Select(n => $"1 x {n}"));
        }
        return rewards;
    }

    public ControlBank ReadBank()
    {
        HeadlessGame? game = Game;
        if (game is null || game.World.GetPlayer() is not PlayerInfo me)
            return new ControlBank(false, [], [], 0, 0);
        WorldModel w = game.World;
        return new ControlBank(
            w.IsBankLoaded,
            w.IsBankLoaded ? w.GetBank().Select(ToRow).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList() : [],
            w.GetInventory().Select(ToRow).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            me.BankSlots,
            me.BagSlots);
    }

    private static InventoryRow ToRow(ItemInfo i) => new(i.ItemId, i.Name, i.Quantity, i.MaxStack, i.Type, i.EquipSlot, i.Equipped, i.Temp, i.Member);

    // ------------------------------------------------------------------ actions

    public string JoinMap(string map)
    {
        string target = (map ?? string.Empty).Trim();
        if (!MapName().IsMatch(target))
            throw new EngineException("Enter a map name (letters, digits, '-' and '_' only), for example battleon or battleon-1234.");
        (IScriptInterface bot, HeadlessGame game) = Ready();
        bot.Wait.ForActionCooldown(GameActions.Transfer);
        try
        {
            AreaInfo area = game.Session.Commands.JoinMapAsync(target).GetAwaiter().GetResult();
            return $"Joined {area.AreaName}.";
        }
        catch (JoinFailedException e)
        {
            throw new EngineException($"Could not join {target}: {e.Message}", e);
        }
        catch (TimeoutException e)
        {
            throw new EngineException($"Could not join {target}: {e.Message}", e);
        }
        catch (InvalidOperationException e)
        {
            throw new EngineException($"Could not join {target}: {e.Message}", e);
        }
    }

    public string Jump(string cell, string? pad)
    {
        string c = (cell ?? string.Empty).Trim();
        if (!CellName().IsMatch(c))
            throw new EngineException("That is not a cell name.");
        if (pad is not null && !CellName().IsMatch(pad))
            throw new EngineException("That is not a pad name.");
        (IScriptInterface bot, HeadlessGame game) = Ready();
        Space("jump", JumpGap);
        string chosen = pad ?? game.Client.Cells?.CorrectPad(c, "Spawn") ?? "Spawn";
        bot.Map.Jump(c, chosen, autoCorrect: pad is null);
        AreaInfo area = game.World.GetArea();
        return $"Moved to {area.Cell} ({area.Pad}).";
    }

    public string GotoPlayer(string name)
    {
        string who = (name ?? string.Empty).Trim();
        if (!PlayerName().IsMatch(who))
            throw new EngineException("Enter a player name (letters, digits, spaces, '-' and '_').");
        (IScriptInterface bot, HeadlessGame game) = Ready();
        if (who.Equals(game.World.MyUsername, StringComparison.OrdinalIgnoreCase))
            throw new EngineException("That is this character.");
        if (game.World.GetPlayer() is not { State: 1 })
            throw new EngineException("The game only sends /goto while you are alive and out of combat.");
        Space("goto", GotoGap);
        bot.Player.Goto(who);
        return $"Sent /goto {who}.";
    }

    public string Attack(int monMapId)
    {
        (IScriptInterface bot, HeadlessGame game) = Ready();
        MonsterInfo m = game.World.GetMonster(monMapId) ?? throw new EngineException("That monster is not on this map.");
        if (m.Cell != game.World.GetArea().Cell)
            throw new EngineException($"{m.Name} is in {m.Cell}; move there first.");
        if (!m.IsAlive)
            throw new EngineException($"{m.Name} is dead; wait for it to respawn.");
        if (game.World.GetPlayer() is not { IsAlive: true })
            throw new EngineException("You are dead.");
        if (!bot.Combat.Attack(monMapId))
            throw new EngineException($"The game did not attack {m.Name}.");
        return $"Attacking {m.Name}.";
    }

    public string UseSkill(int index)
    {
        if (index is < 1 or > 5)
            throw new EngineException("Skills are 1 to 5.");
        (IScriptInterface bot, HeadlessGame game) = Ready();
        SkillInfo? skill = game.World.GetSkills().FirstOrDefault(s => !s.Passive && s.Index == index)
            ?? throw new EngineException($"There is no skill {index} on the skill bar.");
        string name = skill.Name.Length > 0 ? skill.Name : skill.Ref;
        if (!bot.Skills.UseSkill(index))
            throw new EngineException($"{name} is not ready (cooldown, mana, target or range).");
        return $"Used {name}.";
    }

    public string CancelTarget()
    {
        (IScriptInterface bot, _) = Ready();
        bot.Combat.CancelAutoAttack();
        bot.Combat.CancelTarget();
        return "Target cleared.";
    }

    public string Rest()
    {
        (IScriptInterface bot, HeadlessGame game) = Ready();
        PlayerInfo me = game.World.GetPlayer() ?? throw new EngineException("Log in first.");
        if (!me.IsAlive)
            throw new EngineException("You are dead.");
        if (me.InCombat)
            throw new EngineException("You cannot rest in combat.");
        bot.Player.Rest();
        return "Resting.";
    }

    public string Resurrect()
    {
        (_, HeadlessGame game) = Ready();
        if (game.World.GetPlayer() is { IsAlive: true })
            throw new EngineException("You are alive.");
        game.Client.Resurrect();
        return "Asked to resurrect (the game waits for the respawn timer).";
    }

    public string PickupDrop(int itemId)
    {
        (IScriptInterface bot, _) = Ready();
        ItemBase drop = bot.Drops.CurrentDropInfos.FirstOrDefault(d => d.ID == itemId) ?? throw new EngineException("That drop is gone.");
        bot.Drops.Pickup(new[] { itemId });
        if (bot.Drops.CurrentDropInfos.Any(d => d.ID == itemId))
            throw new EngineException($"{drop.Name} was not picked up (no room in the inventory, or no reply).");
        return $"Picked up {drop.Name}.";
    }

    public string RejectDrop(int itemId)
    {
        (IScriptInterface bot, _) = Ready();
        List<ItemBase> drops = bot.Drops.CurrentDropInfos.ToList();
        ItemBase drop = drops.FirstOrDefault(d => d.ID == itemId) ?? throw new EngineException("That drop is gone.");
        bot.Drops.RejectExcept(drops.Where(d => d.ID != itemId).Select(d => d.Name).ToArray());
        return $"Rejected {drop.Name}.";
    }

    public string LoadShop(int shopId)
    {
        if (shopId <= 0)
            throw new EngineException("Enter a shop ID.");
        (IScriptInterface bot, HeadlessGame game) = Ready();
        bot.Shops.Load(shopId);
        if (game.World.GetShop() is not { } shop || shop.ShopId != shopId)
            throw new EngineException($"Shop {shopId} did not load (no such shop, or not available here).");
        return $"Loaded {shop.Name} ({shop.Items.Count} items).";
    }

    public string Buy(int itemId, int shopItemId, int quantity)
    {
        (IScriptInterface bot, HeadlessGame game) = Ready();
        ControlShop shop = ReadShop() ?? throw new EngineException("Load a shop first.");
        ControlShopItem item = shop.Items.FirstOrDefault(i => i.ItemId == itemId && i.ShopItemId == shopItemId)
            ?? throw new EngineException("That item is not in the loaded shop.");
        if (item.Refusal is not null)
            throw new EngineException(item.Refusal);
        if (item.Unmet.Count > 0)
            throw new EngineException(string.Join(" ", item.Unmet));
        if (quantity != -1 && quantity < 1)
            throw new EngineException("Buy at least one.");
        if (item.MaxBuys <= 0)
            throw new EngineException("No room or funds for more of it.");
        if (quantity > item.MaxBuys)
            throw new EngineException($"At most {item.MaxBuys} can be bought now.");
        JsonObject? raw = game.Client.ShopItem(itemId, shopItemId);
        if (!game.Client.HasSpaceFor(raw, quantity == -1 ? item.Bundle : quantity, purchase: true))
            throw new EngineException("No room in the inventory.");
        int before = Held(game, itemId);
        bot.Shops.BuyItem(itemId, shopItemId, quantity);
        int after = Held(game, itemId);
        if (after <= before)
            throw new EngineException($"The purchase of {item.Name} did not go through (refused, or no reply).");
        return $"Bought {after - before} x {item.Name}.";
    }

    public string Sell(int itemId, int quantity)
    {
        (IScriptInterface bot, HeadlessGame game) = Ready();
        ItemInfo item = game.World.GetInventory().FirstOrDefault(i => i.ItemId == itemId) ?? throw new EngineException("That item is not in the backpack.");
        if (item.Equipped)
            throw new EngineException($"{item.Name} is equipped; unequip it first.");
        if (bot.Inventory.IsFavorited(itemId))
            throw new EngineException($"{item.Name} is a favourite; Skua does not sell favourites.");
        int sell = quantity == -1 ? item.Quantity : Math.Min(quantity, item.Quantity);
        if (sell < 1)
            throw new EngineException("Sell at least one.");
        int before = item.Quantity;
        bot.Shops.SellItem(itemId, sell);
        int after = game.World.GetInventory().FirstOrDefault(i => i.ItemId == itemId)?.Quantity ?? 0;
        if (after >= before)
            throw new EngineException($"{item.Name} was not sold (refused, or no reply).");
        return $"Sold {before - after} x {item.Name}.";
    }

    public string LoadQuest(int questId)
    {
        if (questId <= 0)
            throw new EngineException("Enter a quest ID.");
        (_, HeadlessGame game) = Ready();
        if (game.World.GetQuest(questId) is not { Loaded: true })
        {
            game.Client.ShowQuests(questId.ToString(CultureInfo.InvariantCulture));
            DateTimeOffset until = _time.GetUtcNow() + QuestLoadWait;
            while (game.World.GetQuest(questId) is not { Loaded: true })
            {
                if (_time.GetUtcNow() > until)
                    throw new EngineException($"Quest {questId} did not load (no such quest?).");
                Thread.Sleep(100);
            }
        }
        return $"Loaded quest {questId}: {game.World.GetQuest(questId)?.Name}.";
    }

    public string AcceptQuest(int questId)
    {
        (IScriptInterface bot, _) = Ready();
        ControlQuest quest = ReadQuest(questId) ?? throw new EngineException("Load the quest first.");
        if (quest.Accepted)
            return $"{quest.Name} is already accepted.";
        if (quest.AcceptRefusal is not null)
            throw new EngineException($"Cannot accept {quest.Name}: {quest.AcceptRefusal}");
        bot.Quests.Accept(questId);
        if (!bot.Quests.IsInProgress(questId))
            throw new EngineException($"The server did not accept {quest.Name}.");
        return $"Accepted {quest.Name}.";
    }

    public string TurnInQuest(int questId)
    {
        (IScriptInterface bot, _) = Ready();
        ControlQuest quest = ReadQuest(questId) ?? throw new EngineException("Load the quest first.");
        if (!quest.Accepted)
            throw new EngineException($"Accept {quest.Name} first.");
        if (!quest.Completable)
        {
            string missing = string.Join(", ", quest.Progress.Where(p => !p.Done).Select(p => p.Text));
            throw new EngineException($"{quest.Name} is not ready{(missing.Length > 0 ? $": {missing}" : ".")}");
        }
        if (!bot.Quests.Complete(questId))
            throw new EngineException($"The server did not complete {quest.Name}.");
        return $"Turned in {quest.Name}.";
    }

    public string LoadBank()
    {
        (IScriptInterface bot, HeadlessGame game) = Ready();
        bot.Bank.Load(true);
        if (!game.World.IsBankLoaded)
            throw new EngineException("The bank did not load.");
        return $"Bank loaded: {game.World.GetBank().Count} items.";
    }

    public string BankToInventory(int itemId)
    {
        (IScriptInterface bot, HeadlessGame game) = Ready();
        if (!game.World.IsBankLoaded)
            throw new EngineException("Load the bank first.");
        ItemInfo item = game.World.GetBank().FirstOrDefault(i => i.ItemId == itemId) ?? throw new EngineException("That item is not in the bank.");
        if (!bot.Bank.ToInventory(itemId))
            throw new EngineException($"{item.Name} was not moved (no room in the backpack, or no reply).");
        return $"Moved {item.Name} to the backpack.";
    }

    public string InventoryToBank(int itemId)
    {
        (IScriptInterface bot, HeadlessGame game) = Ready();
        if (!game.World.IsBankLoaded)
            throw new EngineException("Load the bank first.");
        ItemInfo item = game.World.GetInventory().FirstOrDefault(i => i.ItemId == itemId) ?? throw new EngineException("That item is not in the backpack.");
        if (item.Equipped)
            throw new EngineException($"{item.Name} is equipped; unequip it first.");
        if (!bot.Inventory.ToBank(itemId))
            throw new EngineException($"{item.Name} was not moved (class items stay out of the bank, the bank may be full, or no reply).");
        return $"Moved {item.Name} to the bank.";
    }

    // ------------------------------------------------------------------ helpers

    private (IScriptInterface Bot, HeadlessGame Game) Ready()
    {
        IScriptInterface bot = _bot() ?? throw new EngineException("The game is not available (no Skua session).");
        HeadlessGame game = Game ?? throw new EngineException("The game is not available (no Skua session).");
        if (game.Session.Connection is not { IsReady: true } || !game.World.IsCharacterLoaded || game.World.GetPlayer() is null)
            throw new EngineException("Log in first.");
        return (bot, game);
    }

    private static int Held(HeadlessGame game, int itemId)
    {
        CharacterSnapshot c = game.Client.Character();
        return c.HeldQuantity(itemId) + JsonUtil.AsInt(game.World.GetBank().FirstOrDefault(b => b.ItemId == itemId)?.Raw["iQty"]);
    }

    /// <summary>Waits until <paramref name="gap"/> has passed since this page's last action of that kind.</summary>
    private void Space(string action, TimeSpan gap)
    {
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        long wait;
        lock (_last)
        {
            long last = _last.GetValueOrDefault(action, long.MinValue / 2);
            wait = last + (long)gap.TotalMilliseconds - now;
            _last[action] = now + Math.Max(0, wait);
        }
        if (wait > 0)
            Thread.Sleep(TimeSpan.FromMilliseconds(wait));
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,40}(-[A-Za-z0-9]{1,12})?$")]
    private static partial Regex MapName();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]{1,40}$")]
    private static partial Regex CellName();

    [GeneratedRegex(@"^[A-Za-z0-9_\- ]{1,32}$")]
    private static partial Regex PlayerName();
}
