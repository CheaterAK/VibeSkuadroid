using Newtonsoft.Json;
using Skua.Core.Interfaces;
using System.Collections;
using System.Reflection;

namespace Skua.Core.Headless.Flash;

// Skua reads the bank (ScriptBank.Items, [ObjectBinding("world.bankinfo.items")]) on every Bank.Contains, TryGetItem
// and GetItem: getGameObject, then Newtonsoft into a fresh List<InventoryItem>. With a 3000-item bank (the measured
// account) that was 27 MB allocated and ~40 ms per read on a desktop CPU, several reads per kill in CoreBots; on the
// phone it drove the managed heap past 1 GB and the collector into >10 s pauses (ANR). The bank changes rarely, so:
// the JSON text is built once per WorldModel.BankVersion, parsed once per version and element type, and each read
// gets its own list of its own copies (the element types are flat: value fields and strings only, so a memberwise
// copy is what a fresh parse would give).
public sealed partial class HeadlessFlashUtil
{
    /// <summary>The path Skua's bank binding reads.</summary>
    public const string BankItemsPath = "world.bankinfo.items";

    private readonly object _bankCacheGate = new();
    private long _bankCacheVersion = -1;
    private Type? _bankCacheType;
    private IList? _bankCacheItems;
    private int _bankParses;

    /// <summary>How many times the bank was parsed for a typed read (tests: once per bank version and type).</summary>
    public int BankParses => Volatile.Read(ref _bankParses);

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
    /// <see cref="IFlashUtil.GetGameObject{T}"/>: the bank path from the cache described above, anything else as the
    /// interface does (getGameObject, then Newtonsoft, the default on any failure).
    /// </summary>
    public T? GetGameObject<T>(string path, T? def = default)
    {
        try
        {
            if (path == BankItemsPath && Game.IsInGame && FlatList<T>.Copy is Func<IList, object> copy)
            {
                // As Call would: a stopping script's call is cancelled, which this method answers with the default.
                if (CancelsScriptCall())
                    return def;
                return (T?)copy(BankList<T>());
            }
            return JsonConvert.DeserializeObject<T>(((IFlashUtil)this).GetGameObject(path)!) ?? def;
        }
        catch
        {
            return def;
        }
    }

    /// <summary>Drops the parsed bank (it is rebuilt from the model on the next read). For memory pressure.</summary>
    public void TrimCaches()
    {
        lock (_bankCacheGate)
        {
            _bankCacheItems = null;
            _bankCacheType = null;
            _bankCacheVersion = -1;
        }
    }

    private IList BankList<T>()
    {
        (long version, string json) = Game.World.GetBankItemsJson();
        lock (_bankCacheGate)
        {
            if (_bankCacheItems is null || _bankCacheVersion != version || _bankCacheType != typeof(T))
            {
                _bankCacheItems = (IList?)JsonConvert.DeserializeObject<T>(json) ?? (IList)Activator.CreateInstance(typeof(T))!;
                _bankCacheVersion = version;
                _bankCacheType = typeof(T);
                Interlocked.Increment(ref _bankParses);
            }
            return _bankCacheItems;
        }
    }

    /// <summary>For List&lt;E&gt; of a flat class E: a function giving a new list of memberwise copies; else null.</summary>
    private static class FlatList<T>
    {
        public static readonly Func<IList, object>? Copy = Build();

        private static Func<IList, object>? Build()
        {
            Type t = typeof(T);
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(List<>))
                return null;
            Type element = t.GetGenericArguments()[0];
            if (!element.IsClass || element == typeof(string) || !IsFlat(element))
                return null;
            MethodInfo? memberwise = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
            if (memberwise is null)
                return null;
            Func<object, object> clone;
            try
            {
                clone = memberwise.CreateDelegate<Func<object, object>>();
            }
            catch
            {
                return null;
            }
            return source =>
            {
                IList list = (IList)Activator.CreateInstance(t, source.Count)!;
                foreach (object? item in source)
                    list.Add(item is null ? null : clone(item));
                return list;
            };
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
