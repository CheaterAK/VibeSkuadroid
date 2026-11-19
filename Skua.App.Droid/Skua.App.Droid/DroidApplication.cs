using Android.App;
using Android.Content;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Diagnostics;
using Skua.App.Droid.UI;

namespace Skua.App.Droid;

/// <summary>
/// The Android Application. Runs once per process before any activity, service or receiver.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Constructor: installs crash capture (it needs nothing else) and starts the startup marker.</item>
/// <item>OnCreate: starts composing the engine on a background thread (process-wide; the foreground service needs it
/// without any UI) and returns. A failure is recorded and the process keeps running without the engine instead of
/// dying.</item>
/// <item>Avalonia is NOT started here but lazily by <see cref="MainActivity"/> (<see cref="EnsureAvaloniaStarted"/>).
/// So the plain-Android <see cref="LaunchActivity"/> and <see cref="CrashActivity"/> work even when Avalonia's startup
/// is what crashes, and a process started only for the service never loads Skia or EGL.</item>
/// </list>
/// The restart trampoline (<see cref="RestartActivity"/>) runs in a separate ":restart" process; there neither the
/// engine nor Avalonia is initialised.
/// </remarks>
[Application]
[Register("com.vibeskua.droid.DroidApplication")]
public sealed class DroidApplication : AvaloniaAndroidApplication<Skua.App.Droid.UI.App>
{
    private bool _avaloniaStarted;

    public DroidApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
        // First managed code of the process: capture crashes before anything else can fail. The base context is
        // already attached here (the runtime creates this peer after Application.attachBaseContext), but a missing
        // directory only delays the file part until OnCreate.
        CrashCapture.InstallHandlers();
        try
        {
            CrashCapture.Attach(this, trackStartup: !ProcessInfo.IsRestartProcess(this));
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn(CrashCapture.Tag, "Crash capture attach deferred: " + e.GetType().Name);
        }
    }

    public override void OnCreate()
    {
        CrashCapture.InstallHandlers();
        bool restartProcess = ProcessInfo.IsRestartProcess(this);
        CrashCapture.Attach(this, trackStartup: !restartProcess);
        if (restartProcess)
            return; // Application.onCreate itself does nothing; skipping base also skips Avalonia here.

        CrashCapture.Step(StartupStep.ApplicationCreate);
        // How the previous run ended, by Android's own records (native crash, ANR, killed for memory): before any
        // activity decides whether to show the crash screen.
        ExitReasons.ScanOnce(this);
        CrashCapture.Lifecycle("process start", UiState.None);
        CrashCapture.StartHeartbeat();
        AppBootstrap.Factory = EngineRuntime.CreateAppServices;
        AppBootstrap.ReportError = (where, e) => CrashCapture.Recovered(where, e);
        AppBootstrap.MainViewCreated = () => CrashCapture.Step(StartupStep.MainViewCreated);
        // The main view waits for the composition (a short "starting" view) instead of the main thread.
        AppBootstrap.EngineReady = () => EngineRuntime.StartInitialization(this);

        if (CrashCapture.PreviousStartup is { DiedInEngineInit: true } && CrashCapture.Store?.HasPendingReport == true)
        {
            // The previous process died inside engine composition without an exception (native crash or abort):
            // do not repeat it before the user has seen the crash screen. MainActivity composes it once they continue.
            CrashCapture.Info("engine composition skipped: the previous start died in it");
        }
        else
        {
            // On a background thread: Application.onCreate (and so the first frame) never waits for it.
            _ = EngineRuntime.StartInitialization(this);
        }
        CrashCapture.Step(StartupStep.ApplicationReady);
    }

    /// <summary>Starts Avalonia (AppBuilder setup) once per process, on the main thread, before the first Avalonia activity.</summary>
    public void EnsureAvaloniaStarted()
    {
        if (_avaloniaStarted)
            return;
        _avaloniaStarted = true;
        CrashCapture.Step(StartupStep.AvaloniaInitStarted);
        // AvaloniaAndroidApplication.OnCreate: Application.onCreate (empty in the framework) plus the AppBuilder setup.
        base.OnCreate();
        CrashCapture.Step(StartupStep.AvaloniaInitFinished);
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();

    /// <summary>Memory pressure as Android reports it: recorded (the next report says how close the process was to a kill).</summary>
    public override void OnTrimMemory([GeneratedEnum] TrimMemory level)
    {
        base.OnTrimMemory(level);
        if (!ProcessInfo.IsRestartProcess(this))
            CrashCapture.Lifecycle($"trimMemory {(int)level} {level}");
    }

    public override void OnLowMemory()
    {
        base.OnLowMemory();
        if (!ProcessInfo.IsRestartProcess(this))
            CrashCapture.Lifecycle("lowMemory");
    }
}
