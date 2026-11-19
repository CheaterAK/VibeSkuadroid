using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Skua.App.Droid.Core.Diagnostics;

namespace Skua.App.Droid.Diagnostics;

/// <summary>
/// Plain Android crash screen (no Avalonia, no engine, no AndroidX): shows the pending crash report and the recent
/// startup log, with Share and Copy. Shown by <see cref="LaunchActivity"/> before Avalonia is started, so it works
/// even when Avalonia or the engine is what crashed. "Continue" clears the report and opens the app.
/// </summary>
/// <remarks>
/// Not exported, no task affinity. The text is scrubbed of registered secrets and credential-like key/value pairs;
/// it holds stack traces and device facts, so screenshots are allowed (no FLAG_SECURE) to make it easy to share.
/// </remarks>
[Activity(
    Label = "VibeSkua crash report",
    Exported = false,
    TaskAffinity = "",
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize
        | Android.Content.PM.ConfigChanges.ScreenLayout | Android.Content.PM.ConfigChanges.SmallestScreenSize)]
[Register("com.vibeskua.droid.CrashActivity")]
public sealed class CrashActivity : Activity
{
    private const int LogLines = 80;

    private string _text = string.Empty;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _text = BuildText();

        int pad = Dp(12);
        LinearLayout root = new(this) { Orientation = Orientation.Vertical };
        root.SetPadding(pad, pad, pad, pad);
        root.SetBackgroundColor(Color.ParseColor("#0A0A0A"));
        root.SetFitsSystemWindows(true);

        TextView title = new(this)
        {
            Text = "VibeSkua stopped unexpectedly last time. Please share this report (Share or Copy), then Continue.",
            TextSize = 16,
        };
        title.SetTextColor(Color.ParseColor("#F5F5F5"));
        root.AddView(title);

        LinearLayout buttons = new(this) { Orientation = Orientation.Horizontal };
        buttons.AddView(MakeButton("Share", Share), Weighted());
        buttons.AddView(MakeButton("Copy", Copy), Weighted());
        buttons.AddView(MakeButton("Continue", Continue), Weighted());
        root.AddView(buttons, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        TextView body = new(this)
        {
            Text = _text,
            TextSize = 11,
            Typeface = Typeface.Monospace,
        };
        body.SetTextColor(Color.ParseColor("#D4D4D4"));
        body.SetTextIsSelectable(true);
        ScrollView scroll = new(this);
        scroll.AddView(body);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(root);
        CrashCapture.Info("crash screen shown");
    }

    private string BuildText()
    {
        try
        {
            DiagnosticsStore? store = CrashCapture.Store;
            if (store is null)
                return "No diagnostics directory is available.";
            string report = store.ReadPendingReport() ?? "(no crash report is pending)";
            string log = string.Join("\n", store.ReadLogTail(LogLines));
            return report + "\n\n--- recent events (" + DiagnosticsStore.LogFile + ") ---\n" + log;
        }
        catch (Exception e)
        {
            return "The crash report could not be read: " + e.GetType().Name;
        }
    }

    private void Share()
    {
        try
        {
            Intent send = new Intent(Intent.ActionSend)
                .SetType("text/plain")!
                .PutExtra(Intent.ExtraSubject, "VibeSkua crash report")!
                .PutExtra(Intent.ExtraText, _text)!;
            StartActivity(Intent.CreateChooser(send, "Share crash report"));
        }
        catch (Exception e)
        {
            Toast.MakeText(this, "Could not open the share sheet: " + e.GetType().Name, ToastLength.Short)?.Show();
        }
    }

    private void Copy()
    {
        try
        {
            ClipboardManager? clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
            clipboard?.PrimaryClip = ClipData.NewPlainText("VibeSkua crash report", _text);
            if (!OperatingSystem.IsAndroidVersionAtLeast(33)) // 13+ shows its own confirmation
                Toast.MakeText(this, "Copied", ToastLength.Short)?.Show();
        }
        catch (Exception e)
        {
            Toast.MakeText(this, "Could not copy: " + e.GetType().Name, ToastLength.Short)?.Show();
        }
    }

    private void Continue()
    {
        CrashCapture.Store?.ClearPendingReport();
        try
        {
            StartActivity(new Intent(this, typeof(MainActivity)));
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("open the app from the crash screen", e);
        }
        Finish();
    }

    private Button MakeButton(string text, Action onClick)
    {
        Button button = new(this) { Text = text };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static LinearLayout.LayoutParams Weighted() => new(0, ViewGroup.LayoutParams.WrapContent, 1f);

    private int Dp(int dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 1f));
}

/// <summary>
/// The launcher entry point: a plain Android activity (no Avalonia) that decides, before Avalonia is started, whether
/// to show the crash screen or the app. It finishes immediately either way.
/// </summary>
/// <remarks>
/// Exported (it is the launcher), with an empty task affinity so no other app's task can claim it. It never shows
/// anything itself, has no history and is excluded from Recents: its task only exists for the instant it forwards.
/// </remarks>
[Activity(
    Label = "VibeSkua",
    MainLauncher = true,
    Exported = true,
    TaskAffinity = "",
    NoHistory = true,
    ExcludeFromRecents = true,
    Theme = "@style/VibeSkua.Theme",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round")]
[Register("com.vibeskua.droid.LaunchActivity")]
public sealed class LaunchActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        CrashCapture.Step(StartupStep.LauncherActivity);
        ExitReasons.ScanOnce(this); // normally done in Application.onCreate already (then a no-op)
        bool crashPending = CrashCapture.Store?.HasPendingReport == true;
        try
        {
            if (crashPending)
                StartActivity(new Intent(this, typeof(CrashActivity)).AddFlags(ActivityFlags.NewTask));
            else
                StartActivity(new Intent(this, typeof(MainActivity)));
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("forward from the launcher", e);
        }
        Finish();
    }
}
