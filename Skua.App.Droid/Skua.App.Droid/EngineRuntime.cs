using Android.Content;
using Android.Content.Res;
using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Notifications;
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
/// <remarks>
/// Composition (Skua's DI container, folders, Keystore, script references) takes seconds on a phone, so it never runs
/// on the main thread: <see cref="StartInitialization"/> does the cheap, process-global part (HOME, the folders) on
/// the caller's thread and the rest on a background thread (<see cref="EngineStartup{T}"/>), and returns at once.
/// Everything that needs the engine continues when that task completes: the UI shows a short "starting" view, the
/// service is in the foreground immediately and runs its commands afterwards, the activity requests permissions and
/// resumes afterwards. A failure is recorded (crash report + log) before anyone continues, the app keeps running
/// without an engine, and the next <see cref="StartInitialization"/> tries again.
/// </remarks>
internal static class EngineRuntime
{
    public const string LogTag = CrashCapture.Tag;

    private static readonly object s_pathsLock = new();
    private static readonly EngineStartup<Composition> s_startup = new(Compose, OnStartupChanged);
    private static AppPaths? s_paths;
    private static Context? s_application;

    public static AppPaths Paths => s_paths ?? throw NotInitialized();

    public static EngineHost Host => s_startup.Value?.Host ?? throw NotInitialized();

    public static AndroidPlatformServices Platform => s_startup.Value?.Platform ?? throw NotInitialized();

    /// <summary>The game view switch (null until the engine is composed).</summary>
    public static Skua.App.Droid.Core.GameView.GameViewSwitch? GameView => s_startup.Value?.GameView;

    /// <summary>The engine was composed. False in the ":restart" process, while it is composing, or after a recorded failure.</summary>
    public static bool IsInitialized => s_startup.Value is not null;

    /// <summary>
    /// Starts composing the engine (main process only) unless it is composed or composing, and returns at once: from
    /// <see cref="DroidApplication.OnCreate"/>, and again from the activity, the service or the restart receiver if that
    /// first attempt was skipped or failed. Never throws and never blocks on the composition.
    /// </summary>
    /// <returns>Completes on a background thread with whether the engine is available; never faults.</returns>
    public static Task<bool> StartInitialization(Context context)
    {
        Context application = context.ApplicationContext ?? context;
        if (!PreparePaths(application))
            return Task.FromResult(false);
        Volatile.Write(ref s_application, application);
        Task<Composition?> attempt = s_startup.EnsureStarted();
        return attempt.IsCompleted
            ? Task.FromResult(attempt.Result is not null)
            : attempt.ContinueWith(t => t.Result is not null, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the main thread once the engine composition started by
    /// <see cref="StartInitialization"/> completed, with whether the engine is available. Runs it right away when it already is.
    /// </summary>
    public static void WhenInitialized(Context context, Action<bool> action)
    {
        Task<bool> ready = StartInitialization(context);
        if (ready.IsCompleted && Android.OS.Looper.MyLooper() == Android.OS.Looper.MainLooper)
        {
            Run(ready.Result);
            return;
        }
        ready.ContinueWith(t => new Android.OS.Handler(Android.OS.Looper.MainLooper!).Post(() => Run(t.Result)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        void Run(bool available)
        {
            try
            {
                action(available);
            }
            catch (Exception e)
            {
                CrashCapture.NonFatal("continuing after engine composition", e);
            }
        }
    }

    /// <summary>
    /// The cheap, process-global part, on the caller's thread: pins HOME and TMPDIR before any other thread could read
    /// the environment (setenv is not safe next to concurrent getenv) and creates the folders. Once per process.
    /// </summary>
    private static bool PreparePaths(Context application)
    {
        if (Volatile.Read(ref s_paths) is not null)
            return true;
        lock (s_pathsLock)
        {
            if (s_paths is not null)
                return true;
            try
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
                Volatile.Write(ref s_paths, paths);
                CrashCapture.Info("engine: paths ready");
                return true;
            }
            catch (Exception e)
            {
                CrashCapture.Recovered("engine composition (app folders)", e);
                return false;
            }
        }
    }

    private static void OnStartupChanged(EngineStartupState state, Exception? error)
    {
        switch (state)
        {
            case EngineStartupState.Starting:
                CrashCapture.Engine(EngineInitPhase.Started);
                break;
            case EngineStartupState.Ready:
                CrashCapture.Engine(EngineInitPhase.Finished);
                break;
            case EngineStartupState.Failed:
                CrashCapture.Recovered("engine composition", error!);
                CrashCapture.Engine(EngineInitPhase.Finished, "engine unavailable");
                break;
        }
    }

    /// <summary>What a successful composition produced; published as one object once everything is in place.</summary>
    private sealed record Composition(EngineHost Host, AndroidPlatformServices Platform, ScriptLibrary Library, NotificationHub Notifications,
        Skua.App.Droid.Core.GameView.GameViewSwitch GameView, Skua.App.Droid.Core.Army.ArmyCoordinator Army);

    /// <summary>The expensive part, on the background thread.</summary>
    private static Composition Compose()
    {
        Context application = Volatile.Read(ref s_application) ?? throw new InvalidOperationException("No application context.");
        AppPaths paths = Paths;
        SettingsStore settings = new(paths.SettingsFile);
        // One Keystore-backed store: the remembered password and the Discord webhook URL are separate entries in it.
        ISecretStore secrets = CreateSecretStore(application, paths.SecretsDir);
        CredentialVault vault = new(secrets);
        AndroidPlatformServices platform = new(application);
        ScriptLibrary library = new(paths.ScriptsDir);
        CrashCapture.Info($"engine: settings, vault (available: {vault.IsAvailable}) and script library ready");

        ScriptReferenceSet references = LoadScriptReferences(application);
        IScriptHost scripts = new SkuaScriptHostAdapter(references, new ScriptAssemblyStore(), paths.ScriptsDir)
        {
            // Scripts compile in the short-lived ":compiler" process: Roslyn's peak (~430 MB for CoreBots) and what it leaves
            // behind never enter this process's heap; here they are only loaded from the cache.
            Precompiler = new ScriptPrecompiler(new Compiler.CompilerClient(application, 1), references.Identity,
                () => ScriptPrecompiler.RefuseInProcessFor(Army.AndroidArmyPlatform.Read(application))),
        };

        HeadlessBotEngine engine = new(new HeadlessEngineOptions
        {
            AutoReconnect = settings.Current.AutoReconnect,
            // Secrets the engine redacts are also redacted from crash reports.
            Scrubber = DiagnosticText.Secrets,
        }, scripts);
        CrashCapture.Info("engine: engine created");
        // Memory pressure (DroidApplication.OnTrimMemory): drop what can be rebuilt.
        Skua.App.Droid.Core.Memory.MemoryPressure.Register(() =>
        {
            engine.TrimCaches();
            ((SkuaScriptHostAdapter)scripts).TrimCaches();
        });
        EngineHost host = new(engine, settings, vault, new ResumeStore(paths.ResumeFile), platform);
        host.RememberFailed += (_, e) => CrashCapture.NonFatal("remembering the password", e);
        // The script queue lives with the engine (kept alive by the foreground service), not in the UI.
        host.AttachScheduler(new Skua.App.Droid.Core.Scheduler.ScriptScheduler(engine,
            new Skua.App.Droid.Core.Scheduler.QueueStore(paths.QueueFile, paths.PlaylistsDir), paths.ScriptsDir, paths.SkuaOptionsDir));

        // Drop notifications, Notification Center, junk and the Discord webhook: alive with the engine, UI or not.
        NotificationHub notifications = new(
            engine,
            new NotificationSettingsStore(paths.NotificationSettingsFile),
            new NotificationHistory(paths.NotificationHistoryFile, DiagnosticText.Scrub),
            new DropNotifications(application),
            new WebhookUrlVault(secrets),
            new DiscordWebhookSender(log: CrashCapture.Info),
            DiagnosticText.Secrets,
            log: CrashCapture.Info);
        host.RestartStarting += (_, _) => notifications.History.Flush();

        // The foreground service follows the engine: started as soon as a session or script is active.
        engine.SnapshotChanged += (_, snapshot) => EngineService.EnsureRunningFor(application, snapshot);
        // Heartbeats record the script and connection state (read from the immutable snapshot; no subscription).
        CrashCapture.EngineState = () => (engine.Snapshot.Script.IsPaused ? "Paused" : engine.Snapshot.Script.State.ToString(), engine.Snapshot.Connection.ToString());
        // The army: light sessions in this process, full slots 1 (this engine) and 2..6 (their own processes).
        Skua.App.Droid.Core.Army.LoginStagger stagger = new();
        Skua.App.Droid.Core.Army.ArmyCoordinator army = new(
            new Army.AndroidArmyPlatform(application),
            new Skua.App.Droid.Core.Army.AccountStore(Skua.App.Droid.Core.Army.SlotLayout.AccountsFileOf(paths)),
            new Skua.App.Droid.Core.Army.AccountVault(secrets),
            engine,
            stagger,
            account => new Skua.App.Droid.Core.Army.LightAccount(account, new Skua.App.Droid.Core.Army.LightOptions { Stagger = stagger }),
            paths.ScriptsDir);
        // The memory watchdog of this process restarts only this process; never while light sessions live here.
        host.AutomaticRestartVeto = () => army.LightSessionsActive ? "light army sessions are online in this process" : null;
        // The game view (its own ":gameview" process) and the switch that hands the account to it and back. It is for
        // slot 1 (this engine) only; light army accounts have no Skua engine to hand over.
        Skua.App.Droid.Core.GameView.GameViewSwitch gameView = new(host, new global::Skua.App.Droid.GameView.AndroidGameViewLauncher(application));
        CrashCapture.Info($"Engine ready. Script references: {references.Files.Count} images{(references.IsComplete ? string.Empty : " (incomplete)")}.");
        return new Composition(host, platform, library, notifications, gameView, army);
    }

    /// <summary>The Keystore-backed store, or a store that remembers nothing if the Keystore cannot be used at all.</summary>
    internal static ISecretStore CreateSecretStore(Context context, string directory)
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
    internal static ScriptReferenceSet LoadScriptReferences(Context application)
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

    /// <summary>
    /// The ViewModels' services; called on the UI thread when the first main view is created, which the UI does only
    /// after the composition completed (<see cref="Skua.App.Droid.UI.AppBootstrap.EngineReady"/>).
    /// </summary>
    public static AppServices CreateAppServices()
    {
        Composition composition = s_startup.Value ?? throw NotInitialized();
        return new(
            composition.Host,
            new AvaloniaUiDispatcher(),
            new AvaloniaFilePicker(),
            composition.Library,
            new ScriptRepositorySync(composition.Library, Paths.SkuaDir, warn: CrashCapture.Info),
            Paths)
        {
            Notifications = composition.Notifications,
            GameView = composition.GameView,
            Army = composition.Army,
            DeviceMemory = () => Army.AndroidArmyPlatform.Read(Volatile.Read(ref s_application)!),
        };
    }

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
