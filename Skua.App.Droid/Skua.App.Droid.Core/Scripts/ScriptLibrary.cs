using Skua.App.Droid.Core.Platform;

namespace Skua.App.Droid.Core.Scripts;

/// <summary>A folder or file in the script library.</summary>
/// <param name="Name">File or folder name.</param>
/// <param name="RelativePath">Path below the library root, with '/' separators.</param>
/// <param name="FullPath">Absolute path.</param>
/// <param name="IsFolder">True for folders.</param>
/// <param name="Size">File size in bytes (0 for folders).</param>
/// <param name="Modified">Last write time.</param>
public sealed record ScriptEntry(string Name, string RelativePath, string FullPath, bool IsFolder, long Size, DateTime Modified)
{
    public bool IsScript => !IsFolder && Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public string Detail => IsFolder ? "folder" : Size < 1024 ? $"{Size} B" : $"{Size / 1024.0:0.#} KB";
}

/// <summary>What an import did.</summary>
public sealed record ImportResult(int Imported, long Bytes, IReadOnlyList<string> Skipped)
{
    public string Summary => Skipped.Count == 0
        ? $"Imported {Imported} file(s)."
        : $"Imported {Imported} file(s); skipped {Skipped.Count}: {string.Join("; ", Skipped.Take(5))}{(Skipped.Count > 5 ? "; ..." : string.Empty)}";
}

/// <summary>
/// The app-private script folder (<see cref="AppPaths.ScriptsDir"/>, the same folder Skua.Core resolves
/// <c>//cs_include</c> against): browsing, importing from the system picker, deleting.
/// </summary>
/// <remarks>
/// Imported paths come from a content provider and are untrusted: every path segment is validated, nothing may
/// land outside the library root, only known text file types are accepted and sizes are bounded.
/// </remarks>
public sealed class ScriptLibrary
{
    /// <summary>File types a script folder legitimately contains (sources, includes, option/data files).</summary>
    public static IReadOnlySet<string> AllowedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".json", ".txt", ".xml", ".csv", ".md" };

    public const long MaxFileBytes = 8L * 1024 * 1024;
    public const long MaxImportBytes = 256L * 1024 * 1024;
    public const int MaxImportFiles = 10_000;

    // The compiler cache lives inside the script folder (Skua.Core layout); never show or overwrite it.
    private const string CacheFolderName = "Cached-Scripts";

    public ScriptLibrary(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>Folders first, then files, both by name. <paramref name="relativeFolder"/> is "" for the root.</summary>
    public IReadOnlyList<ScriptEntry> List(string relativeFolder = "")
    {
        string folder = Resolve(relativeFolder, allowRoot: true) ?? throw new ArgumentException("Invalid folder.", nameof(relativeFolder));
        if (!Directory.Exists(folder))
            return [];
        DirectoryInfo dir = new(folder);
        IEnumerable<ScriptEntry> folders = dir.EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.') && !(folder == Root && d.Name.Equals(CacheFolderName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new ScriptEntry(d.Name, ToRelative(d.FullName), d.FullName, true, 0, d.LastWriteTime));
        IEnumerable<ScriptEntry> files = dir.EnumerateFiles()
            .Where(f => !f.Name.StartsWith('.'))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ScriptEntry(f.Name, ToRelative(f.FullName), f.FullName, false, f.Length, f.LastWriteTime));
        return folders.Concat(files).ToList();
    }

    /// <summary>Recursively finds scripts whose name contains <paramref name="query"/> (bounded).</summary>
    public IReadOnlyList<ScriptEntry> Search(string query, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(query) || !Directory.Exists(Root))
            return [];
        string cache = Path.Combine(Root, CacheFolderName);
        return Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(p => Path.GetFileName(p).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(max)
            .Select(p => new FileInfo(p))
            .Select(f => new ScriptEntry(f.Name, ToRelative(f.FullName), f.FullName, false, f.Length, f.LastWriteTime))
            .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Copies picked files into <paramref name="targetFolder"/> (relative to the root), keeping their sub-paths.</summary>
    public async Task<ImportResult> ImportAsync(IReadOnlyList<PickedFile> files, string targetFolder = "", CancellationToken cancellationToken = default)
    {
        string target = Resolve(targetFolder, allowRoot: true) ?? throw new ArgumentException("Invalid target folder.", nameof(targetFolder));
        List<string> skipped = new();
        int imported = 0;
        long total = 0;
        foreach (PickedFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (imported >= MaxImportFiles)
            {
                skipped.Add($"stopped after {MaxImportFiles} files");
                break;
            }
            string? relative = SanitizeRelativePath(file.RelativePath);
            if (relative is null)
            {
                skipped.Add($"{file.RelativePath}: invalid path");
                continue;
            }
            if (!AllowedExtensions.Contains(Path.GetExtension(relative)))
            {
                skipped.Add($"{relative}: not a script or data file");
                continue;
            }
            if (file.Size is long declared && declared > MaxFileBytes)
            {
                skipped.Add($"{relative}: larger than {MaxFileBytes / 1048576} MB");
                continue;
            }
            string destination = Path.GetFullPath(Path.Combine(target, relative));
            if (!IsInside(destination) || IsInsideCache(destination))
            {
                skipped.Add($"{relative}: outside the script folder");
                continue;
            }

            long written = await CopyBoundedAsync(file, destination, MaxImportBytes - total, cancellationToken).ConfigureAwait(false);
            if (written < 0)
            {
                skipped.Add($"{relative}: too large");
                continue;
            }
            total += written;
            imported++;
        }
        return new ImportResult(imported, total, skipped);
    }

    /// <summary>Deletes a file or folder below the root (never the root itself).</summary>
    public void Delete(string relativePath)
    {
        string path = Resolve(relativePath, allowRoot: false) ?? throw new ArgumentException("Invalid path.", nameof(relativePath));
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Absolute path for a relative one, or null if it would leave the root.</summary>
    public string? Resolve(string relativePath, bool allowRoot)
    {
        if (string.IsNullOrEmpty(relativePath))
            return allowRoot ? Root : null;
        string? clean = SanitizeRelativePath(relativePath);
        if (clean is null)
            return null;
        string full = Path.GetFullPath(Path.Combine(Root, clean));
        return IsInside(full) ? full : null;
    }

    /// <summary>
    /// Normalises an untrusted relative path to the platform separator, or returns null when it is absolute, has
    /// "." / ".." / empty segments, control or reserved characters, or is unreasonably long.
    /// </summary>
    public static string? SanitizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024)
            return null;
        string[] segments = path.Split('/', '\\');
        if (segments.Length > 16)
            return null;
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Length > 255)
                return null;
            if (segment != segment.Trim())
                return null;
            foreach (char c in segment)
            {
                if (char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
                    return null;
            }
        }
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private bool IsInside(string fullPath) =>
        fullPath.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private bool IsInsideCache(string fullPath) =>
        fullPath.StartsWith(Path.Combine(Root, CacheFolderName) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private string ToRelative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    // Copies through a temp file; returns bytes written, or -1 when the file exceeds the per-file or remaining budget.
    private static async Task<long> CopyBoundedAsync(PickedFile file, string destination, long budget, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = destination + ".import-tmp";
        long limit = Math.Min(MaxFileBytes, budget);
        long written = 0;
        try
        {
            await using (Stream source = await file.OpenRead().ConfigureAwait(false))
            await using (FileStream target = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > limit)
                        break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            if (written > limit)
            {
                File.Delete(temp);
                return -1;
            }
            File.Move(temp, destination, overwrite: true);
            return written;
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            throw;
        }
    }
}

/// <summary>
/// Placeholder entry point for syncing the community script repository (github.com/auqw/Scripts, branch Skua, the
/// one Skua.Core's GetScriptsService uses) into <see cref="AppPaths.ScriptsDir"/>. Not implemented in this version:
/// the intended implementation is Skua.Core's own GetScriptsService once the host project provides its DI container.
/// </summary>
public interface IScriptRepositorySync
{
    bool Available { get; }

    string Description { get; }

    Task<string> SyncAsync(IProgress<string>? progress, CancellationToken cancellationToken);
}

public sealed class PendingScriptRepositorySync : IScriptRepositorySync
{
    public bool Available => false;

    public string Description =>
        "Community scripts (github.com/auqw/Scripts) will sync here once the Skua.Core host is wired in. "
        + "Until then, import a downloaded copy with \"Import folder\".";

    public Task<string> SyncAsync(IProgress<string>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(Description);
}
