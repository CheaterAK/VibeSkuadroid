using Android.App;
using Android.Content;
using Android.OS;
using Environment = System.Environment;
using Skua.App.Droid.Core.Army;
using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scheduler;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;
using Skua.App.Droid.Diagnostics;
using Skua.App.Droid.Scripting;

namespace Skua.App.Droid.Army;

/// <summary>What a full slot process composed: its slot host (engine, settings, watchdog, queue) and the endpoint the coordinator talks to.</summary>
internal sealed record SlotComposition(SlotHost Host, SlotEndpoint Endpoint);

/// <summary>
/// Composition root of a full army slot process (":acct2" .. ":acct6"): the same engine as the main process (Skua.Core
/// host, script references, memory watchdog, script queue) on the slot's own folders (<see cref="SlotLayout"/>), no UI,
/// no Avalonia, no notification hub. Started from <see cref="DroidApplication.OnCreate"/> in that process; the slot
/// service waits for it.
/// </summary>
internal static class SlotRuntime
{
    private static readonly EngineStartup<SlotComposition> s_startup = new(Compose, OnStartupChanged);
    private static Context? s_application;
    private static SlotLayout? s_layout;

    /// <summary>This process's slot (2..6), or 0 when this is not a slot process.</summary>
    public static int Slot { get; private set; }

    public static SlotLayout Layout => s_layout ?? throw new InvalidOperationException("Not a slot process.");

    public static SlotComposition? Value => s_startup.Value;

    /// <summary>The slot of this process from its name, or null (main or ":restart" process).</summary>
    public static int? Detect() => SlotIds.FromProcessName(ProcessInfo.CurrentName());

    /// <summary>
    /// The cheap, process-global part, on the main thread before anything reads the environment: HOME and TMPDIR of
    /// the slot (Skua.Core captures <c>$HOME/.config</c> once), its folders and its links to the shared scripts.
    /// </summary>
    public static void PreparePaths(Context context, int slot)
    {
        Context application = context.ApplicationContext ?? context;
        AppPaths main = new(application.FilesDir!.AbsolutePath, application.NoBackupFilesDir!.AbsolutePath, application.CacheDir!.AbsolutePath);
        SlotLayout layout = SlotLayout.For(main, slot);
        Environment.SetEnvironmentVariable("HOME", layout.Paths.FilesDir);
        Environment.SetEnvironmentVariable("TMPDIR", layout.Paths.CacheDir);
        IReadOnlyList<string> problems = layout.Prepare();
        foreach (string problem in problems.Take(5))
            CrashCapture.Info($"slot {slot} layout: {problem}");
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.Equals(appData.TrimEnd('/'), layout.Paths.ApplicationDataDir, StringComparison.Ordinal))
            CrashCapture.Info($"slot {slot}: ApplicationData resolved to {appData}, expected {layout.Paths.ApplicationDataDir}.");
        s_layout = layout;
        s_application = application;
        Slot = slot;
    }

    /// <summary>Starts composing (once; a failed attempt is retried by the next call). Completes with whether the slot is available.</summary>
    public static Task<bool> StartInitialization()
    {
        if (Slot == 0)
            return Task.FromResult(false);
        Task<SlotComposition?> attempt = s_startup.EnsureStarted();
        return attempt.ContinueWith(t => t.Result is not null, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void OnStartupChanged(EngineStartupState state, Exception? error)
    {
        if (state == EngineStartupState.Failed)
            CrashCapture.Recovered($"slot {Slot} composition", error!);
        else if (state == EngineStartupState.Ready)
            CrashCapture.Info($"slot {Slot}: engine ready");
    }

    private static SlotComposition Compose()
    {
        Context application = s_application ?? throw new InvalidOperationException("No application context.");
        SlotLayout layout = Layout;
        AppPaths paths = layout.Paths;
        SettingsStore settings = new(paths.SettingsFile);
        // The one Keystore-backed store of the app: this process decrypts only the entry of the account it logs in.
        ISecretStore secrets = EngineRuntime.CreateSecretStore(application, paths.SecretsDir);
        ScriptReferenceSet references = EngineRuntime.LoadScriptReferences(application);
        IScriptHost scripts = new SkuaScriptHostAdapter(references, new ScriptAssemblyStore(), paths.ScriptsDir)
        {
            // This slot's own compiler process (":compilerN", this slot's HOME and cache); one compile at a time app-wide.
            Precompiler = new ScriptPrecompiler(new Compiler.CompilerClient(application, Slot), references.Identity,
                () => ScriptPrecompiler.RefuseInProcessFor(AndroidArmyPlatform.Read(application))),
        };
        HeadlessBotEngine engine = new(new HeadlessEngineOptions
        {
            AutoReconnect = settings.Current.AutoReconnect,
            Scrubber = DiagnosticText.Secrets,
        }, scripts);
        EngineHost host = new(engine, settings, new CredentialVault(secrets), new ResumeStore(paths.ResumeFile), new SlotPlatformServices(application, Slot));
        host.AttachScheduler(new ScriptScheduler(engine, new QueueStore(paths.QueueFile, paths.PlaylistsDir), paths.ScriptsDir, paths.SkuaOptionsDir));
        SlotHost slot = new(Slot, host, layout, new AccountStore(layout.AccountsFile), new AccountVault(secrets));
        SlotEndpoint endpoint = new(slot);
        engine.SnapshotChanged += (_, s) => SlotEngineService.OnSnapshot(application, s);
        CrashCapture.EngineState = () => (engine.Snapshot.Script.IsPaused ? "Paused" : engine.Snapshot.Script.State.ToString(), engine.Snapshot.Connection.ToString());
        return new SlotComposition(slot, endpoint);
    }
}

/// <summary>
/// <see cref="IPlatformServices"/> of a slot process: no UI; a restart restarts only this slot's process and brings its
/// service back (which logs the slot's account back in).
/// </summary>
internal sealed class SlotPlatformServices : IPlatformServices
{
    private readonly Context _context;
    private readonly int _slot;

    public SlotPlatformServices(Context context, int slot)
    {
        _context = context;
        _slot = slot;
    }

    public string DeviceDescription => $"Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}), slot {_slot}";

    public string AppVersion => "slot";

    public bool NotificationsAllowed => true;

    public void RequestNotificationPermission()
    {
    }

    public bool IgnoringBatteryOptimizations => true;

    public void RequestIgnoreBatteryOptimizations()
    {
    }

    public bool CanRestartProcess => true;

    public void RestartProcess(string reason, bool resumeLogin)
    {
        Android.Util.Log.Info(CrashCapture.Tag, $"Restarting slot {_slot}: {reason}");
        try
        {
            SlotRuntime.Value?.Host.Engine.LogoutAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
        // The alarm brings this slot's service back in a fresh process (Android 12+: when the app may start a
        // foreground service from the background, e.g. exempt from battery optimisation); the coordinator also
        // restarts a slot it sees die while it should run.
        try
        {
            if (_context.GetSystemService(Context.AlarmService) is AlarmManager alarms)
            {
                PendingIntent pending = PendingIntent.GetForegroundService(_context, 100 + _slot,
                    new Intent(_context, SlotEngineService.TypeFor(_slot)).SetAction(SlotEngineService.ActionResume),
                    PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
                alarms.SetAndAllowWhileIdle(AlarmType.ElapsedRealtimeWakeup, SystemClock.ElapsedRealtime() + 2000, pending);
            }
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("scheduling the slot restart", e);
        }
        CrashCapture.PlannedExit($"slot {_slot} restart: {reason}");
        Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
    }

    public void ShareText(string subject, string text)
    {
    }

    public bool EngineServiceRunning => true;
}
