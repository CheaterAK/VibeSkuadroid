using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace Skua.App.Droid.Scripting;

/// <summary>
/// Loads script and include assemblies at most once per process, keyed by the SHA-256 of their image.
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
/// framework and Skua.
/// </para>
/// <para>
/// Consequence the host must accept or handle: a reused assembly keeps its static state between runs (on desktop
/// every run starts with fresh statics because the context is recreated).
/// </para>
/// </remarks>
public sealed class ScriptAssemblyStore
{
    private readonly ConcurrentDictionary<string, Lazy<Entry>> _byHash = new(StringComparer.Ordinal);
    private long _totalBytes;

    /// <summary>Distinct images loaded into this process.</summary>
    public int Count => _byHash.Count;

    /// <summary>Total size of the distinct images loaded (a lower bound of the memory they pin).</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    /// <summary>Raised after a new image was actually loaded (not for cache hits).</summary>
    public event EventHandler<Assembly>? Loaded;

    /// <summary>
    /// Returns the already loaded assembly for this exact image, or loads it.
    /// </summary>
    /// <param name="image">The PE image (as emitted by Roslyn or read from the compile cache).</param>
    /// <param name="dependencies">Script/include assemblies this one references, by simple name.</param>
    public Assembly GetOrLoad(byte[] image, IReadOnlyDictionary<string, Assembly>? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        string hash = Convert.ToHexString(SHA256.HashData(image));
        bool created = false;
        Lazy<Entry> lazy = _byHash.GetOrAdd(hash, _ => new Lazy<Entry>(() =>
        {
            created = true;
            ImageContext context = new($"script-{hash[..12]}", dependencies);
            using MemoryStream stream = new(image, writable: false);
            Assembly assembly = context.LoadFromStream(stream);
            Interlocked.Add(ref _totalBytes, image.Length);
            return new Entry(assembly, hash);
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        Entry entry;
        try
        {
            entry = lazy.Value;
        }
        catch
        {
            // Do not cache a failed load: a later attempt may succeed (for example once a dependency is available).
            _byHash.TryRemove(new KeyValuePair<string, Lazy<Entry>>(hash, lazy));
            throw;
        }
        if (created)
            Loaded?.Invoke(this, entry.Assembly);
        return entry.Assembly;
    }

    /// <summary>Loads (or reuses) an image read from a file, such as Skua.Core's compiled-script disk cache.</summary>
    public Assembly GetOrLoadFile(string path, IReadOnlyDictionary<string, Assembly>? dependencies = null) =>
        GetOrLoad(File.ReadAllBytes(path), dependencies);

    /// <summary>The loaded assembly for this image hash, if any.</summary>
    public bool TryGet(byte[] image, out Assembly? assembly)
    {
        string hash = Convert.ToHexString(SHA256.HashData(image));
        if (_byHash.TryGetValue(hash, out Lazy<Entry>? lazy) && lazy.IsValueCreated)
        {
            assembly = lazy.Value.Assembly;
            return true;
        }
        assembly = null;
        return false;
    }

    private sealed record Entry(Assembly Assembly, string Hash);

    private sealed class ImageContext : AssemblyLoadContext
    {
        private readonly IReadOnlyDictionary<string, Assembly> _dependencies;

        public ImageContext(string name, IReadOnlyDictionary<string, Assembly>? dependencies)
            : base(name, isCollectible: false)
        {
            _dependencies = dependencies ?? new Dictionary<string, Assembly>();
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is string name && _dependencies.TryGetValue(name, out Assembly? dependency))
                return dependency;
            return null; // default context: framework, Skua.Core and everything else the app ships
        }
    }
}
