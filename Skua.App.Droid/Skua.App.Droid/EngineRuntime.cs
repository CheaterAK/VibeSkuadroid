using Android.Content;
using Android.Content.Res;
using Android.Util;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;
using Skua.App.Droid.Core.ViewModels;
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
    public const string LogTag = "VibeSkua";

    private static Context? s_context;
    private static AppPaths? s_paths;
    private static EngineHost? s_host;
    private static AndroidPlatformServices? s_platform;
    private static ScriptLibrary? s_library;

    public static AppPaths Paths => s_paths ?? throw NotInitialized();

    public static EngineHost Host => s_host ?? throw NotInitialized();

    public static AndroidPlatformServices Platform => s_platform ?? throw NotInitialized();

    public static bool IsInitialized => s_host is not null;

    /// <summary>Called first thing in <see cref="DroidApplication.OnCreate"/> (main process only).</summary>
    public static void Initialize(Context application)
    {
        if (s_host is not null)
            return;
        s_context = application;

        string files = application.FilesDir!.AbsolutePath;
        string noBackup = application.NoBackupFilesDir!.AbsolutePath;
        string cache = application.CacheDir!.AbsolutePath;

        // Deliberately pin HOME (the .NET Android host already sets it to FilesDir): on Android
        // SpecialFolder.ApplicationData is $HOME/.config, and Skua.Core's ClientFileSources captures it once.
        Environment.SetEnvironmentVariable("HOME", files);
        Environment.SetEnvironmentVariable("TMPDIR", cache);
        AppPaths paths = new(files, noBackup, cache);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.Equals(appData.TrimEnd('/'), paths.ApplicationDataDir, StringComparison.Ordinal))
            Log.Warn(LogTag, $"ApplicationData resolved to {appData}, expected {paths.ApplicationDataDir}; Skua.Core files will not be where the app looks.");
        paths.EnsureCreated();
        s_paths = paths;

        SettingsStore settings = new(paths.SettingsFile);
        CredentialVault vault = new(new KeystoreSecretStore(paths.SecretsDir));
        s_platform = new AndroidPlatformServices(application);
        s_library = new ScriptLibrary(paths.ScriptsDir);

        ScriptReferenceSet references = ScriptReferenceSet.Load(new AssetScriptReferenceSource(application.Assets!));
        if (!references.IsComplete)
            Log.Error(LogTag, "Script references incomplete: " + string.Join("; ", references.Problems));
        IScriptHost scripts = new SkuaScriptHostAdapter(references, new ScriptAssemblyStore(), paths.ScriptsDir);

        HeadlessBotEngine engine = new(new HeadlessEngineOptions { AutoReconnect = settings.Current.AutoReconnect }, scripts);
        s_host = new EngineHost(engine, settings, vault, new ResumeStore(paths.ResumeFile), s_platform);

        // The foreground service follows the engine: started as soon as a session or script is active.
        engine.SnapshotChanged += (_, snapshot) => EngineService.EnsureRunningFor(application, snapshot);
        Log.Info(LogTag, $"Engine ready. Scripts: {paths.ScriptsDir}; references: {references.Files.Count} images{(references.IsComplete ? string.Empty : " (incomplete)")}.");
    }

    /// <summary>The ViewModels' services; called on the UI thread when the first view is created.</summary>
    public static AppServices CreateAppServices() => new(
        Host,
        new AvaloniaUiDispatcher(),
        new AvaloniaFilePicker(),
        s_library ?? throw NotInitialized(),
        new PendingScriptRepositorySync(),
        Paths);

    private static InvalidOperationException NotInitialized() => new("EngineRuntime.Initialize was not called.");
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
