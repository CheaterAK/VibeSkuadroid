using Newtonsoft.Json;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using Skua.Core.Headless.World;
using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Flash;

// Skua reads its item lists ([ObjectBinding] Items of Bank, Inventory, TempInv, House) for every Contains, TryGetItem,
// GetItem and GetQuantity: getGameObject, then Newtonsoft into a fresh List. CoreBots' CheckInventory makes four such
// reads, its farm loops call it every 250 ms and Skua's waits poll Inventory.Contains every 50 ms. Measured on a
// desktop CPU with a 253-item backpack and a 3000-item bank (DailiesPatternTests): 2.7-5 MB and 3-30 ms per backpack
// lookup, 0.4 MB per bank lookup, 30 MB and 44 ms for the first bank read after each bank move; a simulated daily
// allocated 150 MB/s. So, per list and element type:
// * the parse is kept until the model says the list changed (WorldModel.BankVersion, InventoryKey,
//   TempInventoryVersion, HouseInventoryVersion);
// * the bank is parsed per item and an item is parsed again only when its stored object changed (WorldModel.ReadBankItems:
//   the model never changes a bank item in place), so a move costs one item's parse, not the bank's;
// * lookups read a shared immutable snapshot with indexes (IItemListSnapshots, used by Skua.Core's ItemLookup) and copy
//   only the item they return; Items still gets a new list of copies (the element types are flat: value fields and
//   strings only, so a memberwise copy is what a fresh parse gives).
public sealed partial class HeadlessFlashUtil : IItemListSnapshots
{
    /// <summary>The path Skua's bank binding reads.</summary>
    public const string BankItemsPath = "world.bankinfo.items";

    /// <summary>The path Skua's backpack binding reads.</summary>
    public const string InventoryItemsPath = "world.myAvatar.items";

    /// <summary>The path Skua's temporary inventory binding reads.</summary>
    public const string TempItemsPath = "world.myAvatar.tempitems";

    /// <summary>The path Skua's house inventory binding reads.</summary>
    public const string HouseItemsPath = "world.myAvatar.houseitems";

    private readonly object _listGate = new();
    private readonly Dictionary<(string Path, Type Element), ListCache> _lists = new();
    private int _bankParses;
    private long _bankItemParses;
    private int _listParses;

    private sealed class ListCache
    {
        public object? Key;
        public IList? Items;
        public object? Snapshot;

        // The backpack: the undecorated parse, where the equipped class is, and the class points Items is decorated for.
        public IList? Raw;
        public int[]? ClassItems;
        public long ClassPoints;

        // The bank: each stored item object's parse, valid within one generation of the model's bank.
        public long Generation = -1;
        public Dictionary<object, object?>? ByObject;
    }

    /// <summary>How many times the bank list was rebuilt for a typed read (tests: once per bank version and type).</summary>
    public int BankParses => Volatile.Read(ref _bankParses);

    /// <summary>How many bank items were parsed (tests: a move parses one, not the bank).</summary>
    public long BankItemParses => Interlocked.Read(ref _bankItemParses);

    /// <summary>How many times a backpack, temporary or house list was parsed.</summary>
    public int ListParses => Volatile.Read(ref _listParses);

    private static bool IsItemListPath(string path) =>
        path is BankItemsPath or InventoryItemsPath or TempItemsPath or HouseItemsPath;

    private bool TryBankItemsJson(string path, out string? json)
    {
        if (path == BankItemsPath && Game.IsInGame)
        {
            json = Game.World.GetBankItemsJson().Json;
            return true;
        }
        json = null;
        return false;
    }

    /// <summary>
    /// <see cref="IFlashUtil.GetGameObject{T}"/>: the item lists from the caches described above, anything else as the
    /// interface does (getGameObject, then Newtonsoft, the default on any failure).
    /// </summary>
    public T? GetGameObject<T>(string path, T? def = default)
    {
        try
        {
            if (IsItemListPath(path) && Game.IsInGame && FlatList<T>.Element is Type element)
            {
                // As Call would: a stopping script's call is cancelled, which this method answers with the default.
                if (CancelsScriptCall())
                    return def;
                return (T?)FlatList<T>.Copy!(CachedList(path, element));
            }
            return JsonConvert.DeserializeObject<T>(((IFlashUtil)this).GetGameObject(path)!) ?? def;
        }
        catch
        {
            return def;
        }
    }

    /// <inheritdoc/>
    public ItemListSnapshot<T>? GetItemListSnapshot<T>(string path) where T : ItemBase
    {
        if (!IsItemListPath(path) || !Game.IsInGame || FlatList<List<T>>.Element is null)
            return null;
        // What the list read gives a stopping script (the services' default, an empty list).
        if (CancelsScriptCall())
            return ItemListSnapshot<T>.Empty;
        try
        {
            lock (_listGate)
            {
                ListCache entry = Entry(path, typeof(T));
                return (ItemListSnapshot<T>)(entry.Snapshot ??= new ItemListSnapshot<T>((List<T?>)entry.Items!));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log?.Invoke($"Item list {path} could not be read: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Memory pressure: drops the parsed lists while no script runs (the next read parses again). While one runs they
    /// are its working set: dropping them would only trade a few MB for parsing everything again at its next lookup.
    /// </summary>
    public void TrimCaches()
    {
        if (_manager?.Value.ScriptRunning == true)
            return;
        lock (_listGate)
            _lists.Clear();
    }

    private IList CachedList(string path, Type element)
    {
        lock (_listGate)
            return Entry(path, element).Items!;
    }

    // Under _listGate.
    private ListCache Entry(string path, Type element)
    {
        if (!_lists.TryGetValue((path, element), out ListCache? entry))
            _lists[(path, element)] = entry = new ListCache();
        if (path == BankItemsPath)
        {
            RefreshBank(entry, element);
            return entry;
        }
        if (path == InventoryItemsPath && typeof(ItemBase).IsAssignableFrom(element))
        {
            RefreshInventory(entry, element);
            return entry;
        }
        object key = path switch
        {
            InventoryItemsPath => Game.World.InventoryKey,
            TempItemsPath => Game.World.TempInventoryVersion,
            _ => Game.World.HouseInventoryVersion,
        };
        if (entry.Items is not null && Equals(entry.Key, key))
            return entry;
        // The text getGameObject gives Skua (the key was read first: the list is at least as new as the key).
        Lookup l = Resolve(path);
        string? text = As3.Stringify(l.Node, l.Defined);
        Type listType = typeof(List<>).MakeGenericType(element);
        IList? parsed = text is null ? null : (IList?)JsonConvert.DeserializeObject(text, listType);
        entry.Items = parsed ?? (IList)Activator.CreateInstance(listType)!;
        entry.Key = key;
        entry.Snapshot = null;
        Interlocked.Increment(ref _listParses);
        return entry;
    }

    // Under _listGate. The backpack: parsed when its version changed; the client shows the class points as the equipped
    // class's quantity (GameClient.DecorateItems), and they change with every kill, so only that item is redone for
    // new class points (a copy with the new quantity, which is what parsing the decorated list gives).
    private void RefreshInventory(ListCache entry, Type element)
    {
        (long version, long classPoints) = Game.World.InventoryKey;
        if (entry.Raw is null || !Equals(entry.Key, version))
        {
            Game.World.TryGetGameObjectNode(InventoryItemsPath, out JsonNode? node);
            JsonArray array = node as JsonArray ?? new JsonArray();
            List<int> classItems = new();
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonObject o && Skua.Core.Headless.Client.GameClient.ShowsClassPoints(o))
                    classItems.Add(i);
            }
            Type listType = typeof(List<>).MakeGenericType(element);
            entry.Raw = (IList?)JsonConvert.DeserializeObject(array.ToJsonString(), listType) ?? (IList)Activator.CreateInstance(listType)!;
            entry.ClassItems = classItems.ToArray();
            entry.Key = version;
            entry.Items = null;
            Interlocked.Increment(ref _listParses);
        }
        if (entry.Items is not null && entry.ClassPoints == classPoints)
            return;
        IList items = entry.Raw;
        if (classPoints != long.MinValue && entry.ClassItems!.Length > 0)
        {
            items = (IList)Activator.CreateInstance(entry.Raw.GetType(), entry.Raw)!;
            foreach (int i in entry.ClassItems)
            {
                if (items[i] is not ItemBase item)
                    continue;
                ItemBase copy = ItemListSnapshot<ItemBase>.Copy(item)!;
                // As Newtonsoft reads a number too large for the int (it fails, and the read answers the default).
                copy.Quantity = checked((int)classPoints);
                items[i] = copy;
            }
        }
        entry.Items = items;
        entry.ClassPoints = classPoints;
        entry.Snapshot = null;
    }

    // Under _listGate. The bank, rebuilt when its version changed, parsing only the items whose stored object is new.
    private void RefreshBank(ListCache entry, Type element)
    {
        long version = Game.World.BankVersion;
        if (entry.Items is not null && Equals(entry.Key, version))
            return;
        Dictionary<object, object?> previous = entry.ByObject ?? new(ReferenceEqualityComparer.Instance);
        BankItemsRead read = Game.World.ReadBankItems(entry.Generation, previous.ContainsKey);
        bool sameGeneration = read.Generation == entry.Generation;
        Dictionary<object, object?> byObject = new(read.Keys.Count, ReferenceEqualityComparer.Instance);
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element), read.Keys.Count)!;
        long parsed = 0;
        for (int i = 0; i < read.Keys.Count; i++)
        {
            object key = read.Keys[i];
            object? item;
            if (read.Json[i] is string json)
            {
                item = JsonConvert.DeserializeObject(json, element);
                parsed++;
            }
            else if (sameGeneration && previous.TryGetValue(key, out object? known))
            {
                item = known;
            }
            else
            {
                throw new InvalidOperationException("The bank read returned no text for an item the cache does not hold.");
            }
            byObject[key] = item;
            list.Add(item);
        }
        entry.Items = list;
        entry.ByObject = byObject;
        entry.Generation = read.Generation;
        entry.Key = read.Version;
        entry.Snapshot = null;
        Interlocked.Increment(ref _bankParses);
        Interlocked.Add(ref _bankItemParses, parsed);
    }

    /// <summary>For List&lt;E&gt; of a flat class E: E and a function giving a new list of memberwise copies; else null.</summary>
    private static class FlatList<T>
    {
        public static readonly Type? Element;
        public static readonly Func<IList, object>? Copy;

        static FlatList()
        {
            Type t = typeof(T);
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(List<>))
                return;
            Type element = t.GetGenericArguments()[0];
            if (!element.IsClass || element == typeof(string) || !IsFlat(element))
                return;
            MethodInfo? memberwise = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
            if (memberwise is null)
                return;
            Func<object, object> clone;
            try
            {
                clone = memberwise.CreateDelegate<Func<object, object>>();
            }
            catch
            {
                return;
            }
            Copy = source =>
            {
                IList list = (IList)Activator.CreateInstance(t, source.Count)!;
                foreach (object? item in source)
                    list.Add(item is null ? null : clone(item));
                return list;
            };
            Element = element;
        }

        private static bool IsFlat(Type type)
        {
            for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    Type ft = Nullable.GetUnderlyingType(f.FieldType) ?? f.FieldType;
                    if (!(ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft == typeof(decimal) || ft == typeof(DateTime) || ft == typeof(DateTimeOffset) || ft == typeof(TimeSpan) || ft == typeof(Guid)))
                        return false;
                }
            }
            return true;
        }
    }
}
