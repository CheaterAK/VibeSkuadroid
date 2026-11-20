using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Diagnostics;

namespace Skua.App.Droid;

/// <summary>
/// Watched-drop notifications (<see cref="IDropAlertSink"/>): their own channel "drops" (default importance), separate
/// from <see cref="EngineService"/>'s status and notice channels, whose code this does not touch.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>One notification per item (tag "drop", id = item id): a later alert for the same item replaces it, alerting
/// only once while it is shown, so an item dropping every kill does not ring every kill.</item>
/// <item>Tapping opens the app (immutable PendingIntent to <see cref="MainActivity"/>).</item>
/// <item>Lock screen: private visibility with a public version that names neither the item nor the character, unless
/// the user allowed item names there (then public). The channel itself is created private.</item>
/// <item>Posting never throws: a missing permission or a refused post is recorded as a diagnostics line (type only).</item>
/// </list>
/// </remarks>
internal sealed class DropNotifications : IDropAlertSink
{
    public const string ChannelId = "drops";
    private const string Tag = "drop";

    private readonly Context _context;
    private int _channelCreated;

    public DropNotifications(Context context)
    {
        _context = context.ApplicationContext ?? context;
    }

    public bool Enabled
    {
        get
        {
            try
            {
                NotificationManagerCompat manager = NotificationManagerCompat.From(_context)!;
                if (!manager.AreNotificationsEnabled())
                    return false;
                if (!OperatingSystem.IsAndroidVersionAtLeast(26))
                    return true;
                NotificationChannel? channel = ((NotificationManager)_context.GetSystemService(Context.NotificationService)!).GetNotificationChannel(ChannelId);
                return channel is null || channel.Importance != NotificationImportance.None;
            }
            catch (Java.Lang.Exception)
            {
                return true;
            }
        }
    }

    public void Post(DropAlert alert)
    {
        try
        {
            EnsureChannel();
            NotificationManagerCompat manager = NotificationManagerCompat.From(_context)!;
            if (!manager.AreNotificationsEnabled())
                return;

            PendingIntent open = PendingIntent.GetActivity(_context, 3,
                new Intent(_context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

            // What a secure lock screen shows instead: no item name, no character name.
            Notification publicVersion = new NotificationCompat.Builder(_context, ChannelId)
                .SetSmallIcon(Resource.Drawable.ic_stat_engine)!
                .SetContentTitle("VibeSkua")!
                .SetContentText(alert.PublicText)!
                .Build()!;

            Notification notification = new NotificationCompat.Builder(_context, ChannelId)
                .SetSmallIcon(Resource.Drawable.ic_stat_engine)!
                .SetContentTitle(alert.Title)!
                .SetContentText(alert.Text)!
                .SetAutoCancel(true)!
                .SetOnlyAlertOnce(true)!
                .SetCategory(NotificationCompat.CategoryEvent)!
                .SetPriority(NotificationCompat.PriorityDefault)!
                .SetVisibility(alert.ShowNameOnLockScreen ? NotificationCompat.VisibilityPublic : NotificationCompat.VisibilityPrivate)!
                .SetPublicVersion(publicVersion)!
                .SetContentIntent(open)!
                .Build()!;
            manager.Notify(Tag, alert.ItemId, notification);
        }
        catch (Exception e) when (e is Java.Lang.Exception or InvalidOperationException)
        {
            // SecurityException without POST_NOTIFICATIONS, or the system refused: the Notification Center still has it.
            CrashCapture.Info($"Drop notification not posted ({e.GetType().Name}).");
        }
    }

    private void EnsureChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || Interlocked.Exchange(ref _channelCreated, 1) != 0)
            return;
        NotificationManager manager = (NotificationManager)_context.GetSystemService(Context.NotificationService)!;
        NotificationChannel channel = new(ChannelId, "Watched drops", NotificationImportance.Default)
        {
            Description = "An item on the drop watch list dropped or was obtained.",
            LockscreenVisibility = NotificationVisibility.Private,
        };
        manager.CreateNotificationChannel(channel);
    }
}
