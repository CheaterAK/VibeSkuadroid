using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Skua.App.Droid.Core.Army;
using Skua.App.Droid.Diagnostics;

namespace Skua.App.Droid.Army;

/// <summary>
/// <see cref="IArmyPlatform"/> on Android, in the main process (the coordinator): starts and binds the slot processes'
/// services, reads the device's memory, keeps the main foreground service up while light sessions are online and
/// writes the army line into the app's one persistent notification.
/// </summary>
internal sealed class AndroidArmyPlatform : IArmyPlatform
{
    private readonly Context _context;
    private readonly Dictionary<int, SlotClient> _clients = new();
    private readonly object _gate = new();

    public AndroidArmyPlatform(Context context)
    {
        _context = context.ApplicationContext ?? context;
    }

    public ISlotChannel? StartSlot(int slot)
    {
        lock (_gate)
        {
            if (!_clients.TryGetValue(slot, out SlotClient? client))
            {
                client = new SlotClient(_context, slot);
                _clients[slot] = client;
            }
            if (!client.Start())
            {
                _clients.Remove(slot);
                _ = client.DisposeAsync();
                return null;
            }
            return client;
        }
    }

    public ISlotChannel? AttachSlot(int slot)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(slot, out SlotClient? existing))
                return existing;
            SlotClient client = new(_context, slot);
            if (!client.Attach())
            {
                _ = client.DisposeAsync();
                return null;
            }
            _clients[slot] = client;
            return client;
        }
    }

    public void StopSlot(int slot)
    {
        SlotClient? client;
        lock (_gate)
            _clients.Remove(slot, out client);
        if (client is null)
            return;
        client.Shutdown();
        _ = client.DisposeAsync();
    }

    public DeviceMemory ReadDeviceMemory() => Read(_context);

    public static DeviceMemory Read(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.ActivityService) is not ActivityManager am)
                return DeviceMemory.Unknown;
            ActivityManager.MemoryInfo info = new();
            am.GetMemoryInfo(info);
            return new DeviceMemory(info.TotalMem, info.AvailMem, info.Threshold);
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("reading the device memory", e);
            return DeviceMemory.Unknown;
        }
    }

    public void KeepAlive(bool active)
    {
        EngineService.ArmyActive = active;
        if (active && !EngineService.IsRunning)
            EngineService.Start(_context, EngineService.ActionStart);
    }

    private string? _lastSummary;
    private long _lastPost;

    public void PublishSummary(string? summary)
    {
        // Android throttles notification updates: only a changed line, at most once a second.
        long now = System.Environment.TickCount64;
        if (summary == _lastSummary || (summary is not null && _lastSummary is not null && now - _lastPost < 1000))
            return;
        _lastSummary = summary;
        _lastPost = now;
        EngineService.ArmySummary = summary;
        if (EngineService.RefreshArmyLine() || summary is null)
            return;
        // The main service is not running (only slot processes are): the coordinator updates the shared notification
        // they hold, so it still says what the army does.
        bool slotsRunning;
        lock (_gate)
            slotsRunning = _clients.Count > 0;
        if (!slotsRunning)
            return;
        try
        {
            PendingIntent open = PendingIntent.GetActivity(_context, 0,
                new Intent(_context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
            Notification n = new NotificationCompat.Builder(_context, EngineService.ChannelId)
                .SetSmallIcon(Resource.Drawable.ic_stat_engine)!
                .SetContentTitle("VibeSkua army")!
                .SetContentText(summary)!
                .SetOngoing(true)!
                .SetOnlyAlertOnce(true)!
                .SetShowWhen(false)!
                .SetVisibility(NotificationCompat.VisibilityPrivate)!
                .SetContentIntent(open)!
                .Build()!;
            NotificationManagerCompat.From(_context)!.Notify(EngineService.NotificationId, n);
        }
        catch (Java.Lang.SecurityException)
        {
        }
    }
}
