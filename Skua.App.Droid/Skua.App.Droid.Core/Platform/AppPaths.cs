namespace Skua.App.Droid.Core.Platform;

/// <summary>
/// The app-private directory layout. On Android every root is inside the app sandbox
/// (<c>/data/user/&lt;n&gt;/com.vibeskua.droid/...</c>), unreadable by other apps and excluded from backup.
/// </summary>
/// <remarks>
/// <para>
/// Skua.Core's <c>ClientFileSources</c> derives everything from <c>Environment.SpecialFolder.ApplicationData</c>,
/// captured once in static fields. On Android (Mono, .NET 10) that folder is <c>$HOME/.config</c>: the Android
/// build of System.Private.CoreLib has no XDG_CONFIG_HOME override (verified on the 10.0.15 android-arm64 runtime
/// pack), and the .NET Android host sets HOME to <c>Context.FilesDir</c>. The app sets HOME to FilesDir itself
/// before any Skua type is touched and then checks the result, so <see cref="SkuaDir"/> below and
/// <c>ClientFileSources.SkuaDIR</c> are the same directory.
/// </para>
/// <code>
/// files/                          HOME (Context.FilesDir)
///   .config/                      ApplicationData
///     VibeSkua/                   ClientFileSources.SkuaDIR (Skua.Core settings, options, quest data ...)
///       Scripts/                  ClientFileSources.SkuaScriptsDIR: imported and synced scripts
///         Cached-Scripts/         Skua.Core compiler disk cache (emitted script assemblies)
///   droid/settings.json           this app's settings (no secrets)
///   droid/plugins.json            built-in plugins: on/off and options (no secrets)
///   droid/queue.json              the script queue and its position (resume after a restart; no secrets)
///   droid/playlists/*.json        saved script queues (no secrets)
/// no_backup/secrets/              Keystore-encrypted credential (Context.NoBackupFilesDir)
/// cache/                          temporary files (Context.CacheDir, TMPDIR)
/// </code>
/// </remarks>
public sealed record AppPaths(string FilesDir, string NoBackupDir, string CacheDir)
{
    /// <summary>What <c>Environment.SpecialFolder.ApplicationData</c> resolves to once HOME = <see cref="FilesDir"/>.</summary>
    public string ApplicationDataDir => Path.Combine(FilesDir, ".config");

    /// <summary>Equals Skua.Core's <c>ClientFileSources.SkuaDIR</c>.</summary>
    public string SkuaDir => Path.Combine(ApplicationDataDir, "VibeSkua");

    /// <summary>Equals Skua.Core's <c>ClientFileSources.SkuaScriptsDIR</c>: the script folder the app browses.</summary>
    public string ScriptsDir => Path.Combine(SkuaDir, "Scripts");

    /// <summary>Skua.Core's compiled-script disk cache (<c>Compiler</c> / <c>ScriptLoadContext</c>).</summary>
    public string CompiledScriptCacheDir => Path.Combine(ScriptsDir, "Cached-Scripts");

    public string AppDataDir => Path.Combine(FilesDir, "droid");

    public string SettingsFile => Path.Combine(AppDataDir, "settings.json");

    /// <summary>The built-in plugins' state and options (<see cref="Plugins.PluginStore"/>). No secrets.</summary>
    public string PluginsFile => Path.Combine(AppDataDir, "plugins.json");

    /// <summary>Restart bookkeeping (resume after a memory restart). No secrets.</summary>
    public string ResumeFile => Path.Combine(AppDataDir, "resume.json");

    /// <summary>The script queue: entries, position and results (<see cref="Scheduler.QueueStore"/>). No secrets.</summary>
    public string QueueFile => Path.Combine(AppDataDir, "queue.json");

    /// <summary>Saved script queues (playlists). No secrets.</summary>
    public string PlaylistsDir => Path.Combine(AppDataDir, "playlists");

    /// <summary>Skua's options folder (script options <c>.cfg</c>, CoreBots' <c>CBO_Storage(..).txt</c>).</summary>
    public string SkuaOptionsDir => Path.Combine(SkuaDir, "options");

    public string SecretsDir => Path.Combine(NoBackupDir, "secrets");

    /// <summary>For a desktop host or tests: everything under one root.</summary>
    public static AppPaths UnderRoot(string root) =>
        new(Path.Combine(root, "files"), Path.Combine(root, "no_backup"), Path.Combine(root, "cache"));

    /// <summary>Creates the directories the app writes to.</summary>
    public void EnsureCreated()
    {
        foreach (string dir in new[] { FilesDir, NoBackupDir, CacheDir, SkuaDir, ScriptsDir, AppDataDir, SecretsDir })
            Directory.CreateDirectory(dir);
    }
}
