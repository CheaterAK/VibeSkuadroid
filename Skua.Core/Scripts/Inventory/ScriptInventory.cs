using Newtonsoft.Json;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Core.Models.Shops;
using System.Dynamic;

namespace Skua.Core.Scripts;

public partial class ScriptInventory : IScriptInventory
{
    private readonly Lazy<IFlashUtil> _lazyFlash;
    private readonly Lazy<IScriptSend> _lazySend;
    private readonly Lazy<IScriptOption> _lazyOptions;
    private readonly Lazy<IScriptWait> _lazyWait;
    private readonly Lazy<IScriptMap> _lazyMap;
    private readonly Lazy<IScriptManager> _lazyManager;
    private readonly Lazy<IScriptPlayer> _lazyPlayer;
    private IFlashUtil Flash => _lazyFlash.Value;
    private IScriptOption Options => _lazyOptions.Value;
    private IScriptWait Wait => _lazyWait.Value;
    private IScriptMap Map => _lazyMap.Value;
    private IScriptManager Manager => _lazyManager.Value;
    private IScriptPlayer Player => _lazyPlayer.Value;
    private IScriptSend Send => _lazySend.Value;

    public ScriptInventory(
        Lazy<IFlashUtil> flash,
        Lazy<IScriptSend> send,
        Lazy<IScriptOption> options,
        Lazy<IScriptWait> wait,
        Lazy<IScriptMap> map,
        Lazy<IScriptManager> manager,
        Lazy<IScriptPlayer> player)
    {
        _lazyFlash = flash;
        _lazySend = send;
        _lazyOptions = options;
        _lazyWait = wait;
        _lazyMap = map;
        _lazyManager = manager;
        _lazyPlayer = player;
    }

    [ObjectBinding("world.myAvatar.items", Default = "new()")]
    private List<InventoryItem> _items;

    // The lookups below read the bridge's shared snapshot when it offers one, else the whole list as before (ItemLookup).
    private const string ItemsPath = "world.myAvatar.items";

    public bool Contains(string name, int quantity = 1) => ItemLookup.Contains(Flash, ItemsPath, this, name, quantity);

    public bool Contains(int id, int quantity = 1) => ItemLookup.Contains(Flash, ItemsPath, this, id, quantity);

    public InventoryItem? GetItem(string name) => ItemLookup.GetItem(Flash, ItemsPath, this, name);

    public InventoryItem? GetItem(int id) => ItemLookup.GetItem(Flash, ItemsPath, this, id);

    public bool TryGetItem(string name, out InventoryItem? item) => (item = GetItem(name)) is not null;

    public bool TryGetItem(int id, out InventoryItem? item) => (item = GetItem(id)) is not null;

    public int GetQuantity(string name) => ItemLookup.GetQuantity(Flash, ItemsPath, this, name);

    public int GetQuantity(int id) => ItemLookup.GetQuantity(Flash, ItemsPath, this, id);

    public bool IsMaxStack(string name) => ItemLookup.IsMaxStack(Flash, ItemsPath, this, name);

    public bool IsMaxStack(int id) => ItemLookup.IsMaxStack(Flash, ItemsPath, this, id);

    [ObjectBinding("world.myAvatar.objData.iBagSlots")]
    private int _slots;

    // Keep Items complete; only slot usage is category-specific.
    public int UsedSlots => Flash.Call<int>("inventoryBagUsedSlots");
    public bool HasCategories => Flash.Call<bool>("hasInventoryCategories");
    public int MiscSlots => Flash.Call<int>("inventoryMiscSlots");
    public int MiscUsedSlots => Flash.Call<int>("inventoryMiscUsedSlots");
    public int MiscFreeSlots => MiscSlots - MiscUsedSlots;

    public string GetPool(ItemBase item) => Flash.Call<string>("inventoryPool", JsonConvert.SerializeObject(item)) ?? "bag";

    public bool HasSpaceFor(ItemBase item, int quantity = 1) =>
        Flash.Call<bool>("inventoryHasSpaceFor", JsonConvert.SerializeObject(item), quantity, item is ShopItem);

    public bool CanBank(InventoryItem item) => item.Category != ItemCategory.Class || !HasCategories;

    public bool IsFavorited(int itemId) => Flash.Call<bool>("isFavoriteItem", itemId);

    public void EquipItem(int id)
    {
        Wait.ForActionCooldown(GameActions.EquipItem);
        dynamic item = new ExpandoObject();
        item.ItemID = id;
        Flash.CallGameFunction("world.sendEquipItemRequest", item);
        Wait.ForItemEquip(id);
    }

    public void EquipUsableItem(InventoryItem? item)
    {
        if (item is null)
            return;
        Wait.ForActionCooldown(GameActions.EquipItem);

        dynamic dynItem = new ExpandoObject();
        dynItem.ItemID = item.ID;
        dynItem.sDesc = item.Description;
        dynItem.sFile = item.FileLink;
        dynItem.sName = item.Name;

        Flash.CallGameFunction("world.equipUseableItem", dynItem);

        Wait.ForItemEquip(item.ID);
    }

    public bool ToBank(InventoryItem item)
    {
        if (!CanBank(item))
            return false;
        Send.Packet($"%xt%zm%bankFromInv%{Map.RoomID}%{item.ID}%{item.CharItemID}%");
        Wait.ForInventoryToBank(item.Name);
        return !((IScriptInventory)this).Contains(item.Name);
    }

    public bool EnsureToBank(string name)
    {
        if (!((IScriptInventory)this).TryGetItem(name, out InventoryItem? item))
            return false;
        if (!CanBank(item!))
            return false;
        int i = 0;
        while (!ToBank(item!) && !Manager.ShouldExit && Player.Playing && ++i < Options.MaximumTries)
            Thread.Sleep(Options.ActionDelay);

        return !((IScriptInventory)this).Contains(name);
    }

    public bool EnsureToBank(int id)
    {
        if (!((IScriptInventory)this).TryGetItem(id, out InventoryItem? item))
            return false;
        if (!CanBank(item!))
            return false;
        int i = 0;
        while (!ToBank(item!) && !Manager.ShouldExit && Player.Playing && ++i < Options.MaximumTries)
            Thread.Sleep(Options.ActionDelay);

        return !((IScriptInventory)this).Contains(id);
    }
}