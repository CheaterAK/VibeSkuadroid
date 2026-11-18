using Android.Content;
using Android.Content.Res;
using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.Diagnostics;
using Skua.App.Droid.Scripting;
using Skua.App.Droid.Security;
using Skua.App.Droid.UI.Services;

namespace Skua.App.Droid;

/// <summary>
/// Process-wide composition root. The engine lives here, not in an activity or the service object, so it survives
/// activities being destroyed and the service being rebound; the foreground service only keeps the process alive.
/// </summary>
internal static class EngineRuntime
{
    public const string LogTag = CrashCapture.Tag;

    private static readonly object s_initLock = new();
    private static AppPaths? s_paths;
    private static EngineHost? s_host;
    private static AndroidPlatformServices? s_platform;
    private static ScriptLibrary? s_library;

    public static AppPaths Paths => s_paths ?? throw NotInitialized();

    public static EngineHost Host => s_host ?? throw NotInitialized();

    public static AndroidPlatformServices Platform => s_platform ?? throw NotInitialized();

    /// <summary>The engine was composed. False in the ":restart" process, or after a recorded composition failure.</summary>
    public static bool IsInitialized => s_host is not null;

    /// <summary>
    /// Composes the engine once per process (main process only): from <see cref="DroidApplication.OnCreate"/>, and
    /// again from the activity, the service or the restart receiver if that first attempt was skipped or failed.
    /// Never throws: a failure is recorded (crash report + log) and the app keeps running without an engine.
    /// </summary>
    /// <returns>Whether the engine is available.</returns>
    public static bool TryInitialize(Context context)
    {
        if (s_host is not null)
            return true;
        lock (s_initLock)
        {
            if (s_host is not null)
                return true;
            CrashCapture.Step(StartupStep.EngineInitStarted);
            try
            {
                Initialize(context.ApplicationContext ?? context);
            }
            catch (Exception e)
            {
                CrashCapture.Recovered("engine composition", e);
            }
            CrashCapture.Step(StartupStep.EngineInitFinished, s_host is null ? "engine unavailable" : null);
            return s_host is not null;
        }
    }

    private static void Initialize(Context application)
    {
        string files = application.FilesDir!.AbsolutePath;
        string noBackup = application.NoBackupFilesDir!.AbsolutePath;
        string cache = application.CacheDir!.AbsolutePath;

        // Deliberately pin HOME (the .NET Android host already sets it to FilesDir): on Android
        // SpecialFolder.ApplicationData is $HOME/.config, and Skua.Core's ClientFileSources captures it once.
        // Nothing before this point (crash capture included) asks for a SpecialFolder.
        Environment.SetEnvironmentVariable("HOME", files);
        Environment.SetEnvironmentVariable("TMPDIR", cache);
        AppPaths paths = new(files, noBackup, cache);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.Equals(appData.TrimEnd('/'), paths.ApplicationDataDir, StringComparison.Ordinal))
            CrashCapture.Info($"ApplicationData resolved to {appData}, expected {paths.ApplicationDataDir}; Skua.Core files will not be where the app looks.");
        paths.EnsureCreated();
        s_paths = paths;
        CrashCapture.Info("engine: paths ready");

        SettingsStore settings = new(paths.SettingsFile);
        CredentialVault vault = new(CreateSecretStore(application, paths.SecretsDir));
        AndroidPlatformServices platform = new(application);
        s_library = new ScriptLibrary(paths.ScriptsDir);
        CrashCapture.Info($"engine: settings, vault (available: {vault.IsAvailable}) and script library ready");

        ScriptReferenceSet references = LoadScriptReferences(application);
        IScriptHost scripts = new SkuaScriptHostAdapter(references, new ScriptAssemblyStore(), paths.ScriptsDir);

        HeadlessBotEngine engine = new(new HeadlessEngineOptions
        {
            AutoReconnect = settings.Current.AutoReconnect,
            // Secrets the engine redacts are also redacted from crash reports.
            Scrubber = DiagnosticText.Secrets,
        }, scripts);
        CrashCapture.Info("engine: engine created");
        s_platform = platform;
        s_host = new EngineHost(engine, settings, vault, new ResumeStore(paths.ResumeFile), platform);
        s_host.RememberFailed += (_, e) => CrashCapture.NonFatal("remembering the password", e);

        // The foreground service follows the engine: started as soon as a session or script is active.
        engine.SnapshotChanged += (_, snapshot) => EngineService.EnsureRunningFor(application, snapshot);
        CrashCapture.Info($"Engine ready. Script references: {references.Files.Count} images{(references.IsComplete ? string.Empty : " (incomplete)")}.");
    }

    /// <summary>The Keystore-backed store, or a store that remembers nothing if the Keystore cannot be used at all.</summary>
    private static ISecretStore CreateSecretStore(Context context, string directory)
    {
        try
        {
            return new KeystoreSecretStore(context, directory);
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("Keystore secret store", e);
            return new UnavailableSecretStore();
        }
    }

    /// <summary>The script reference images from the APK assets; an empty (incomplete) set if they cannot be listed.</summary>
    private static ScriptReferenceSet LoadScriptReferences(Context application)
    {
        try
        {
            ScriptReferenceSet references = ScriptReferenceSet.Load(new AssetScriptReferenceSource(application.Assets!));
            if (!references.IsComplete)
                CrashCapture.Info("Script references incomplete: " + string.Join("; ", references.Problems.Take(5)));
            return references;
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("loading script references", e);
            return ScriptReferenceSet.Load(new EmptyScriptReferenceSource());
        }
    }

    /// <summary>The ViewModels' services; called on the UI thread when the first view is created.</summary>
    public static AppServices CreateAppServices() => new(
        Host,
        new AvaloniaUiDispatcher(),
        new AvaloniaFilePicker(),
        s_library ?? throw NotInitialized(),
        new PendingScriptRepositorySync(),
        Paths);

    private static InvalidOperationException NotInitialized() => new("The engine is not available (its composition failed or was skipped; see the crash report).");
}

/// <summary>Reads the script reference images packed under assets/script-refs.</summary>
internal sealed class AssetScriptReferenceSource : IScriptReferenceSource
{
    private readonly AssetManager _assets;

    public AssetScriptReferenceSource(AssetManager assets)
    {
        _assets = assets;
    }

    public IReadOnlyList<string> List(string folder) => _assets.List($"{ScriptReferenceSet.AssetRoot}/{folder}") ?? [];

    public Stream Open(string relativePath) => _assets.Open($"{ScriptReferenceSet.AssetRoot}/{relativePath}");

    public string? ReadManifest()
    {
        try
        {
            using Stream stream = _assets.Open($"{ScriptReferenceSet.AssetRoot}/{ScriptReferenceSet.ManifestName}");
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }
        catch (Java.IO.IOException)
        {
            return null;
        }
    }
}

/// <summary>No reference images (used when the asset folder cannot be read): scripts then report incomplete references.</summary>
internal sealed class EmptyScriptReferenceSource : IScriptReferenceSource
{
    public IReadOnlyList<string> List(string folder) => [];

    public Stream Open(string relativePath) => throw new FileNotFoundException(relativePath);

    public string? ReadManifest() => null;
}
