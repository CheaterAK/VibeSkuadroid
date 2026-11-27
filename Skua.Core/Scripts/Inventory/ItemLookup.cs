using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using Skua.Core.Utils;

namespace Skua.Core.Scripts;

/// <summary>
/// The lookups of <see cref="ICheckInventory{T}"/> for the inventory services. When the Flash bridge offers a shared
/// snapshot of the list (<see cref="IItemListSnapshots"/>, the headless bridge), a lookup reads it through its indexes
/// and copies at most the one item it returns; otherwise (the desktop) it is exactly the interface's own code, which
/// reads (parses) the whole list for every call. Both compare as Skua always has (Caseless: names ignore case).
/// </summary>
internal static class ItemLookup
{
    private static class Rules<T> where T : ItemBase
    {
        // ICheckInventory.Contains: enough of the stack, or a class (whose quantity is its class points).
        public static readonly Func<T, int, bool> HasQuantity = (i, quantity) => i.Quantity >= quantity || i.Category == ItemCategory.Class;
    }

    private static ItemListSnapshot<T>? Snapshot<T>(IFlashUtil flash, string path) where T : ItemBase =>
        flash is IItemListSnapshots source ? source.GetItemListSnapshot<T>(path) : null;

    public static bool Contains<T>(IFlashUtil flash, string path, ICheckInventory<T> container, string name, int quantity) where T : ItemBase
    {
        if (quantity == 0)
            return true;
        if (Snapshot<T>(flash, path) is ItemListSnapshot<T> s)
            return s.AnyByName(name, Rules<T>.HasQuantity, quantity);
        return container.Items.Contains(i => i.Name == name && (i.Quantity >= quantity || i.Category == ItemCategory.Class));
    }

    public static bool Contains<T>(IFlashUtil flash, string path, ICheckInventory<T> container, int id, int quantity) where T : ItemBase
    {
        if (quantity == 0)
            return true;
        if (Snapshot<T>(flash, path) is ItemListSnapshot<T> s)
            return s.AnyById(id, Rules<T>.HasQuantity, quantity);
        return container.Items.Contains(i => i.ID == id && (i.Quantity >= quantity || i.Category == ItemCategory.Class));
    }

    public static T? GetItem<T>(IFlashUtil flash, string path, ICheckInventory<T> container, string name) where T : ItemBase
    {
        if (Snapshot<T>(flash, path) is ItemListSnapshot<T> s)
            return ItemListSnapshot<T>.Copy(s.FirstByName(name));
        return container.Items?.Find(x => x.Name == name);
    }

    public static T? GetItem<T>(IFlashUtil flash, string path, ICheckInventory<T> container, int id) where T : ItemBase
    {
        if (Snapshot<T>(flash, path) is ItemListSnapshot<T> s)
            return ItemListSnapshot<T>.Copy(s.FirstById(id));
        return container.Items?.Find(x => x.ID == id);
    }

    // GetQuantity and IsMaxStack read the item without handing it out: no copy.
    private static T? Peek<T>(IFlashUtil flash, string path, ICheckInventory<T> container, string name) where T : ItemBase =>
        Snapshot<T>(flash, path) is ItemListSnapshot<T> s ? s.FirstByName(name) : container.Items?.Find(x => x.Name == name);

    private static T? Peek<T>(IFlashUtil flash, string path, ICheckInventory<T> container, int id) where T : ItemBase =>
        Snapshot<T>(flash, path) is ItemListSnapshot<T> s ? s.FirstById(id) : container.Items?.Find(x => x.ID == id);

    public static int GetQuantity<T>(IFlashUtil flash, string path, ICheckInventory<T> container, string name) where T : ItemBase =>
        Peek(flash, path, container, name)?.Quantity ?? 0;

    public static int GetQuantity<T>(IFlashUtil flash, string path, ICheckInventory<T> container, int id) where T : ItemBase =>
        Peek(flash, path, container, id)?.Quantity ?? 0;

    public static bool IsMaxStack<T>(IFlashUtil flash, string path, ICheckInventory<T> container, string name) where T : ItemBase =>
        Peek(flash, path, container, name) is T item && item.Quantity >= item.MaxStack;

    public static bool IsMaxStack<T>(IFlashUtil flash, string path, ICheckInventory<T> container, int id) where T : ItemBase =>
        Peek(flash, path, container, id) is T item && item.Quantity >= item.MaxStack;
}
