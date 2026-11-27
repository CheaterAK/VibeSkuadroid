using Skua.Core.Models.Items;
using System.Reflection;

namespace Skua.Core.Interfaces;

/// <summary>
/// An optional capability of an <see cref="IFlashUtil"/>: a shared, read-only parse of one of the game's item lists
/// (<c>world.bankinfo.items</c>, <c>world.myAvatar.items</c>, <c>world.myAvatar.tempitems</c>,
/// <c>world.myAvatar.houseitems</c>) that stays valid until the game changes that list. The inventory services use it
/// for lookups that hand no list to the caller (Contains, GetItem, TryGetItem, GetQuantity, IsMaxStack), instead of
/// parsing the whole list for every call; <see cref="IItemContainer{T}.Items"/> still returns a new list of new items.
/// The desktop's Flash bridge has none, and every lookup reads <see cref="IItemContainer{T}.Items"/> as it always did.
/// </summary>
public interface IItemListSnapshots
{
    /// <summary>
    /// The current items at <paramref name="path"/>, or null when this source cannot answer it (the caller then reads
    /// the list as before). The snapshot and its items are shared: callers must not change them, and hand out copies
    /// (<see cref="ItemListSnapshot{T}.Copy"/>).
    /// </summary>
    ItemListSnapshot<T>? GetItemListSnapshot<T>(string path) where T : ItemBase;
}

/// <summary>
/// An immutable list of parsed items with lookups by name and by ID that compare exactly as the inventory services
/// always have: the first item in list order whose <see cref="ItemBase.Name"/> equals the name ignoring case (ordinal;
/// Skua's assemblies compare strings with Caseless, <see cref="StringComparison.OrdinalIgnoreCase"/>), or whose
/// <see cref="ItemBase.ID"/> equals the ID. The indexes are built on first use.
/// </summary>
public sealed class ItemListSnapshot<T> where T : ItemBase
{
    private static readonly Func<object, object>? s_clone = BuildClone();

    private readonly T?[] _items;
    private Index? _index;

    public ItemListSnapshot(IEnumerable<T?> items)
    {
        _items = items.ToArray();
    }

    /// <summary>An empty snapshot (what an unreadable list gives: the services' default of an empty list).</summary>
    public static ItemListSnapshot<T> Empty { get; } = new(Array.Empty<T>());

    /// <summary>The items in the game's order (shared: never change them).</summary>
    public IReadOnlyList<T?> Items => _items;

    public int Count => _items.Length;

    /// <summary>The first item named <paramref name="name"/> (ignoring case), or null.</summary>
    public T? FirstByName(string? name)
    {
        Positions at = Find(name);
        return at.Found ? _items[at.First] : null;
    }

    /// <summary>The first item with ID <paramref name="id"/>, or null.</summary>
    public T? FirstById(int id)
    {
        return GetIndex().ById.TryGetValue(id, out Positions at) ? _items[at.First] : null;
    }

    /// <summary>Whether any item named <paramref name="name"/> (ignoring case) satisfies <paramref name="predicate"/>.</summary>
    public bool AnyByName(string? name, Func<T, int, bool> predicate, int argument)
    {
        return Any(Find(name), predicate, argument);
    }

    /// <summary>Whether any item with ID <paramref name="id"/> satisfies <paramref name="predicate"/>.</summary>
    public bool AnyById(int id, Func<T, int, bool> predicate, int argument)
    {
        return GetIndex().ById.TryGetValue(id, out Positions at) && Any(at, predicate, argument);
    }

    /// <summary>
    /// A copy of <paramref name="item"/> for a caller (what a fresh parse would have given it: the item types are flat,
    /// value fields and strings only), so a caller changing it changes nothing here.
    /// </summary>
    public static T? Copy(T? item)
    {
        if (item is null)
            return null;
        return s_clone is null ? item : (T)s_clone(item);
    }

    private Positions Find(string? name)
    {
        Index index = GetIndex();
        if (name is null)
            return index.NullNames;
        return index.ByName.TryGetValue(name, out Positions at) ? at : default;
    }

    private Index GetIndex()
    {
        Index? index = Volatile.Read(ref _index);
        if (index is not null)
            return index;
        index = new Index(_items);
        Interlocked.CompareExchange(ref _index, index, null);
        return Volatile.Read(ref _index)!;
    }

    private static Func<object, object>? BuildClone()
    {
        MethodInfo? memberwise = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        try
        {
            return memberwise?.CreateDelegate<Func<object, object>>();
        }
        catch
        {
            return null;
        }
    }

    private bool Any(Positions at, Func<T, int, bool> predicate, int argument)
    {
        if (!at.Found)
            return false;
        if (at.All is null)
            return predicate(_items[at.First]!, argument);
        foreach (int i in at.All)
        {
            if (predicate(_items[i]!, argument))
                return true;
        }
        return false;
    }

    /// <summary>Where the items with one name or ID are: the first, and all of them in order when there are several.</summary>
    private readonly record struct Positions(bool Found, int First, List<int>? All);

    // Positions per name and per ID, in list order. Null items are skipped (the services' Contains skips them).
    private sealed class Index
    {
        public Index(T?[] items)
        {
            ByName = new(items.Length, StringComparer.OrdinalIgnoreCase);
            ById = new(items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                T? item = items[i];
                if (item is null)
                    continue;
                if (item.Name is string n)
                    Add(ByName, n, i);
                else
                    NullNames = NullNames.Found ? new Positions(true, NullNames.First, With(NullNames, i)) : new Positions(true, i, null);
                Add(ById, item.ID, i);
            }
        }

        public Dictionary<string, Positions> ByName { get; }

        public Dictionary<int, Positions> ById { get; }

        public Positions NullNames { get; private set; }

        private static void Add<TKey>(Dictionary<TKey, Positions> map, TKey key, int i) where TKey : notnull
        {
            map[key] = map.TryGetValue(key, out Positions at) ? new Positions(true, at.First, With(at, i)) : new Positions(true, i, null);
        }

        private static List<int> With(Positions at, int i)
        {
            List<int> all = at.All ?? new List<int>(2) { at.First };
            all.Add(i);
            return all;
        }
    }
}
