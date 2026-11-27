using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;

namespace Skua.Core.Scripts;

public partial class ScriptTempInv : IScriptTempInv
{
    private readonly Lazy<IFlashUtil> _lazyFlash;
    private IFlashUtil Flash => _lazyFlash.Value;

    public ScriptTempInv(Lazy<IFlashUtil> flash)
    {
        _lazyFlash = flash;
    }

    [ObjectBinding("world.myAvatar.tempitems", Default = "new()")]
    private List<ItemBase> _items;

    // The lookups below read the bridge's shared snapshot when it offers one, else the whole list as before (ItemLookup).
    private const string ItemsPath = "world.myAvatar.tempitems";

    public bool Contains(string name, int quantity = 1) => ItemLookup.Contains(Flash, ItemsPath, this, name, quantity);

    public bool Contains(int id, int quantity = 1) => ItemLookup.Contains(Flash, ItemsPath, this, id, quantity);

    public ItemBase? GetItem(string name) => ItemLookup.GetItem(Flash, ItemsPath, this, name);

    public ItemBase? GetItem(int id) => ItemLookup.GetItem(Flash, ItemsPath, this, id);

    public bool TryGetItem(string name, out ItemBase? item) => (item = GetItem(name)) is not null;

    public bool TryGetItem(int id, out ItemBase? item) => (item = GetItem(id)) is not null;

    public int GetQuantity(string name) => ItemLookup.GetQuantity(Flash, ItemsPath, this, name);

    public int GetQuantity(int id) => ItemLookup.GetQuantity(Flash, ItemsPath, this, id);

    public bool IsMaxStack(string name) => ItemLookup.IsMaxStack(Flash, ItemsPath, this, name);

    public bool IsMaxStack(int id) => ItemLookup.IsMaxStack(Flash, ItemsPath, this, id);
}