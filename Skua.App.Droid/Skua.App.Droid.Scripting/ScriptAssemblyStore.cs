using Skua.Core;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace Skua.App.Droid.Scripting;

/// <summary>
/// Loads script and include assemblies at most once per process, keyed by the SHA-256 of their image and the
/// identity of the script assemblies it binds to.
/// </summary>
/// <remarks>
/// <para>
/// Why: on Mono/Android a collectible <see cref="AssemblyLoadContext"/> is never collected (measured on a Galaxy
/// S21 Ultra, .NET 10.0.15): every load of a CoreBots-sized assembly keeps about 10 MB until the process dies.
/// Skua.Core's <c>ScriptManager</c> creates a new <c>ScriptLoadContext</c> per run and loads the cached include
/// images into it again, so on Android each run would leak the whole include set. With this store an unchanged
/// include is loaded once, a changed one adds one copy, and re-running the same script costs nothing.
/// </para>
/// <para>
/// Each image gets its own non-collectible context (one context cannot hold two assemblies with the same simple
/// name, and an edited CoreBots must be able to sit next to the old one). A context resolves the dependencies it
/// was given (the include images the script was compiled against) and falls back to the default context for the
/// framework and Skua. The contexts are <see cref="ScriptLoadContext"/>s, so Skua's checks for "a frame of a
/// script is on the stack" (the headless Flash bridge stops a cancelled script that way) see them as scripts.
/// </para>
/// <para>
/// The key includes the dependencies an image actually references: an include whose source did not change but which
/// is recompiled against a changed CoreBots can produce a byte-identical image (references are by name), and it must
/// bind to the new CoreBots, not reuse the copy bound to the old one.
/// </para>
/// <para>
/// Consequence the host must accept or handle: a reused assembly keeps its static state between runs (on desktop
/// every run starts with fresh statics because the context is recreated).
/// </para>
/// </remarks>
public sealed class ScriptAssemblyStore
{
    private readonly ConcurrentDictionary<string, Lazy<Entry>> _byKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Assembly> _byImage = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Assembly, string> _keys = new();
    private long _totalBytes;

    /// <summary>Distinct images loaded into this process.</summary>
    public int Count => _byKey.Count;

    /// <summary>Total size of the distinct images loaded (a lower bound of the memory they pin).</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    /// <summary>Raised after a new image was actually loaded (not for cache hits).</summary>
    public event EventHandler<Assembly>? Loaded;

    /// <summary>
    /// Returns the already loaded assembly for this exact image (bound to the same dependencies), or loads it.
    /// </summary>
    /// <param name="image">The PE image (as emitted by Roslyn or read from the compile cache).</param>
    /// <param name="dependencies">Script/include assemblies this one may reference, by simple name. Only the ones the
    /// image references are used.</param>
    public Assembly GetOrLoad(byte[] image, IReadOnlyDictionary<string, Assembly>? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        string imageHash = Convert.ToHexString(SHA256.HashData(image));
        Dictionary<string, Assembly> used = SelectReferenced(image, dependencies);
        string key = imageHash + DependencyKey(used);
        bool created = false;
        Lazy<Entry> lazy = _byKey.GetOrAdd(key, _ => new Lazy<Entry>(() =>
        {
            created = true;
            ImageContext context = new($"script-{imageHash[..12]}", used);
            using MemoryStream stream = new(image, writable: false);
            Assembly assembly = context.LoadFromStream(stream);
            Interlocked.Add(ref _totalBytes, image.Length);
            return new Entry(assembly, key);
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        Entry entry;
        try
        {
            entry = lazy.Value;
        }
        catch
        {
            // Do not cache a failed load: a later attempt may succeed (for example once a dependency is available).
            _byKey.TryRemove(new KeyValuePair<string, Lazy<Entry>>(key, lazy));
            throw;
        }
        if (created)
        {
            _keys.AddOrUpdate(entry.Assembly, entry.Key);
            _byImage.TryAdd(imageHash, entry.Assembly);
            Loaded?.Invoke(this, entry.Assembly);
        }
        return entry.Assembly;
    }

    /// <summary>Loads (or reuses) an image read from a file, such as Skua.Core's compiled-script disk cache.</summary>
    public Assembly GetOrLoadFile(string path, IReadOnlyDictionary<string, Assembly>? dependencies = null) =>
        GetOrLoad(File.ReadAllBytes(path), dependencies);

    /// <summary>A loaded assembly for this image (the first one loaded, whatever it was bound to), if any.</summary>
    public bool TryGet(byte[] image, out Assembly? assembly)
    {
        string hash = Convert.ToHexString(SHA256.HashData(image));
        return _byImage.TryGetValue(hash, out assembly);
    }

    /// <summary>The simple names of the assemblies <paramref name="image"/> references, and its own name.</summary>
    public static (string Name, IReadOnlyList<string> References) ReadIdentity(byte[] image)
    {
        using PEReader pe = new(new MemoryStream(image, writable: false));
        MetadataReader md = pe.GetMetadataReader();
        List<string> references = new();
        foreach (AssemblyReferenceHandle h in md.AssemblyReferences)
            references.Add(md.GetString(md.GetAssemblyReference(h).Name));
        return (md.GetString(md.GetAssemblyDefinition().Name), references);
    }

    private static Dictionary<string, Assembly> SelectReferenced(byte[] image, IReadOnlyDictionary<string, Assembly>? dependencies)
    {
        Dictionary<string, Assembly> used = new(StringComparer.OrdinalIgnoreCase);
        if (dependencies is null || dependencies.Count == 0)
            return used;
        foreach (string name in ReadIdentity(image).References)
        {
            if (dependencies.TryGetValue(name, out Assembly? dependency))
                used[name] = dependency;
        }
        return used;
    }

    private string DependencyKey(Dictionary<string, Assembly> used)
    {
        if (used.Count == 0)
            return string.Empty;
        StringBuilder sb = new();
        foreach (KeyValuePair<string, Assembly> d in used.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            string identity = _keys.TryGetValue(d.Value, out string? k)
                ? k
                : $"{d.Value.ManifestModule.ModuleVersionId:N}@{AssemblyLoadContext.GetLoadContext(d.Value)?.Name}";
            sb.Append('|').Append(d.Key).Append('=').Append(identity);
        }
        return "+" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private sealed record Entry(Assembly Assembly, string Key);

    private sealed class ImageContext : ScriptLoadContext
    {
        private readonly IReadOnlyDictionary<string, Assembly> _dependencies;

        public ImageContext(string name, IReadOnlyDictionary<string, Assembly> dependencies)
            : base(name, isCollectible: false)
        {
            _dependencies = dependencies;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is string name && _dependencies.TryGetValue(name, out Assembly? dependency))
                return dependency;
            return null; // default context: framework, Skua.Core and everything else the app ships
        }
    }
}

/// <summary>
/// The <see cref="ScriptLoadContext"/> Skua.Core's ScriptManager uses on Android (set as its
/// <c>LoadContextFactory</c>): every script and include assembly the compiler or the manager loads goes through
/// <see cref="ScriptAssemblyStore"/>, so an unchanged image is never loaded twice in the process. Nothing is loaded
/// into this context itself; one instance serves every compile and forgets the previous compile's names on
/// <see cref="BeginCompile"/>.
/// </summary>
/// <remarks>
/// Resolution follows the desktop <see cref="ScriptLoadContext"/>: a referenced name the default context can load is
/// left to it; otherwise the include loaded (or registered) for this compile, else the newest compiled file of that
/// name in the disk cache.
/// </remarks>
public sealed class StoreScriptLoadContext : ScriptLoadContext
{
    private readonly ScriptAssemblyStore _store;
    private readonly object _gate = new();
    private Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public StoreScriptLoadContext(ScriptAssemblyStore store)
        : base("droid-script-runs", isCollectible: false)
    {
        _store = store;
    }

    /// <summary>The store behind this context.</summary>
    public ScriptAssemblyStore Store => _store;

    /// <summary>Starts a compile: forgets the assemblies the previous compile bound by name.</summary>
    public StoreScriptLoadContext BeginCompile()
    {
        lock (_gate)
            _loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        return this;
    }

    public override Assembly LoadScriptAssembly(string path)
    {
        lock (_gate)
            return LoadImage(File.ReadAllBytes(path), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private Assembly LoadImage(byte[] image, HashSet<string> visiting)
    {
        (string self, IReadOnlyList<string> references) = ScriptAssemblyStore.ReadIdentity(image);
        visiting.Add(self);
        Dictionary<string, Assembly> dependencies = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in references)
        {
            if (_loaded.TryGetValue(name, out Assembly? known))
            {
                dependencies[name] = known;
                continue;
            }
            if (visiting.Contains(name) || ResolvesInDefault(name))
                continue;
            string? path = FindScriptAssemblyPath(name);
            if (path is not null)
                dependencies[name] = LoadImage(File.ReadAllBytes(path), visiting);
        }
        Assembly assembly = _store.GetOrLoad(image, dependencies);
        _loaded[self] = assembly;
        return assembly;
    }

    private static bool ResolvesInDefault(string name)
    {
        try
        {
            Default.LoadFromAssemblyName(new AssemblyName(name));
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return false;
        }
    }

    // Nothing is ever loaded into this context; references of store-loaded images resolve in their own contexts.
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
