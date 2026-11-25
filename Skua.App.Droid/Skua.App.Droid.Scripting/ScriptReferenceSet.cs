using Skua.Core.Scripts;

namespace Skua.App.Droid.Scripting;

/// <summary>Where the script reference images come from: APK assets on Android, a folder on Linux.</summary>
public interface IScriptReferenceSource
{
    /// <summary>File names (not paths) in <paramref name="folder"/> ("framework" or "app").</summary>
    IReadOnlyList<string> List(string folder);

    /// <summary>Opens "folder/file".</summary>
    Stream Open(string relativePath);

    /// <summary>The build-time manifest ("folder/file" per line), or null if missing.</summary>
    string? ReadManifest();
}

/// <summary>Reads the reference images from a directory laid out like the asset folder (tests, desktop).</summary>
public sealed class DirectoryScriptReferenceSource : IScriptReferenceSource
{
    private readonly string _root;

    public DirectoryScriptReferenceSource(string root)
    {
        _root = root;
    }

    public IReadOnlyList<string> List(string folder)
    {
        string dir = Path.Combine(_root, folder);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.dll").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];
    }

    public Stream Open(string relativePath) => File.OpenRead(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public string? ReadManifest()
    {
        string path = Path.Combine(_root, ScriptReferenceSet.ManifestName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}

/// <summary>
/// The base references for compiling scripts on the device: the net10.0 reference pack plus Skua's own assemblies
/// and the WinForms shim, read as streams (only when a compilation binds them) and handed to Skua.Core through
/// <see cref="LazyScriptReferenceProvider"/>.
/// </summary>
public sealed class ScriptReferenceSet
{
    /// <summary>Asset folder (below the APK's assets/) holding the images.</summary>
    public const string AssetRoot = "script-refs";

    public const string ManifestName = "manifest.txt";

    /// <summary>Assemblies without which no community script compiles; checked at start-up.</summary>
    public static IReadOnlyList<string> Required { get; } =
    [
        "framework/System.Runtime.dll",
        "framework/netstandard.dll",
        "framework/System.Collections.dll",
        "framework/System.Linq.dll",
        "app/Skua.Core.dll",
        "app/Skua.Core.Interfaces.dll",
        "app/Skua.Core.Models.dll",
        "app/Skua.Core.Utils.dll",
        "app/Skua.Compat.WinForms.dll",
        "app/CommunityToolkit.Mvvm.dll",
        "app/Newtonsoft.Json.dll",
    ];

    private ScriptReferenceSet(IReadOnlyList<string> files, IReadOnlyList<string> problems, LazyScriptReferenceProvider provider, string identity)
    {
        Files = files;
        Problems = problems;
        Provider = provider;
        Identity = identity;
    }

    /// <summary>"folder/file" for every image found.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>Missing required or manifest-listed images; empty when the set is complete.</summary>
    public IReadOnlyList<string> Problems { get; }

    public bool IsComplete => Problems.Count == 0;

    /// <summary>Identifies this set and the Skua.Core it compiles with (compared by the compiler process).</summary>
    public string Identity { get; }

    /// <summary>What to register as Skua.Core's <c>IScriptReferenceProvider</c>.</summary>
    public LazyScriptReferenceProvider Provider { get; }

    /// <summary>What to register as Skua.Core's <c>IScriptNamespaceProvider</c>: the desktop list, unchanged.</summary>
    public static IScriptNamespaceProvider Namespaces => ScriptNamespaceProvider.Desktop;

    /// <summary>Lists the images and checks them against the manifest and <see cref="Required"/>. Images are read lazily, on first compile.</summary>
    public static ScriptReferenceSet Load(IScriptReferenceSource source)
    {
        List<string> files = new();
        foreach (string folder in new[] { "framework", "app" })
            files.AddRange(source.List(folder).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(f => $"{folder}/{f}"));

        HashSet<string> present = new(files, StringComparer.Ordinal);
        List<string> problems = new();
        foreach (string required in Required)
        {
            if (!present.Contains(required))
                problems.Add($"missing {required}");
        }
        string? manifest = source.ReadManifest();
        if (manifest is null)
        {
            problems.Add("missing manifest");
        }
        else
        {
            foreach (string line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!present.Contains(line))
                    problems.Add($"missing {line} (listed in the manifest)");
            }
        }

        LazyScriptReferenceProvider provider = new(files.Select(f =>
            new KeyValuePair<string, Func<Stream>>(f[(f.IndexOf('/') + 1)..], () => source.Open(f))));
        // Same images listed and the same Skua.Core loaded (same APK): two processes compile identically.
        string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join("\n", files) + "\n#\n" + manifest + "\n#\n" + typeof(ScriptManager).Assembly.ManifestModule.ModuleVersionId.ToString("N"))))[..32];
        return new ScriptReferenceSet(files, problems, provider, identity);
    }
}
