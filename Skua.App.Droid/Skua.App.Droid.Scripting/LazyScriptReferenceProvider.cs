using Microsoft.CodeAnalysis;
using Skua.Core.Scripts;
using System.Collections.Immutable;

namespace Skua.App.Droid.Scripting;

/// <summary>
/// The base references as <see cref="PortableExecutableReference"/>s that read their image only when a compilation
/// binds them, and keep what they read only while a compilation uses it.
/// </summary>
/// <remarks>
/// Skua.Core asks for the base references whenever it creates a compiler, also for a start whose script and includes
/// are all in the compiled-script cache (no compilation happens). Skua.Core's <see cref="StreamScriptReferenceProvider"/>
/// reads every image on that first call and keeps them, and the metadata Roslyn builds over them, for the whole process
/// (measured on Linux: 7.5 MB retained after a cached start, 21 MB after a cold CoreBots compile). Here a cached start
/// reads nothing, and after a compile the images and their metadata are collectable (weak cache): the engine process,
/// which only loads compiled scripts, carries none of it.
/// </remarks>
public sealed class LazyScriptReferenceProvider : IScriptReferenceProvider
{
    private readonly IReadOnlyList<PortableExecutableReference> _references;
    private int _loads;

    /// <param name="sources">The images by name (file name; duplicates by name are dropped, first one wins) and how to open each.</param>
    public LazyScriptReferenceProvider(IEnumerable<KeyValuePair<string, Func<Stream>>> sources)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<PortableExecutableReference> refs = new();
        foreach ((string name, Func<Stream> open) in sources)
        {
            if (string.IsNullOrEmpty(name) || open is null || !seen.Add(name))
                continue;
            refs.Add(new LazyImageReference(new ImageSource(name, open, this), MetadataReferenceProperties.Assembly));
        }
        _references = refs;
    }

    /// <summary>Images read so far (each read is one open of the asset; a collected image is read again when needed).</summary>
    public int Loads => Volatile.Read(ref _loads);

    /// <summary>Images whose metadata is in memory right now.</summary>
    public int Resident => _references.Count(r => ((LazyImageReference)r).Source.IsResident);

    public IReadOnlyList<PortableExecutableReference> GetReferences() => _references;

    private sealed class ImageSource
    {
        private readonly Func<Stream> _open;
        private readonly LazyScriptReferenceProvider _owner;
        private readonly object _gate = new();
        private WeakReference<AssemblyMetadata>? _metadata;

        public ImageSource(string name, Func<Stream> open, LazyScriptReferenceProvider owner)
        {
            Name = name;
            _open = open;
            _owner = owner;
        }

        public string Name { get; }

        public bool IsResident
        {
            get
            {
                lock (_gate)
                    return _metadata is not null && _metadata.TryGetTarget(out _);
            }
        }

        public AssemblyMetadata Get()
        {
            lock (_gate)
            {
                if (_metadata is not null && _metadata.TryGetTarget(out AssemblyMetadata? alive))
                    return alive;
                ImmutableArray<byte> image;
                using (Stream stream = _open())
                {
                    using MemoryStream buffer = new(stream.CanSeek ? (int)stream.Length : 0);
                    stream.CopyTo(buffer);
                    image = ImmutableArray.Create(buffer.GetBuffer(), 0, (int)buffer.Length);
                }
                AssemblyMetadata metadata = AssemblyMetadata.CreateFromImage(image);
                _metadata = new WeakReference<AssemblyMetadata>(metadata);
                Interlocked.Increment(ref _owner._loads);
                return metadata;
            }
        }
    }

    private sealed class LazyImageReference : PortableExecutableReference
    {
        public LazyImageReference(ImageSource source, MetadataReferenceProperties properties)
            : base(properties, source.Name)
        {
            Source = source;
        }

        public ImageSource Source { get; }

        protected override DocumentationProvider CreateDocumentationProvider() => DocumentationProvider.Default;

        protected override Metadata GetMetadataImpl() => Source.Get();

        protected override PortableExecutableReference WithPropertiesImpl(MetadataReferenceProperties properties) => new LazyImageReference(Source, properties);
    }
}
