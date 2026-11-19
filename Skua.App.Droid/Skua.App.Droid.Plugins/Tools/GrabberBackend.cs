using Microsoft.Extensions.DependencyInjection;
using Skua.App.Droid.Core.Control;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Tools;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;
using Skua.Core.Services;

namespace Skua.App.Droid.Plugins.Tools;

/// <summary>
/// The Grabber on Skua's own pieces: Skua's <see cref="GrabberService"/> grabs (the desktop's categories) and runs the
/// actions that never ask anything (open, register and fake complete quests, unregister all, equip, house to bank,
/// kill, teleport, and get map item once the quantity is known); the desktop's buy and sell rules
/// (<see cref="GrabberService.IsPaidAcItem"/>, gold, one sale at a time, equipped) decide what is refused; buying,
/// selling, loading a shop, accepting quests and the bank moves go through the Control page's backend, the same calls
/// the Control page makes. Summary lines and exports are the desktop's (<see cref="GrabberText"/>).
/// </summary>
public sealed class GrabberBackend : IGrabberBackend
{
    /// <summary>Highest "Get Map Item" quantity offered (the desktop takes any number).</summary>
    public const int MaxMapItemQuantity = 100;

    private readonly Func<IScriptInterface?> _bot;
    private readonly Func<IServiceProvider?> _services;
    private readonly IControlBackend _control;
    private IServiceProvider? _grabberFor;
    private GrabberService? _grabber;

    public GrabberBackend(Func<IScriptInterface?> bot, Func<IServiceProvider?> services, IControlBackend control)
    {
        _bot = bot;
        _services = services;
        _control = control;
    }

    /// <summary>Skua's grabber service on the session's services (Skua.App.Avalonia registers the same).</summary>
    private GrabberService Grabber()
    {
        IServiceProvider services = _services() ?? throw new EngineException("The game is not available (no Skua session).");
        lock (this)
        {
            if (_grabber is null || !ReferenceEquals(_grabberFor, services))
            {
                _grabber = ActivatorUtilities.CreateInstance<GrabberService>(services);
                _grabberFor = services;
            }
            return _grabber;
        }
    }

    public static GrabberTypes TypeOf(GrabberCategory category) => category switch
    {
        GrabberCategory.ShopItems => GrabberTypes.Shop_Items,
        GrabberCategory.ShopIds => GrabberTypes.Shop_IDs,
        GrabberCategory.Quests => GrabberTypes.Quests,
        GrabberCategory.MapItemIds => GrabberTypes.GetMap_Item_IDs,
        GrabberCategory.Inventory => GrabberTypes.Inventory_Items,
        GrabberCategory.HouseInventory => GrabberTypes.House_Inventory_Items,
        GrabberCategory.TempInventory => GrabberTypes.Temp_Inventory_Items,
        GrabberCategory.Bank => GrabberTypes.Bank_Items,
        GrabberCategory.CellMonsters => GrabberTypes.Cell_Monsters,
        _ => GrabberTypes.Map_Monsters,
    };

    public IReadOnlyList<GrabbedItem> Grab(GrabberCategory category)
    {
        Ready();
        List<object> grabbed = Grabber().Grab(TypeOf(category));
        IJunkService? junk = _services()?.GetService<IJunkService>();
        return grabbed.Select(o => ToItem(o, junk)).ToList();
    }

    private static GrabbedItem ToItem(object o, IJunkService? junk)
    {
        (int id, string name) = o switch
        {
            Quest q => (q.ID, q.Name),
            ShopInfo s => (s.ID, s.Name),
            Monster m => (m.ID, m.Name),
            MapItem mi => (mi.ID, "Map item"),
            ItemBase i => (i.ID, i.Name),
            _ => (0, o.ToString() ?? string.Empty),
        };
        bool isJunk = false;
        if (o is ItemBase item && junk is not null)
        {
            try
            {
                isJunk = junk.IsJunk(item.ID);
            }
            catch (Exception)
            {
                // The junk list is a hint.
            }
        }
        return new GrabbedItem(id, name ?? string.Empty, GrabberText.FormatGrabbedItem(o), isJunk, o);
    }

    public GrabberPlan Plan(GrabberAction action, IReadOnlyList<GrabbedItem> items)
    {
        if (items.Count == 0 && action != GrabberAction.UnregisterAll)
            return GrabberPlan.Refuse("Select at least one item.");
        List<object> objects = items.Select(i => i.Handle).ToList();
        switch (action)
        {
            case GrabberAction.Buy:
            {
                List<ShopItem> shopItems = objects.OfType<ShopItem>().ToList();
                if (shopItems.Count != objects.Count)
                    return GrabberPlan.Refuse("Only shop items can be bought.");
                if (shopItems.Any(GrabberService.IsPaidAcItem))
                    return GrabberPlan.Refuse(GrabberService.PaidAcWarning);
                if (shopItems.Count == 1)
                {
                    ShopItem item = shopItems[0];
                    return new GrabberPlan(null, $"Buy {item.Name} (Cost: {item.Cost:N0} {(item.Coins ? "AC" : "Gold")} each)?",
                        new QuantityAsk("Buy quantity", 1, Math.Max(1, item.MaxStack), 1));
                }
                if (GrabberService.NotEnoughGold(shopItems, Gold()) is string shortOfGold)
                    return GrabberPlan.Refuse(Plain(shortOfGold));
                int total = shopItems.Where(i => !i.Coins).Sum(i => i.Cost);
                return new GrabberPlan(null, $"Buy one of each of the {shopItems.Count} items for {total:N0} gold?\n\n{string.Join("\n", shopItems.Select(i => i.Name))}", null);
            }
            case GrabberAction.Sell:
            case GrabberAction.SellAll:
            {
                if (objects.Count > 1)
                    return GrabberPlan.Refuse(Plain(GrabberService.MultipleSellWarning(objects.Count)));
                if (objects[0] is not InventoryItem item)
                    return GrabberPlan.Refuse("Only backpack items can be sold.");
                if (item.Equipped)
                    return GrabberPlan.Refuse(GrabberService.EquippedSellRefusal);
                int max = GrabberService.MaxSellQuantity(item);
                return action == GrabberAction.SellAll
                    ? new GrabberPlan(null, $"Sell all {max} {item.Name}?", null)
                    : new GrabberPlan(null, $"Sell {item.Name} (currently has: {max})?", new QuantityAsk("Sell quantity", 1, Math.Max(1, max), 1));
            }
            case GrabberAction.GetMapItem:
                return objects.All(o => o is MapItem)
                    ? new GrabberPlan(null, items.Count == 1 ? $"Get map item {items[0].Id}?" : $"Get {items.Count} map items?", new QuantityAsk("Quantity", 1, MaxMapItemQuantity, 1))
                    : GrabberPlan.Refuse("Only map items.");
            case GrabberAction.Equip:
                return new GrabberPlan(null, $"Equip {Names(items)}?", null);
            case GrabberAction.ToBank:
            case GrabberAction.HouseToBank:
                return new GrabberPlan(null, $"Move {Names(items)} to the bank?", null);
            case GrabberAction.ToInventory:
                return new GrabberPlan(null, $"Move {Names(items)} to the backpack?", null);
            case GrabberAction.RegisterQuest:
                return new GrabberPlan(null, $"Register {Names(items)}? Skua then accepts and turns them in by itself whenever they can be completed (turn-ins use up their items).", null);
            case GrabberAction.LoadShop:
            case GrabberAction.FakeComplete:
            case GrabberAction.TeleportTo:
                return items.Count == 1 ? new GrabberPlan(null, null, null) : GrabberPlan.Refuse("Select exactly one.");
            default:
                return new GrabberPlan(null, null, null);
        }
    }

    public async Task<string> RunAsync(GrabberAction action, IReadOnlyList<GrabbedItem> items, int quantity, IProgress<string> progress, CancellationToken cancellationToken)
    {
        GrabberPlan plan = Plan(action, items);
        if (plan.Refusal is string refusal)
            throw new EngineException(refusal);
        IScriptInterface bot = Ready();
        List<object> objects = items.Select(i => i.Handle).ToList();
        LastLine last = new(progress);
        string? success = null;
        switch (action)
        {
            case GrabberAction.Buy:
            {
                List<ShopItem> shopItems = objects.Cast<ShopItem>().ToList();
                if (shopItems.Count == 1)
                {
                    ShopItem item = shopItems[0];
                    int q = GrabberService.ClampBuyQuantity(item, quantity);
                    if (GrabberService.NotEnoughGold(item, q, Gold()) is string shortOfGold)
                        throw new EngineException(Plain(shortOfGold));
                    return await Task.Run(() => _control.Buy(item.ID, item.ShopItemID, q), cancellationToken).ConfigureAwait(false);
                }
                List<string> results = new();
                for (int index = 0; index < shopItems.Count; index++)
                {
                    ShopItem item = shopItems[index];
                    results.Add(await Task.Run(() => _control.Buy(item.ID, item.ShopItemID, -1), cancellationToken).ConfigureAwait(false));
                    progress.Report(results[^1]);
                    if (index != shopItems.Count - 1)
                        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                return string.Join(" ", results);
            }
            case GrabberAction.Sell:
            {
                InventoryItem item = (InventoryItem)objects[0];
                int q = Math.Clamp(quantity, 1, Math.Max(1, GrabberService.MaxSellQuantity(item)));
                return await Task.Run(() => _control.Sell(item.ID, q), cancellationToken).ConfigureAwait(false);
            }
            case GrabberAction.SellAll:
            {
                InventoryItem item = (InventoryItem)objects[0];
                return await Task.Run(() => _control.Sell(item.ID, -1), cancellationToken).ConfigureAwait(false);
            }
            case GrabberAction.LoadShop:
                return await Task.Run(() => _control.LoadShop(((ShopInfo)objects[0]).ID), cancellationToken).ConfigureAwait(false);
            case GrabberAction.AcceptQuest:
                return await Task.Run(() => string.Join(" ", QuestIds(objects).Select(id =>
                {
                    if (_control.ReadQuest(id) is null)
                        _control.LoadQuest(id);
                    return _control.AcceptQuest(id);
                })), cancellationToken).ConfigureAwait(false);
            case GrabberAction.ToBank:
                return await Task.Run(() =>
                {
                    if (!_control.ReadBank().Loaded)
                        _control.LoadBank();
                    return string.Join(" ", objects.Cast<ItemBase>().Select(i => _control.InventoryToBank(i.ID)));
                }, cancellationToken).ConfigureAwait(false);
            case GrabberAction.ToInventory:
                return await Task.Run(() => string.Join(" ", objects.Cast<ItemBase>().Select(i => _control.BankToInventory(i.ID))), cancellationToken).ConfigureAwait(false);
            case GrabberAction.OpenQuest:
                await Grabber().OpenQuests(objects, last, cancellationToken).ConfigureAwait(false);
                break;
            case GrabberAction.RegisterQuest:
                await Grabber().RegisterQuests(objects, last, cancellationToken).ConfigureAwait(false);
                break;
            case GrabberAction.FakeComplete:
                await Grabber().UpdateQuest(objects, last, cancellationToken).ConfigureAwait(false);
                break;
            case GrabberAction.UnregisterAll:
                await Task.Run(() => bot.Quests.UnregisterAllQuests(), cancellationToken).ConfigureAwait(false);
                return "Unregistered all quests.";
            case GrabberAction.GetMapItem:
                await GrabberService.GetMapItems(objects.Cast<MapItem>().ToList(), Math.Clamp(quantity, 1, MaxMapItemQuantity), last, cancellationToken).ConfigureAwait(false);
                break;
            case GrabberAction.Equip:
                await Grabber().EquipItems(objects, last, cancellationToken).ConfigureAwait(false);
                success = $"Equipped {Names(items)}.";
                break;
            case GrabberAction.HouseToBank:
                await Grabber().HouseInvToBank(objects, last, cancellationToken).ConfigureAwait(false);
                success = $"Moved {Names(items)} to the bank.";
                break;
            case GrabberAction.Kill:
                await Grabber().KillMonster(objects, last, cancellationToken).ConfigureAwait(false);
                break;
            case GrabberAction.TeleportTo:
                await Grabber().TeleportToMonster(objects, last, cancellationToken).ConfigureAwait(false);
                success = $"Jumped to {((Monster)objects[0]).Cell}.";
                break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (last.Line is string line && (line.StartsWith("Operation failed", StringComparison.Ordinal) || line.StartsWith("Failed", StringComparison.Ordinal) || line.StartsWith("Invalid", StringComparison.Ordinal) || line.StartsWith("No ", StringComparison.Ordinal) || line.StartsWith("Please", StringComparison.Ordinal)))
            throw new EngineException(line);
        return success ?? last.Line ?? "Done.";
    }

    public string CopyText(string title, IReadOnlyList<GrabbedItem> items) => GrabberText.BuildExportText(title, items.Select(i => i.Handle));

    public string DetailedText(string title, IReadOnlyList<GrabbedItem> items) => GrabberText.BuildDetailedExportText(title, items.Select(i => i.Handle));

    private static IEnumerable<int> QuestIds(IEnumerable<object> objects) => objects.Select(o => o switch
    {
        Quest q => q.ID,
        MapItem m => m.QuestID,
        _ => 0,
    }).Where(id => id > 0).Distinct();

    private static string Names(IReadOnlyList<GrabbedItem> items) =>
        items.Count == 1 ? items[0].Name : $"{items.Count} items ({string.Join(", ", items.Take(5).Select(i => i.Name))}{(items.Count > 5 ? ", ..." : string.Empty)})";

    private static string Plain(string desktopMessage) => desktopMessage.Replace("\r\n", "\n");

    private int Gold() => _bot()?.Player?.Gold ?? 0;

    private IScriptInterface Ready()
    {
        IScriptInterface bot = _bot() ?? throw new EngineException("The game is not available (no Skua session).");
        if (bot.Player?.LoggedIn != true)
            throw new EngineException("Log in first.");
        return bot;
    }

    /// <summary>Passes progress on and keeps the last line (Skua's grabber tasks report their outcome that way).</summary>
    private sealed class LastLine : IProgress<string>
    {
        private readonly IProgress<string> _inner;

        public LastLine(IProgress<string> inner) => _inner = inner;

        public string? Line { get; private set; }

        public void Report(string value)
        {
            Line = value;
            _inner.Report(value);
        }
    }
}
