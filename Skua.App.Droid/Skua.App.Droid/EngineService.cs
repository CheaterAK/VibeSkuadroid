using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using Android.Runtime;
using Android.Util;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Diagnostics;

namespace Skua.App.Droid;

/// <summary>
/// Foreground service that keeps the process (and so the engine, its socket and the script thread) alive while
/// the UI is in the background or gone. It owns the status notification (with a Stop action), the partial wake
/// lock and Wi-Fi lock while a script runs, and resuming after a planned restart.
/// </summary>
/// <remarks>
/// Declared in AndroidManifest.xml (not exported, foregroundServiceType specialUse). The engine itself is the
/// process-wide <see cref="EngineRuntime.Host"/>; this object can be destroyed and recreated without touching it.
/// </remarks>
[Register("com.vibeskua.droid.EngineService")]
public sealed class EngineService : Service
{
    public const string ActionStart = "com.vibeskua.droid.action.START";
    public const string ActionStop = "com.vibeskua.droid.action.STOP";
    public const string ActionResume = "com.vibeskua.droid.action.RESUME";

    private const int NotificationId = 1001;
    private const int RestartNoticeId = 1002;
    private const string ChannelId = "engine";
    private const string NoticeChannelId = "notices";

    /// <summary>How long the service stays up with nothing to do (offline, no script) before it stops itself.</summary>
    private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(60);

    private static volatile bool s_running;
    private static long s_lastStartAttempt;

    private readonly LocalBinder _binder;
    private EngineHost? _host;
    private Handler? _main;
    private PowerManager.WakeLock? _wakeLock;
    private WifiManager.WifiLock? _wifiLock;
    private EngineSnapshot? _latest;
    private long _lastNotifyTicks;
    private bool _notifyPosted;
    private DateTimeOffset? _idleSince;

    public EngineService()
    {
        _binder = new LocalBinder(this);
    }

    public EngineService(nint handle, JniHandleOwnership transfer)
        : base(handle, transfer)
    {
        _binder = new LocalBinder(this);
    }

    public static bool IsRunning => s_running;

    /// <summary>Starts the service if the engine needs it (session or script active) and it is not running.</summary>
    public static void EnsureRunningFor(Context context, EngineSnapshot snapshot)
    {
        if (!snapshot.NeedsForegroundService || s_running)
            return;
        // At most one attempt per 5 s: snapshots arrive several times a second.
        long now = System.Environment.TickCount64;
        if (now - Interlocked.Read(ref s_lastStartAttempt) < 5000)
            return;
        Interlocked.Exchange(ref s_lastStartAttempt, now);
        Start(context, ActionStart);
    }

    /// <summary>startForegroundService with the given action; false when Android refused (background start limits).</summary>
    public static bool Start(Context context, string action)
    {
        try
        {
            ContextCompat.StartForegroundService(context, new Intent(context, typeof(EngineService)).SetAction(action));
            return true;
        }
        catch (Java.Lang.Exception e)
        {
            // ForegroundServiceStartNotAllowedException (Android 12+) or IllegalStateException (8-11) when in background.
            Log.Warn(EngineRuntime.LogTag, $"Could not start the engine service: {e.GetType().Name}");
            return false;
        }
    }

    public override IBinder OnBind(Intent? intent) => _binder;

    public override void OnCreate()
    {
        base.OnCreate();
        _main = new Handler(Looper.MainLooper!);
        CreateChannels(this);
        s_running = true;
        // Started by the system (sticky restart) or the restart alarm in a fresh process: compose the engine if
        // Application.OnCreate could not. Without it the service only satisfies startForeground and stops.
        if (EngineRuntime.TryInitialize(this))
        {
            _host = EngineRuntime.Host;
            _host.Engine.SnapshotChanged += OnSnapshot;
            _host.RestartStarting += OnRestartStarting;
        }
        CrashCapture.Info(_host is null ? "Engine service created without an engine; it will stop." : "Engine service created.");
    }

    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        // Must be in the foreground within seconds of startForegroundService, whatever the action.
        bool foreground = PromoteToForeground();
        if (_host is null || !foreground)
        {
            StopService();
            return StartCommandResult.NotSticky;
        }
        switch (intent?.Action)
        {
            case ActionStop:
                _ = StopRequestedAsync();
                break;
            case ActionResume:
            case null: // restarted by the system after the process was killed (START_STICKY)
                _ = ResumeAsync();
                break;
        }
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        if (_host is not null)
        {
            _host.Engine.SnapshotChanged -= OnSnapshot;
            _host.RestartStarting -= OnRestartStarting;
        }
        ReleaseLocks();
        s_running = false;
        Log.Info(EngineRuntime.LogTag, "Engine service destroyed.");
        base.OnDestroy();
    }

    /// <summary>startForeground with the specialUse type on 14+ (no type before: the constant does not exist there).</summary>
    private bool PromoteToForeground()
    {
        try
        {
            Notification notification = BuildNotification(_host?.Engine.Snapshot ?? EngineSnapshot.Initial);
            int type = OperatingSystem.IsAndroidVersionAtLeast(34) ? (int)ForegroundService.TypeSpecialUse : 0;
            ServiceCompat.StartForeground(this, NotificationId, notification, type);
            return true;
        }
        catch (Exception e)
        {
            // ForegroundServiceStartNotAllowedException, a missing FGS permission (SecurityException) or a bad
            // notification: never crash the process for the status notification.
            CrashCapture.NonFatal("starting the engine service in the foreground", e);
            return false;
        }
    }

    private async Task StopRequestedAsync()
    {
        EngineHost host = _host!;
        try
        {
            if (host.Engine.Snapshot.Script.IsBusy)
            {
                await host.Engine.StopScriptAsync();
                return; // stay connected; a second Stop logs out
            }
            await host.LogoutAsync();
        }
        catch (EngineException e)
        {
            Log.Warn(EngineRuntime.LogTag, "Stop: " + e.Message);
        }
        _main?.Post(StopService);
    }

    private async Task ResumeAsync()
    {
        try
        {
            if (_host is null)
                return;
            ResumeResult? result = await _host.ResumeAfterRestartAsync();
            if (result is null)
                return;
            CrashCapture.Info(result.Message);
            // Could not log back in unattended (no password, Keystore unavailable right now, login failed): the
            // same "tap to reconnect" notice as when Android refused to restart the service.
            if (result.NeedsUser)
                PostRestartNotice(this, "The engine restarted but could not log back in by itself. Tap to reconnect.");
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("resume after restart", e);
        }
    }

    private void OnSnapshot(object? sender, EngineSnapshot snapshot)
    {
        _latest = snapshot;
        _main?.Post(() => Apply(snapshot));
    }

    private void Apply(EngineSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, _latest) || _host is null)
            return;

        UpdateLocks(snapshot.Script.IsBusy);

        bool idle = !snapshot.NeedsForegroundService;
        if (idle)
        {
            _idleSince ??= DateTimeOffset.UtcNow;
            if (DateTimeOffset.UtcNow - _idleSince > IdleStop)
            {
                StopService();
                return;
            }
        }
        else
        {
            _idleSince = null;
        }

        // Android throttles notification updates; once a second is plenty.
        long now = System.Environment.TickCount64;
        if (now - _lastNotifyTicks < 1000)
        {
            if (!_notifyPosted)
            {
                _notifyPosted = true;
                _main?.PostDelayed(() =>
                {
                    _notifyPosted = false;
                    if (_latest is EngineSnapshot latest)
                        Apply(latest);
                }, 1000);
            }
            return;
        }
        _lastNotifyTicks = now;
        try
        {
            NotificationManagerCompat.From(this)!.Notify(NotificationId, BuildNotification(snapshot));
        }
        catch (Java.Lang.SecurityException)
        {
            // POST_NOTIFICATIONS denied: the service keeps running, its notification is just not shown.
        }
    }

    private Notification BuildNotification(EngineSnapshot s)
    {
        string title = s.Connection switch
        {
            ConnectionPhase.Online => $"Online on {s.ServerName}",
            ConnectionPhase.LoggingIn or ConnectionPhase.Connecting => "Connecting...",
            ConnectionPhase.Reconnecting => "Reconnecting...",
            ConnectionPhase.Failed => "Disconnected",
            _ => "Engine idle",
        };
        string script = s.Script.State switch
        {
            ScriptRunState.Running => $"Running {s.Script.ScriptName}",
            ScriptRunState.Compiling => $"Compiling {s.Script.ScriptName}",
            ScriptRunState.Stopping => "Stopping script",
            ScriptRunState.Faulted => "Script failed",
            _ => "No script running",
        };
        if (s.BackgroundWork is string work)
            script = s.Script.IsBusy ? $"{script} · {work}" : work;
        string memory = s.Memory.RssBytes > 0 ? $" · {s.Memory.RssMb:0} MB" : string.Empty;
        string character = s.Character is CharacterState c ? $"{c.Name} (lvl {c.Level}) · {c.Map} · " : string.Empty;

        PendingIntent open = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        PendingIntent stop = PendingIntent.GetService(this, 1,
            new Intent(this, typeof(EngineService)).SetAction(ActionStop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

        // Shown on a secure lock screen instead of the full one: no character name or map.
        Notification publicVersion = new NotificationCompat.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.ic_stat_engine)!
            .SetContentTitle("VibeSkua")!
            .SetContentText(s.Script.IsBusy ? "Script running" : title)!
            .Build()!;

        return new NotificationCompat.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.ic_stat_engine)!
            .SetContentTitle(title)!
            .SetContentText(character + script + memory)!
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(character + script + memory))!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetShowWhen(false)!
            .SetCategory(NotificationCompat.CategoryService)!
            .SetVisibility(NotificationCompat.VisibilityPrivate)!
            .SetPublicVersion(publicVersion)!
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)!
            .SetContentIntent(open)!
            .AddAction(0, s.Script.IsBusy ? "Stop script" : "Stop", stop)!
            .Build()!;
    }

    private void UpdateLocks(bool scriptRunning)
    {
        if (scriptRunning)
        {
            if (_wakeLock is null)
            {
                PowerManager power = (PowerManager)GetSystemService(PowerService)!;
                _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "VibeSkua:script")!;
                _wakeLock.SetReferenceCounted(false);
                _wakeLock.Acquire();
                Log.Info(EngineRuntime.LogTag, "Wake lock acquired (script running).");
            }
            if (_wifiLock is null && _host?.Settings.Current.WifiLockWhileScriptRuns == true
                && GetSystemService(WifiService) is WifiManager wifi)
            {
                Android.Net.WifiMode mode = OperatingSystem.IsAndroidVersionAtLeast(29) ? Android.Net.WifiMode.FullLowLatency : Android.Net.WifiMode.FullHighPerf;
                _wifiLock = wifi.CreateWifiLock(mode, "VibeSkua:script")!;
                _wifiLock.SetReferenceCounted(false);
                _wifiLock.Acquire();
            }
        }
        else
        {
            ReleaseLocks();
        }
    }

    private void ReleaseLocks()
    {
        if (_wakeLock is { IsHeld: true })
        {
            _wakeLock.Release();
            Log.Info(EngineRuntime.LogTag, "Wake lock released.");
        }
        _wakeLock = null;
        if (_wifiLock is { IsHeld: true })
            _wifiLock.Release();
        _wifiLock = null;
    }

    private void OnRestartStarting(object? sender, string reason) => _main?.Post(StopService);

    private void StopService()
    {
        ReleaseLocks();
        try
        {
            ServiceCompat.StopForeground(this, ServiceCompat.StopForegroundRemove);
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("stopForeground", e);
        }
        StopSelf();
    }

    /// <summary>Posts a tap-to-open notice (used when Android refused to restart the service from the background).</summary>
    public static void PostRestartNotice(Context context, string text)
    {
        CreateChannels(context);
        PendingIntent open = PendingIntent.GetActivity(context, 2, new Intent(context, typeof(MainActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        Notification notice = new NotificationCompat.Builder(context, NoticeChannelId)
            .SetSmallIcon(Resource.Drawable.ic_stat_engine)!
            .SetContentTitle("VibeSkua")!
            .SetContentText(text)!
            .SetAutoCancel(true)!
            .SetContentIntent(open)!
            .Build()!;
        try
        {
            NotificationManagerCompat.From(context)!.Notify(RestartNoticeId, notice);
        }
        catch (Java.Lang.SecurityException)
        {
            // Notifications not permitted.
        }
    }

    private static void CreateChannels(Context context)
    {
        try
        {
            CreateChannelsCore(context);
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("creating notification channels", e);
        }
    }

    private static void CreateChannelsCore(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;
        NotificationManager manager = (NotificationManager)context.GetSystemService(NotificationService)!;
        NotificationChannel engine = new(ChannelId, "Engine status", NotificationImportance.Low)
        {
            Description = "Connection and script status while the engine runs in the background.",
            LockscreenVisibility = NotificationVisibility.Private,
        };
        engine.SetShowBadge(false);
        manager.CreateNotificationChannel(engine);
        manager.CreateNotificationChannel(new NotificationChannel(NoticeChannelId, "Notices", NotificationImportance.Default)
        {
            Description = "Engine restarts that need you to open the app.",
        });
    }

    /// <summary>Same-process binder: the activity binds to know the service is up (the engine itself is process-wide).</summary>
    public sealed class LocalBinder : Binder
    {
        public LocalBinder(EngineService service)
        {
            Service = service;
        }

        public EngineService Service { get; }
    }
}
