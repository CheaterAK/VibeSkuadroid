using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Skua.App.Droid.Core.Platform;

namespace Skua.App.Droid;

/// <summary><see cref="IPlatformServices"/> on Android. Called from the UI thread.</summary>
internal sealed class AndroidPlatformServices : IPlatformServices
{
    public const int NotificationPermissionRequest = 4101;
    private const string UiPrefs = "ui";
    private const string AskedNotificationsKey = "asked_notifications";

    private readonly Context _context;

    public AndroidPlatformServices(Context context)
    {
        _context = context;
    }

    /// <summary>The resumed activity, if any (set by <see cref="MainActivity"/>).</summary>
    public static Activity? CurrentActivity { get; set; }

    public string DeviceDescription => $"Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}), {Build.Manufacturer} {Build.Model}";

    public string AppVersion
    {
        get
        {
            try
            {
                PackageInfo? info = _context.PackageManager?.GetPackageInfo(_context.PackageName!, 0);
                return info?.VersionName ?? "?";
            }
            catch (PackageManager.NameNotFoundException)
            {
                return "?";
            }
        }
    }

    public bool NotificationsAllowed => NotificationManagerCompat.From(_context)!.AreNotificationsEnabled();

    public void RequestNotificationPermission()
    {
        Activity? activity = CurrentActivity;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && activity is not null
            && ContextCompat.CheckSelfPermission(_context, Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            ISharedPreferences prefs = _context.GetSharedPreferences(UiPrefs, FileCreationMode.Private)!;
            bool askedBefore = prefs.GetBoolean(AskedNotificationsKey, false);
            // After a second "deny" Android stops showing the prompt; then only the settings page can change it.
            if (!askedBefore || ActivityCompat.ShouldShowRequestPermissionRationale(activity, Manifest.Permission.PostNotifications))
            {
                prefs.Edit()!.PutBoolean(AskedNotificationsKey, true)!.Apply();
                ActivityCompat.RequestPermissions(activity, [Manifest.Permission.PostNotifications], NotificationPermissionRequest);
                return;
            }
        }
        OpenSettings(new Intent(Android.Provider.Settings.ActionAppNotificationSettings)
            .PutExtra(Android.Provider.Settings.ExtraAppPackage, _context.PackageName));
    }

    /// <summary>Asks once, on first start, on Android 13+.</summary>
    public void RequestNotificationPermissionOnFirstRun()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || NotificationsAllowed)
            return;
        ISharedPreferences prefs = _context.GetSharedPreferences(UiPrefs, FileCreationMode.Private)!;
        if (!prefs.GetBoolean(AskedNotificationsKey, false))
            RequestNotificationPermission();
    }

    public bool IgnoringBatteryOptimizations
    {
        get
        {
            PowerManager? power = (PowerManager?)_context.GetSystemService(Context.PowerService);
            return power?.IsIgnoringBatteryOptimizations(_context.PackageName) ?? false;
        }
    }

    public void RequestIgnoreBatteryOptimizations()
    {
        Intent direct = new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations)
            .SetData(Android.Net.Uri.Parse("package:" + _context.PackageName))!;
        if (!OpenSettings(direct))
            OpenSettings(new Intent(Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings));
    }

    public bool CanRestartProcess => true;

    public void RestartProcess(string reason, bool resumeLogin) =>
        ProcessRestarter.Restart(_context, reason, uiVisible: CurrentActivity is not null);

    public void ShareText(string subject, string text)
    {
        Intent send = new Intent(Intent.ActionSend)
            .SetType("text/plain")!
            .PutExtra(Intent.ExtraSubject, subject)!
            .PutExtra(Intent.ExtraText, text)!;
        Intent chooser = Intent.CreateChooser(send, subject)!;
        Start(chooser);
    }

    public bool EngineServiceRunning => EngineService.IsRunning;

    private bool OpenSettings(Intent intent)
    {
        try
        {
            Start(intent);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;
        }
    }

    private void Start(Intent intent)
    {
        Activity? activity = CurrentActivity;
        if (activity is not null)
        {
            activity.StartActivity(intent);
            return;
        }
        intent.AddFlags(ActivityFlags.NewTask);
        try
        {
            _context.StartActivity(intent);
        }
        catch (AndroidRuntimeException e)
        {
            Log.Warn(EngineRuntime.LogTag, "Could not open a system screen without an activity: " + e.GetType().Name);
        }
    }
}
