using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Util;

namespace Skua.App.Droid;

/// <summary>
/// Restarts the whole app process. This is the only way to give back memory held by script assemblies: Mono on
/// Android never collects AssemblyLoadContexts.
/// </summary>
/// <remarks>
/// Sequence (EngineHost.RequestRestart has already written the resume hand-over and the service has dropped its
/// notification and locks):
/// <list type="number">
/// <item>Disconnect the game session cleanly (bounded wait).</item>
/// <item>Schedule <see cref="RestartReceiver"/> through AlarmManager in ~2 s. It starts the engine service in the new
/// process, which logs back in. Android 12+ only allows that background start when the app is exempt from battery
/// optimisation (the app asks for it); otherwise the receiver posts a "tap to reconnect" notification.</item>
/// <item>If the UI is showing, start <see cref="RestartActivity"/> in the separate ":restart" process; it waits for this
/// process to die and reopens the main activity, which also triggers the resume.</item>
/// <item>Kill this process.</item>
/// </list>
/// The resume hand-over is consumed once, so whichever path runs first wins and the other does nothing.
/// </remarks>
internal static class ProcessRestarter
{
    public const string ExtraPid = "com.vibeskua.droid.extra.PID";

    public static void Restart(Context context, string reason, bool uiVisible)
    {
        Log.Info(EngineRuntime.LogTag, $"Restarting the process: {reason}");
        try
        {
            EngineRuntime.Host.Engine.LogoutAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException e)
        {
            Log.Warn(EngineRuntime.LogTag, "Disconnect before restart failed: " + e.InnerException?.GetType().Name);
        }

        ScheduleReceiver(context);

        if (uiVisible)
        {
            Intent trampoline = new Intent(context, typeof(RestartActivity))
                .PutExtra(ExtraPid, Process.MyPid())
                .AddFlags(ActivityFlags.NewTask | ActivityFlags.NoAnimation);
            context.StartActivity(trampoline);
        }

        Process.KillProcess(Process.MyPid());
    }

    private static void ScheduleReceiver(Context context)
    {
        AlarmManager? alarms = (AlarmManager?)context.GetSystemService(Context.AlarmService);
        if (alarms is null)
            return;
        PendingIntent pending = PendingIntent.GetBroadcast(context, 0,
            new Intent(context, typeof(RestartReceiver)).SetAction(RestartReceiver.ActionRestart),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        // Inexact (no SCHEDULE_EXACT_ALARM permission needed); fires promptly while the device is awake.
        alarms.SetAndAllowWhileIdle(AlarmType.ElapsedRealtimeWakeup, SystemClock.ElapsedRealtime() + 2000, pending);
    }
}

/// <summary>Fired by the restart alarm in the new process: starts the engine service so it can log back in.</summary>
[BroadcastReceiver(Exported = false, Enabled = true)]
[Register("com.vibeskua.droid.RestartReceiver")]
public sealed class RestartReceiver : BroadcastReceiver
{
    public const string ActionRestart = "com.vibeskua.droid.action.RESTARTED";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != ActionRestart)
            return;
        if (!EngineRuntime.TryInitialize(context))
            return; // recorded by TryInitialize; nothing to resume without an engine
        if (!File.Exists(EngineRuntime.Paths.ResumeFile))
            return; // already resumed by the reopened activity
        if (!EngineService.Start(context, EngineService.ActionResume))
            EngineService.PostRestartNotice(context, "The engine restarted to free memory. Tap to reconnect.");
    }
}

/// <summary>
/// Restart trampoline in its own ":restart" process (no engine, no Avalonia there): waits until the old main process
/// is gone, reopens the main activity, then exits. Started only while the UI is visible, so starting an activity is
/// allowed.
/// </summary>
[Activity(
    Exported = false,
    Process = ProcessInfo.RestartProcessSuffix,
    TaskAffinity = "",
    Theme = "@style/VibeSkua.Restart",
    NoHistory = true,
    ExcludeFromRecents = true)]
[Register("com.vibeskua.droid.RestartActivity")]
public sealed class RestartActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        int oldPid = Intent?.GetIntExtra(ProcessRestarter.ExtraPid, -1) ?? -1;
        for (int i = 0; i < 40 && oldPid > 0 && Directory.Exists($"/proc/{oldPid}"); i++)
            Thread.Sleep(50);

        Intent main = new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
        StartActivity(main);
        Finish();
        Process.KillProcess(Process.MyPid());
    }
}
