namespace Skua.App.Droid.Core.Notifications;

/// <summary>A watched item dropped or was obtained.</summary>
/// <param name="ItemId">The item's id (one notification per item: a later alert for it replaces the earlier one).</param>
/// <param name="ItemName">The item.</param>
/// <param name="Quantity">How many.</param>
/// <param name="Obtained">Obtained (picked up or given) rather than offered as a drop.</param>
/// <param name="ShowNameOnLockScreen">The user allowed the item name on a secure lock screen.</param>
public sealed record DropAlert(int ItemId, string ItemName, int Quantity, bool Obtained, bool ShowNameOnLockScreen)
{
    /// <summary>The notification's title.</summary>
    public string Title => Obtained ? "Watched item obtained" : "Watched item dropped";

    /// <summary>The notification's text (contains the item name).</summary>
    public string Text => Quantity > 1 ? $"{ItemName} x{Quantity}" : ItemName;

    /// <summary>What a secure lock screen shows unless names are allowed there: no item or character name.</summary>
    public string PublicText => Obtained ? "A watched item was obtained" : "A watched item dropped";
}

/// <summary>Posts drop alerts as system notifications. Android: its own channel, separate from the engine's status notification.</summary>
public interface IDropAlertSink
{
    /// <summary>Whether the system currently lets the app post notifications.</summary>
    bool Enabled { get; }

    /// <summary>Posts (or replaces) the notification for the alert's item. Must not block and must not throw.</summary>
    void Post(DropAlert alert);
}

/// <summary>No system notifications (desktop preview, tests): remembers the alerts.</summary>
public sealed class RecordingDropAlertSink : IDropAlertSink
{
    private readonly List<DropAlert> _posted = new();

    public bool Enabled { get; set; } = true;

    public IReadOnlyList<DropAlert> Posted
    {
        get
        {
            lock (_posted)
                return _posted.ToList();
        }
    }

    public event EventHandler<DropAlert>? AlertPosted;

    public void Post(DropAlert alert)
    {
        lock (_posted)
            _posted.Add(alert);
        AlertPosted?.Invoke(this, alert);
    }
}
