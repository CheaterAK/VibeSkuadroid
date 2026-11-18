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

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        CrashCapture.Step(StartupStep.MainActivityCreate);

        // Reached directly (task restored after the process died, notification) while a report is pending: show it
        // on top. LaunchActivity handles the normal launcher path before Avalonia is ever started.
        if (CrashCapture.Store?.HasPendingReport == true)
            TryRun("show the crash screen", () => StartActivity(new Intent(this, typeof(CrashActivity))));

        // Composes the engine if Application.OnCreate skipped it or it failed there (no-op otherwise).
        bool engine = EngineRuntime.TryInitialize(this);

        ((DroidApplication)Application!).EnsureAvaloniaStarted();
        base.OnCreate(savedInstanceState); // creates the Avalonia view through MainViewFactory
        CrashCapture.Step(StartupStep.MainActivityCreated);

        if (!engine)
            return;
        AndroidPlatformServices.CurrentActivity = this;
        TryRun("notification permission request", EngineRuntime.Platform.RequestNotificationPermissionOnFirstRun);

        // Coming back from a planned restart: log back in (the service does it, so it continues in the background).
        TryRun("resume after restart", () =>
        {
            if (File.Exists(EngineRuntime.Paths.ResumeFile))
                EngineService.Start(this, EngineService.ActionResume);
        });
    }

    protected override void OnStart()
    {
        base.OnStart();
        // Bind without auto-create: connects whenever the engine service is running.
        if (EngineRuntime.IsInitialized)
            TryRun("bind the engine service", () => _link.Bound = BindService(new Intent(this, typeof(EngineService)), _link, 0));
    }

    protected override void OnResume()
    {
        base.OnResume();
        CrashCapture.Step(StartupStep.MainActivityResumed);
        if (EngineRuntime.IsInitialized)
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
        if (ReferenceEquals(AndroidPlatformServices.CurrentActivity, this))
            AndroidPlatformServices.CurrentActivity = null;
        base.OnPause();
    }

    protected override void OnStop()
    {
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
