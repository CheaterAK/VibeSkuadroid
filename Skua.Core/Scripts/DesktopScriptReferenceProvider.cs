using Microsoft.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.PortableExecutable;

namespace Skua.Core.Scripts;

/// <summary>
/// Discovers references from assembly file locations: the loaded assemblies, the trusted platform assemblies,
/// the runtime directory and, on Windows, the Microsoft.WindowsDesktop.App directory.
/// </summary>
/// <remarks>
/// This is the default for hosts that register no <see cref="IScriptReferenceProvider"/>. It needs assemblies
/// with a file location, so it finds nothing useful where assemblies are loaded from memory or an archive.
/// The discovery runs once per process, on first use.
/// </remarks>
public sealed class DesktopScriptReferenceProvider : IScriptReferenceProvider
{
    private static List<PortableExecutableReference>? _cachedBaseReferences;
    private static readonly object _referenceCacheLock = new();

    /// <summary>
    /// The shared instance used when the host registers no provider.
    /// </summary>
    public static DesktopScriptReferenceProvider Shared { get; } = new();

    public IReadOnlyList<PortableExecutableReference> GetReferences()
    {
        if (_cachedBaseReferences == null)
        {
            lock (_referenceCacheLock)
            {
                if (_cachedBaseReferences == null)
                {
                    Dictionary<string, string> assemblyMap = new(StringComparer.OrdinalIgnoreCase);

                    void AddAssemblyPath(string? path)
                    {
                        if (string.IsNullOrEmpty(path) || !File.Exists(path))
                            return;

                        string fileName = Path.GetFileName(path);
                        if (assemblyMap.ContainsKey(fileName))
                            return;

                        if (IsManagedAssembly(path))
                            assemblyMap[fileName] = path;
                    }

                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location) && !asm.Location.Contains("xunit"))
                        {
                            AddAssemblyPath(asm.Location);
                        }
                    }

                    if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
                    {
                        foreach (string path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                        {
                            AddAssemblyPath(path);
                        }
                    }

                    string? runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
                    if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
                    {
                        foreach (string dll in Directory.EnumerateFiles(runtimeDir, "*.dll"))
                        {
                            AddAssemblyPath(dll);
                        }

                        if (OperatingSystem.IsWindows())
                        {
                            string? dotnetShared = Path.GetDirectoryName(runtimeDir);
                            string? dotnetSharedRoot = Path.GetDirectoryName(dotnetShared);
                            string versionDir = Path.GetFileName(runtimeDir);
                            if (!string.IsNullOrEmpty(dotnetSharedRoot))
                            {
                                string desktopDir = Path.Combine(dotnetSharedRoot, "Microsoft.WindowsDesktop.App", versionDir);
                                if (Directory.Exists(desktopDir))
                                {
                                    foreach (string dll in Directory.EnumerateFiles(desktopDir, "*.dll"))
                                    {
                                        AddAssemblyPath(dll);
                                    }
                                }
                            }
                        }
                    }

                    string[] refPaths = {
                        typeof(object).GetTypeInfo().Assembly.Location,
                        typeof(Console).GetTypeInfo().Assembly.Location,
                        typeof(object).Assembly.Location,
                        typeof(Enumerable).Assembly.Location,
                        typeof(ScriptManager).Assembly.Location,
                        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
                        typeof(ZipArchive).Assembly.Location,
                    };

                    foreach (string path in refPaths)
                    {
                        AddAssemblyPath(path);
                    }

                    List<PortableExecutableReference> refs = new(assemblyMap.Count);
                    foreach (string path in assemblyMap.Values)
                    {
                        try
                        {
                            refs.Add(MetadataReference.CreateFromFile(path));
                        }
                        catch
                        {
                        }
                    }

                    _cachedBaseReferences = refs;
                }
            }
        }

        return _cachedBaseReferences;
    }

    private static bool IsManagedAssembly(string filePath)
    {
        try
        {
            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 64)
                return false;

            using PEReader peReader = new(fs);
            return peReader.HasMetadata;
        }
        catch
        {
            return false;
        }
    }
}
