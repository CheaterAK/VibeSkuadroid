using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Avalonia.Android;
using Skua.App.Droid.UI;

namespace Skua.App.Droid;

/// <summary>
/// The only UI activity. Avalonia creates a fresh view for every instance (IActivityApplicationLifetime.MainViewFactory)
/// bound to the process-wide ViewModel, so rotation, backgrounding or the activity being destroyed loses nothing; the
/// engine keeps running under <see cref="EngineService"/>.
/// </summary>
[Activity(
    Label = "VibeSkua",
    MainLauncher = true,
    Exported = true,
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
    private readonly ServiceLink _link = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidPlatformServices.CurrentActivity = this;
        EngineRuntime.Platform.RequestNotificationPermissionOnFirstRun();

        // Coming back from a planned restart: log back in (the service does it, so it continues in the background).
        if (File.Exists(EngineRuntime.Paths.ResumeFile))
            EngineService.Start(this, EngineService.ActionResume);
    }

    protected override void OnStart()
    {
        base.OnStart();
        // Bind without auto-create: connects whenever the engine service is running.
        _link.Bound = BindService(new Intent(this, typeof(EngineService)), _link, 0);
    }

    protected override void OnResume()
    {
        base.OnResume();
        AndroidPlatformServices.CurrentActivity = this;
        AppBootstrap.ShellIfCreated?.RefreshPlatformState();
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
            UnbindService(_link);
        _link.Bound = false;
        base.OnStop();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AppBootstrap.ShellIfCreated?.RefreshPlatformState();
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
