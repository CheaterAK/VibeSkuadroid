using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Reflection.PortableExecutable;

namespace Skua.Core.Scripts;

/// <summary>
/// Builds references from a host-supplied set of assembly images, for hosts where assemblies have no usable
/// file location (for example reference assemblies shipped as Android assets).
/// </summary>
/// <remarks>
/// Each source is opened once, on first use, and copied into memory; the stream is disposed afterwards.
/// Sources are de-duplicated by name (case-insensitive, first one wins). A source that fails to open or has no
/// CLI metadata is skipped, the same way the desktop discovery skips unreadable files.
/// </remarks>
public sealed class StreamScriptReferenceProvider : IScriptReferenceProvider
{
    private readonly Lazy<IReadOnlyList<PortableExecutableReference>> _references;

    /// <param name="sources">
    /// The assembly images, each as a name (typically the file name, used for de-duplication and diagnostics)
    /// and a factory that opens a readable stream over the image.
    /// </param>
    public StreamScriptReferenceProvider(IEnumerable<KeyValuePair<string, Func<Stream>>> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        KeyValuePair<string, Func<Stream>>[] snapshot = sources.ToArray();
        _references = new Lazy<IReadOnlyList<PortableExecutableReference>>(() => Load(snapshot), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Creates a provider over in-memory assembly images.
    /// </summary>
    public static StreamScriptReferenceProvider FromImages(IEnumerable<KeyValuePair<string, byte[]>> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        return new StreamScriptReferenceProvider(images.Select(image =>
            new KeyValuePair<string, Func<Stream>>(image.Key, () => new MemoryStream(image.Value, writable: false))));
    }

    /// <summary>
    /// Creates a provider over assembly files on disk.
    /// </summary>
    public static StreamScriptReferenceProvider FromFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new StreamScriptReferenceProvider(paths.Select(path =>
            new KeyValuePair<string, Func<Stream>>(Path.GetFileName(path), () => File.OpenRead(path))));
    }

    public IReadOnlyList<PortableExecutableReference> GetReferences() => _references.Value;

    private static IReadOnlyList<PortableExecutableReference> Load(KeyValuePair<string, Func<Stream>>[] sources)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<PortableExecutableReference> refs = new(sources.Length);
        foreach ((string name, Func<Stream> open) in sources)
        {
            if (string.IsNullOrEmpty(name) || open is null || !seen.Add(name))
                continue;

            try
            {
                ImmutableArray<byte> image;
                using (Stream stream = open())
                {
                    using MemoryStream buffer = new();
                    stream.CopyTo(buffer);
                    image = ImmutableArray.Create(buffer.GetBuffer(), 0, (int)buffer.Length);
                }

                if (!HasMetadata(image))
                {
                    seen.Remove(name);
                    continue;
                }

                refs.Add(MetadataReference.CreateFromImage(image, filePath: name));
            }
            catch
            {
                seen.Remove(name);
            }
        }

        return refs;
    }

    private static bool HasMetadata(ImmutableArray<byte> image)
    {
        if (image.Length < 64)
            return false;

        try
        {
            using PEReader peReader = new(image);
            return peReader.HasMetadata;
        }
        catch
        {
            return false;
        }
    }
}
