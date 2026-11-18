using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Avalonia.Android;
using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Diagnostics;
using Skua.App.Droid.UI;

namespace Skua.App.Droid;

/// <summary>
/// The only UI activity. Avalonia creates a fresh view for every instance (IActivityApplicationLifetime.MainViewFactory)
/// bound to the process-wide ViewModel, so rotation, backgrounding or the activity being destroyed loses nothing; the
/// engine keeps running under <see cref="EngineService"/>.
/// </summary>
/// <remarks>
/// Not the launcher: <see cref="LaunchActivity"/> (plain Android) starts it, or shows the crash screen instead.
/// Not exported, empty task affinity, singleTask: it always lives in its own task that no other app can claim, and
/// every start (launcher, notification, restart) reuses the one instance.
/// </remarks>
[Activity(
    Label = "VibeSkua",
    Exported = false,
    TaskAffinity = "",
    Theme = "@style/VibeSkua.Theme",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard)]
[Register("com.vibeskua.droid.MainActivity")]
public class MainActivity : AvaloniaMainActivity
{
    private static bool s_firstFrameRecorded;
    private readonly ServiceLink _link = new();
    private bool _engineReady;
    private bool _started;
    private bool _resumed;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        CrashCapture.Step(StartupStep.MainActivityCreate);

        // Reached directly (task restored after the process died, notification) while a report is pending: show it
        // on top. LaunchActivity handles the normal launcher path before Avalonia is ever started.
        if (CrashCapture.Store?.HasPendingReport == true)
            TryRun("show the crash screen", () => StartActivity(new Intent(this, typeof(CrashActivity))));

        // Starts composing the engine if Application.OnCreate skipped it or it failed there (no-op otherwise). Never
        // waits: the view shows "starting" until it is done, and OnEngineReady runs the steps that need the engine.
        _ = EngineRuntime.StartInitialization(this);

        ProtectScreenContent();
        ((DroidApplication)Application!).EnsureAvaloniaStarted();
        base.OnCreate(savedInstanceState); // creates the Avalonia view through MainViewFactory
        CrashCapture.Step(StartupStep.MainActivityCreated);

        EngineRuntime.WhenInitialized(this, available =>
        {
            if (available && !IsFinishing && !IsDestroyed)
                OnEngineReady();
        });
    }

    /// <summary>On the main thread, once the engine is composed: what OnCreate/OnStart/OnResume skip without it.</summary>
    private void OnEngineReady()
    {
        _engineReady = true;
        if (_resumed)
            AndroidPlatformServices.CurrentActivity = this;
        TryRun("notification permission request", EngineRuntime.Platform.RequestNotificationPermissionOnFirstRun);

        // Coming back from a planned restart: log back in (the service does it, so it continues in the background).
        TryRun("resume after restart", () =>
        {
            if (File.Exists(EngineRuntime.Paths.ResumeFile))
                EngineService.Start(this, EngineService.ActionResume);
        });
        if (_started)
            BindEngineService();
        if (_resumed)
            TryRun("refresh platform state", () => AppBootstrap.ShellIfCreated?.RefreshPlatformState());
    }

    private void BindEngineService()
    {
        // Bind without auto-create: connects whenever the engine service is running.
        if (!_link.Bound)
            TryRun("bind the engine service", () => _link.Bound = BindService(new Intent(this, typeof(EngineService)), _link, 0));
    }

    protected override void OnStart()
    {
        base.OnStart();
        _started = true;
        if (_engineReady)
            BindEngineService();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _resumed = true;
        CrashCapture.Step(StartupStep.MainActivityResumed);
        if (_engineReady)
        {
            AndroidPlatformServices.CurrentActivity = this;
            TryRun("refresh platform state", () => AppBootstrap.ShellIfCreated?.RefreshPlatformState());
        }
        if (!s_firstFrameRecorded)
        {
            s_firstFrameRecorded = true;
            // Runs after the next layout/draw traversal of the window: the UI is up.
            Window?.DecorView?.Post(() => CrashCapture.Step(StartupStep.FirstFrame));
        }
    }

    protected override void OnPause()
    {
        _resumed = false;
        if (ReferenceEquals(AndroidPlatformServices.CurrentActivity, this))
            AndroidPlatformServices.CurrentActivity = null;
        base.OnPause();
    }

    protected override void OnStop()
    {
        _started = false;
        if (_link.Bound)
            TryRun("unbind the engine service", () => UnbindService(_link));
        _link.Bound = false;
        base.OnStop();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        TryRun("refresh platform state", () => AppBootstrap.ShellIfCreated?.RefreshPlatformState());
    }

    /// <summary>
    /// No screenshots, screen recording, casting to insecure displays or Recents thumbnail of this activity: it shows the
    /// password field, the live log and captured packets.
    /// </summary>
    /// <remarks>
    /// App-wide (the whole activity), not per page. Avalonia draws every page into one SurfaceView, and a SurfaceView
    /// takes the window's FLAG_SECURE when its surface is created, so toggling the flag while switching pages would
    /// not reliably protect (or unprotect) what is already on screen. It is set here, before Avalonia attaches its view.
    /// </remarks>
    private void ProtectScreenContent()
    {
        TryRun("FLAG_SECURE", () => Window?.AddFlags(WindowManagerFlags.Secure));
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            return;
        try
        {
            SetRecentsScreenshotEnabled(false);
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("disable the Recents screenshot", e);
        }
    }

    /// <summary>Runs a non-essential step; a failure is logged and the activity carries on.</summary>
    private static void TryRun(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal(what, e);
        }
    }

    private sealed class ServiceLink : Java.Lang.Object, IServiceConnection
    {
        /// <summary>bindService was accepted (must be matched by unbindService).</summary>
        public bool Bound { get; set; }

        /// <summary>The service is running and connected.</summary>
        public bool Connected { get; private set; }

        public void OnServiceConnected(ComponentName? name, IBinder? service) => Connected = true;

        public void OnServiceDisconnected(ComponentName? name) => Connected = false;
    }
}
