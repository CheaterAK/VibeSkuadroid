using Skua.App.Droid.Core.Platform;

namespace Skua.App.Droid.Core.Army;

/// <summary>
/// Where a full slot process keeps its files. Skua.Core derives every path from <c>$HOME/.config</c> once per process,
/// so each slot process pins HOME to its own folder and Skua's settings, options (CoreBots' <c>CBO_Storage</c>), quest
/// data, compiled-script cache and the app's own state never collide between slots.
/// </summary>
/// <remarks>
/// <code>
/// files/                                   slot 1 = the main process (coordinator, UI, single-account mode): unchanged
///   .config/VibeSkua/Scripts/              THE script folder: imported and synced scripts (written by the main process only)
///   droid/accounts.json                    army accounts and slot assignment (no secrets; slots read it)
///   slots/acctN/                           HOME of slot N (N = 2..6)
///     .config/VibeSkua/                    Skua dir of slot N: Skua.settings.json, options/, QuestData.json, skills
///       Scripts/                           own folder; every entry of the shared script folder is a symlink to it,
///         Cached-Scripts/                  except Skua's compiled-script cache and LoadoutEnhancerTemp.cs (own)
///     droid/                               settings, resume, queue of slot N (no secrets)
///     diagnostics/                         crash report and events of slot N
/// no_backup/secrets/                       ONE Keystore-encrypted store for all processes: aqw-account (single mode),
///                                          aqw-acct-&lt;id&gt; per army account, discord-webhook
/// app_slotcache/acctN/                     TMPDIR of slot N (next to cache/, which is the main process's TMPDIR)
/// </code>
/// Shared read-only: the scripts (symlinks, so a sync by the main process is seen by every slot at its next start of a
/// script; slots never sync or import), the vault (each slot decrypts only the entry of the account it logs in), the
/// script reference images (APK assets). Copied into a slot when it starts, then its own: the skill rotations
/// (<c>AdvancedSkills.json</c>, <c>UserAdvancedSkills*.json</c>: Skua rewrites them, so sharing would mean concurrent
/// writers) and <c>QuestData.json</c>. Option profiles a command names (<c>options/&lt;storage&gt;.cfg</c>) are copied from
/// the main options folder when the script starts.
/// </remarks>
public sealed record SlotLayout(int Slot, AppPaths Main)
{
    /// <summary>Entries of the shared script folder that each slot keeps for itself (written by Skua at run time).</summary>
    public static IReadOnlyCollection<string> PerSlotScriptEntries { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Cached-Scripts",
        "LoadoutEnhancerTemp.cs",
    };

    /// <summary>Files of the main Skua dir copied into the slot's when the slot starts (newer ones only).</summary>
    public static IReadOnlyList<string> CopiedAtStart { get; } =
    [
        "AdvancedSkills.json",
        "UserAdvancedSkills.json",
        "UserAdvancedSkills.base.json",
        "QuestData.json",
    ];

    /// <summary>The paths of this slot's process (HOME = <see cref="AppPaths.FilesDir"/>; the vault folder is shared).</summary>
    public AppPaths Paths => Slot == SlotIds.MainSlot
        ? Main
        : new AppPaths(
            Path.Combine(Main.FilesDir, "slots", $"acct{Slot}"),
            Main.NoBackupDir,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Main.CacheDir).TrimEnd('/'))!, "app_slotcache", $"acct{Slot}"));

    /// <summary>The army's account list (main process files).</summary>
    public string AccountsFile => AccountsFileOf(Main);

    public static string AccountsFileOf(AppPaths main) => Path.Combine(main.AppDataDir, "accounts.json");

    /// <summary>The shared script folder (the main process's).</summary>
    public string SharedScriptsDir => Main.ScriptsDir;

    public static SlotLayout For(AppPaths main, int slot)
    {
        if (slot != SlotIds.MainSlot && !SlotIds.IsRemote(slot))
            throw new ArgumentOutOfRangeException(nameof(slot));
        return new SlotLayout(slot, main);
    }

    /// <summary>Every directory a process of this slot writes to (for the collision check).</summary>
    public IReadOnlyList<string> WritableRoots()
    {
        AppPaths p = Paths;
        return Slot == SlotIds.MainSlot
            ? [p.SkuaDir, p.AppDataDir, p.DiagnosticsDir, p.CacheDir, p.CompiledScriptCacheDir]
            : [p.FilesDir, p.CacheDir];
    }

    /// <summary>
    /// The links this slot's script folder needs for the shared entries <paramref name="sharedEntries"/> (names in the
    /// shared folder): link path and target, leaving out the per-slot entries.
    /// </summary>
    public IReadOnlyList<(string Link, string Target)> ScriptLinks(IEnumerable<string> sharedEntries)
    {
        if (Slot == SlotIds.MainSlot)
            return [];
        List<(string, string)> links = new();
        foreach (string name in sharedEntries)
        {
            if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\') || PerSlotScriptEntries.Contains(name))
                continue;
            links.Add((Path.Combine(Paths.ScriptsDir, name), Path.Combine(SharedScriptsDir, name)));
        }
        return links;
    }

    /// <summary>
    /// Creates the slot's folders, links the shared scripts and copies the start-time files. Idempotent; run by the slot
    /// process before Skua is composed. Returns problems (the slot still starts).
    /// </summary>
    public IReadOnlyList<string> Prepare()
    {
        List<string> problems = new();
        AppPaths p = Paths;
        p.EnsureCreated();
        if (Slot == SlotIds.MainSlot)
            return problems;
        Directory.CreateDirectory(p.CompiledScriptCacheDir);
        Directory.CreateDirectory(p.SkuaOptionsDir);
        Directory.CreateDirectory(SharedScriptsDir);

        HashSet<string> wanted = new(StringComparer.Ordinal);
        foreach ((string link, string target) in ScriptLinks(Directory.EnumerateFileSystemEntries(SharedScriptsDir).Select(Path.GetFileName)!))
        {
            wanted.Add(Path.GetFileName(link));
            try
            {
                FileSystemInfo info = new FileInfo(link);
                if (info.Exists || Directory.Exists(link) || info.LinkTarget is not null)
                {
                    if (info.LinkTarget == target)
                        continue;
                    if (info.LinkTarget is null && Directory.Exists(link))
                        Directory.Delete(link, recursive: true); // never: only a broken previous layout
                    else
                        File.Delete(link);
                }
                if (Directory.Exists(target))
                    Directory.CreateSymbolicLink(link, target);
                else
                    File.CreateSymbolicLink(link, target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                problems.Add($"{Path.GetFileName(link)}: {e.Message}");
            }
        }
        // Links to entries the shared folder no longer has.
        foreach (string entry in Directory.EnumerateFileSystemEntries(p.ScriptsDir))
        {
            string name = Path.GetFileName(entry);
            if (wanted.Contains(name) || PerSlotScriptEntries.Contains(name))
                continue;
            try
            {
                if (new FileInfo(entry).LinkTarget is not null)
                    File.Delete(entry);
            }
            catch (IOException e)
            {
                problems.Add($"{name}: {e.Message}");
            }
        }
        foreach (string file in CopiedAtStart)
        {
            string from = Path.Combine(Main.SkuaDir, file);
            string to = Path.Combine(p.SkuaDir, file);
            try
            {
                if (File.Exists(from) && (!File.Exists(to) || File.GetLastWriteTimeUtc(from) > File.GetLastWriteTimeUtc(to)))
                    File.Copy(from, to, overwrite: true);
            }
            catch (IOException e)
            {
                problems.Add($"{file}: {e.Message}");
            }
        }
        return problems;
    }

    /// <summary>Copies the main options folder's <c>&lt;storage&gt;.cfg</c> into this slot's (option profiles of army commands).</summary>
    public bool CopyOptionProfile(string storage)
    {
        ArmyNames.RequireOptionStorage(storage);
        if (Slot == SlotIds.MainSlot)
            return true;
        string from = Path.Combine(Main.SkuaOptionsDir, storage + ".cfg");
        if (!File.Exists(from))
            return false;
        Directory.CreateDirectory(Paths.SkuaOptionsDir);
        File.Copy(from, Path.Combine(Paths.SkuaOptionsDir, storage + ".cfg"), overwrite: true);
        return true;
    }

    /// <summary>The full path of a script in this slot's folder; refuses anything outside it or not a .cs file.</summary>
    public string ResolveScript(string relativePath)
    {
        string root = Path.GetFullPath(Paths.ScriptsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(s => s is "" or "." or ".."))
            throw new Engine.EngineException("Invalid script path.");
        string full = Path.GetFullPath(Path.Combine(root, normalized));
        if (!full.StartsWith(root, StringComparison.Ordinal) || !full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new Engine.EngineException("Invalid script path.");
        if (!File.Exists(full))
            throw new Engine.EngineException($"Slot {Slot} has no script {normalized} (sync or import it in the main app).");
        return full;
    }

    /// <summary>
    /// Paths two different slots would both write (the check of the layout): a writable root of one inside or equal to
    /// one of another. Empty when the layout is collision free.
    /// </summary>
    public static IReadOnlyList<string> Collisions(AppPaths main, IEnumerable<int> slots)
    {
        List<(int Slot, string Root)> roots = slots
            .SelectMany(s => For(main, s).WritableRoots().Select(r => (s, Path.GetFullPath(r).TrimEnd('/') + "/")))
            .ToList();
        List<string> found = new();
        foreach ((int a, string ra) in roots)
        {
            foreach ((int b, string rb) in roots)
            {
                if (a < b && (ra.StartsWith(rb, StringComparison.Ordinal) || rb.StartsWith(ra, StringComparison.Ordinal)))
                    found.Add($"slot {a} {ra} / slot {b} {rb}");
            }
        }
        return found;
    }
}
