using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Diagnostics;

namespace Skua.App.Droid.Army;

/// <summary>
/// The foreground service of one full army slot, in that slot's own process (":acct2" .. ":acct6", declared in
/// AndroidManifest.xml: not exported, protected by the signature-level permission <c>...permission.SLOT_IPC</c>,
/// foregroundServiceType specialUse). It keeps the slot process alive and serves the coordinator over a
/// <see cref="Messenger"/>: each request is a Message whose Bundle carries the encoded <c>SlotMessage</c>; the answer
/// goes to <c>replyTo</c>, and status events go to every coordinator that sent a request.
/// </summary>
/// <remarks>
/// Security: Android only lets this app's own UID bind a non-exported service, the permission additionally requires the
/// app's signature, and every message is checked again for <c>sendingUid == myUid</c>. Intents carry only an action;
/// credentials never travel: the slot decrypts its account's password from the Keystore vault itself.
/// Notification: every slot shares the main engine's notification id, so the app shows ONE persistent notification
/// however many processes run a foreground service (Android keeps a notification while any foreground service of the
/// app still uses its id); the coordinator writes the combined text into it.
/// </remarks>
public abstract class SlotEngineService : Service
{
    public const string ActionStart = "com.vibeskua.droid.action.SLOT_START";
    public const string ActionResume = "com.vibeskua.droid.action.SLOT_RESUME";
    public const string ActionShutdown = "com.vibeskua.droid.action.SLOT_SHUTDOWN";
    private const string PayloadKey = "p";
    private const int WhatRequest = 1;
    private const int WhatEvent = 2;

    private static SlotEngineService? s_current;

    private readonly List<Messenger> _clients = new();
    private Messenger? _messenger;
    private HandlerThread? _thread;
    private PowerManager.WakeLock? _wakeLock;
    private bool _endpointHooked;

    protected SlotEngineService()
    {
    }

    protected SlotEngineService(nint handle, JniHandleOwnership transfer)
        : base(handle, transfer)
    {
    }

    /// <summary>The service class of a slot (one per process: a component runs in exactly one process).</summary>
    public static Type TypeFor(int slot) => slot switch
    {
        2 => typeof(SlotService2),
        3 => typeof(SlotService3),
        4 => typeof(SlotService4),
        5 => typeof(SlotService5),
        6 => typeof(SlotService6),
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public override void OnCreate()
    {
        base.OnCreate();
        s_current = this;
        EngineService.CreateChannels(this);
        _thread = new HandlerThread($"slot-ipc-{SlotRuntime.Slot}");
        _thread.Start();
        _messenger = new Messenger(new IncomingHandler(_thread.Looper!, OnMessage));
        _ = SlotRuntime.StartInitialization();
        CrashCapture.Lifecycle($"slot {SlotRuntime.Slot} service created");
    }

    public override IBinder? OnBind(Intent? intent) => _messenger?.Binder;

    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        if (!PromoteToForeground())
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        string? action = intent?.Action;
        _ = RunAsync(action);
        return StartCommandResult.Sticky;
    }

    private async Task RunAsync(string? action)
    {
        bool available = await SlotRuntime.StartInitialization().ConfigureAwait(false);
        if (!available || SlotRuntime.Value is not SlotComposition slot)
        {
            new Handler(Looper.MainLooper!).Post(StopService);
            return;
        }
        HookEndpoint(slot);
        try
        {
            switch (action)
            {
                case ActionShutdown:
                    await slot.Host.Engine.LogoutAsync().ConfigureAwait(false);
                    new Handler(Looper.MainLooper!).Post(() =>
                    {
                        StopService();
                        // The slot is not wanted any more: give its memory back now instead of leaving a cached process.
                        new Handler(Looper.MainLooper!).PostDelayed(() => Android.OS.Process.KillProcess(Android.OS.Process.MyPid()), 500);
                    });
                    break;
                case ActionResume:
                case null: // restarted by the system (START_STICKY)
                    if (await slot.Host.ResumeAsync().ConfigureAwait(false) is string message)
                        CrashCapture.Info(message);
                    break;
            }
        }
        catch (EngineException e)
        {
            CrashCapture.Info($"slot {SlotRuntime.Slot}: {e.Message}");
        }
    }

    private void HookEndpoint(SlotComposition slot)
    {
        lock (_clients)
        {
            if (_endpointHooked)
                return;
            _endpointHooked = true;
        }
        slot.Endpoint.EventReady += Broadcast;
    }

    private bool PromoteToForeground()
    {
        try
        {
            int type = OperatingSystem.IsAndroidVersionAtLeast(34) ? (int)ForegroundService.TypeSpecialUse : 0;
            ServiceCompat.StartForeground(this, EngineService.NotificationId, BuildNotification(), type);
            return true;
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal($"starting slot {SlotRuntime.Slot} in the foreground", e);
            return false;
        }
    }

    // Posted once when the slot goes to the foreground; the coordinator then keeps the shared notification's text.
    private Notification BuildNotification()
    {
        PendingIntent open = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        return new NotificationCompat.Builder(this, EngineService.ChannelId)
            .SetSmallIcon(Resource.Drawable.ic_stat_engine)!
            .SetContentTitle("VibeSkua army")!
            .SetContentText($"Slot {SlotRuntime.Slot} running")!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetShowWhen(false)!
            .SetCategory(NotificationCompat.CategoryService)!
            .SetVisibility(NotificationCompat.VisibilityPrivate)!
            .SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate)!
            .SetContentIntent(open)!
            .Build()!;
    }

    /// <summary>Engine snapshots of this process: the wake lock follows the script.</summary>
    internal static void OnSnapshot(Context context, EngineSnapshot snapshot)
    {
        SlotEngineService? service = s_current;
        service?.UpdateWakeLock(snapshot.Script.IsBusy);
    }

    private void UpdateWakeLock(bool busy)
    {
        lock (_clients)
        {
            if (busy && _wakeLock is null)
            {
                PowerManager power = (PowerManager)GetSystemService(PowerService)!;
                _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, $"VibeSkua:slot{SlotRuntime.Slot}")!;
                _wakeLock.SetReferenceCounted(false);
                _wakeLock.Acquire();
            }
            else if (!busy && _wakeLock is not null)
            {
                if (_wakeLock.IsHeld)
                    _wakeLock.Release();
                _wakeLock = null;
            }
        }
    }

    // On the IPC thread. The Message is recycled when this returns: copy what is needed first.
    private void OnMessage(Message msg)
    {
        if (msg.What != WhatRequest || msg.SendingUid != Android.OS.Process.MyUid())
            return; // only this app (same UID) may talk to a slot; never trust anything else
        byte[]? request = msg.Data?.GetByteArray(PayloadKey);
        Messenger? reply = msg.ReplyTo;
        if (request is null || reply is null)
            return;
        lock (_clients)
        {
            if (!_clients.Any(c => c.Equals(reply)))
                _clients.Add(reply);
        }
        _ = Task.Run(async () =>
        {
            if (!await SlotRuntime.StartInitialization().ConfigureAwait(false) || SlotRuntime.Value is not SlotComposition slot)
                return;
            HookEndpoint(slot);
            byte[]? answer = await slot.Endpoint.HandleAsync(request).ConfigureAwait(false);
            if (answer is not null)
                Send(reply, WhatRequest, answer);
        });
    }

    private void Broadcast(byte[] bytes)
    {
        Messenger[] clients;
        lock (_clients)
            clients = _clients.ToArray();
        foreach (Messenger client in clients)
            Send(client, WhatEvent, bytes);
    }

    private void Send(Messenger client, int what, byte[] bytes)
    {
        try
        {
            Message m = Message.Obtain(null, what)!;
            Bundle data = new();
            data.PutByteArray(PayloadKey, bytes);
            m.Data = data;
            client.Send(m);
        }
        catch (Exception e) when (e is RemoteException or DeadObjectException or Java.Lang.Exception)
        {
            lock (_clients)
                _clients.RemoveAll(c => c.Equals(client)); // that coordinator is gone; a new one registers with its next request
        }
    }

    private void StopService()
    {
        UpdateWakeLock(false);
        try
        {
            ServiceCompat.StopForeground(this, ServiceCompat.StopForegroundRemove);
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("stopForeground (slot)", e);
        }
        StopSelf();
    }

    public override void OnDestroy()
    {
        UpdateWakeLock(false);
        if (SlotRuntime.Value is SlotComposition slot && _endpointHooked)
            slot.Endpoint.EventReady -= Broadcast;
        _thread?.QuitSafely();
        s_current = null;
        CrashCapture.Lifecycle($"slot {SlotRuntime.Slot} service destroyed");
        base.OnDestroy();
    }

    /// <summary>Static handler class (no implicit reference to the service from the looper).</summary>
    private sealed class IncomingHandler : Handler
    {
        private readonly Action<Message> _on;

        public IncomingHandler(Looper looper, Action<Message> on)
            : base(looper)
        {
            _on = on;
        }

        public override void HandleMessage(Message msg)
        {
            try
            {
                _on(msg);
            }
            catch (Exception e)
            {
                CrashCapture.NonFatal("slot IPC message", e);
            }
        }
    }

    /// <summary>Wire format of the coordinator side (<see cref="SlotClient"/>).</summary>
    internal static class Wire
    {
        public const string Key = PayloadKey;
        public const int Request = WhatRequest;
        public const int Event = WhatEvent;
    }
}

// One class per slot process: a component runs in the one process the manifest names for it.
[Register("com.vibeskua.droid.SlotService2")]
public sealed class SlotService2 : SlotEngineService
{
    public SlotService2() { }

    public SlotService2(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }
}

[Register("com.vibeskua.droid.SlotService3")]
public sealed class SlotService3 : SlotEngineService
{
    public SlotService3() { }

    public SlotService3(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }
}

[Register("com.vibeskua.droid.SlotService4")]
public sealed class SlotService4 : SlotEngineService
{
    public SlotService4() { }

    public SlotService4(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }
}

[Register("com.vibeskua.droid.SlotService5")]
public sealed class SlotService5 : SlotEngineService
{
    public SlotService5() { }

    public SlotService5(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }
}

[Register("com.vibeskua.droid.SlotService6")]
public sealed class SlotService6 : SlotEngineService
{
    public SlotService6() { }

    public SlotService6(nint handle, JniHandleOwnership transfer) : base(handle, transfer) { }
}
