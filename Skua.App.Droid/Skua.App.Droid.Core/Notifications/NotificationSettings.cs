using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.App.Droid.Core.Notifications;

/// <summary>
/// Drop notifications, junk and Discord webhook options. Stored in <c>files/droid/notify.json</c> (app-private, no
/// secrets: the webhook URL is a credential and lives encrypted in the secret store, see <see cref="WebhookUrlVault"/>).
/// </summary>
public sealed record NotificationSettings
{
    /// <summary>The drop watch list (item names).</summary>
    public IReadOnlyList<string> WatchedItems { get; init; } = [];

    /// <summary>Post an Android notification when a watched item drops.</summary>
    public bool NotifyWatchedDrops { get; init; } = true;

    /// <summary>Post an Android notification when a watched item is obtained (picked up or given).</summary>
    public bool NotifyWatchedObtained { get; init; } = true;

    /// <summary>Show the item name on a secure lock screen (off: the lock screen only says that a watched item dropped).</summary>
    public bool ShowNamesOnLockScreen { get; init; }

    /// <summary>Reject junk drops as soon as they are offered.</summary>
    public bool AutoRejectJunk { get; init; }

    /// <summary>Send events to the Discord webhook (needs a stored webhook URL).</summary>
    public bool WebhookEnabled { get; init; }

    /// <summary>"Script Started" cards.</summary>
    public bool WebhookNotifyStarted { get; init; } = true;

    /// <summary>"Farming Session Concluded" cards.</summary>
    public bool WebhookNotifyStopped { get; init; } = true;

    /// <summary>"Script Error" cards (the script name only, never the exception text).</summary>
    public bool WebhookNotifyCrashed { get; init; } = true;

    /// <summary>"Kicked / Disconnected &amp; Relogging" cards.</summary>
    public bool WebhookNotifyRelogged { get; init; }

    /// <summary>"Scheduler Paused" cards (sent by the script scheduler).</summary>
    public bool WebhookNotifySchedulerPaused { get; init; } = true;

    /// <summary>Drop alerts for watched items.</summary>
    public bool WebhookNotifyItemDrops { get; init; } = true;

    /// <summary>Put the character name in the cards (off: "VibeSkua Bot").</summary>
    public bool WebhookIncludeCharacterName { get; init; }

    /// <summary>Put the map name in drop alerts and session summaries.</summary>
    public bool WebhookIncludeMap { get; init; } = true;

    /// <summary>The settings with the watch list cleaned up.</summary>
    public NotificationSettings Normalized() => this with { WatchedItems = DropWatchList.Add([], string.Join(DropWatchList.Separator, WatchedItems ?? [])) };

    public bool Equals(NotificationSettings? other) =>
        other is not null
        && WatchedItems.SequenceEqual(other.WatchedItems, StringComparer.Ordinal)
        && NotifyWatchedDrops == other.NotifyWatchedDrops
        && NotifyWatchedObtained == other.NotifyWatchedObtained
        && ShowNamesOnLockScreen == other.ShowNamesOnLockScreen
        && AutoRejectJunk == other.AutoRejectJunk
        && WebhookEnabled == other.WebhookEnabled
        && WebhookNotifyStarted == other.WebhookNotifyStarted
        && WebhookNotifyStopped == other.WebhookNotifyStopped
        && WebhookNotifyCrashed == other.WebhookNotifyCrashed
        && WebhookNotifyRelogged == other.WebhookNotifyRelogged
        && WebhookNotifySchedulerPaused == other.WebhookNotifySchedulerPaused
        && WebhookNotifyItemDrops == other.WebhookNotifyItemDrops
        && WebhookIncludeCharacterName == other.WebhookIncludeCharacterName
        && WebhookIncludeMap == other.WebhookIncludeMap;

    public override int GetHashCode() => HashCode.Combine(WatchedItems.Count, NotifyWatchedDrops, AutoRejectJunk, WebhookEnabled);
}

/// <summary>Loads and saves <see cref="NotificationSettings"/> atomically; a damaged file starts from defaults.</summary>
public sealed class NotificationSettingsStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private NotificationSettings _current;

    public NotificationSettingsStore(string path)
    {
        _path = path;
        _current = Load();
    }

    public string FilePath => _path;

    /// <summary>Raised after a change was saved (on the caller's thread).</summary>
    public event EventHandler<NotificationSettings>? Changed;

    public NotificationSettings Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    /// <summary>Applies <paramref name="change"/> and saves the result.</summary>
    public NotificationSettings Update(Func<NotificationSettings, NotificationSettings> change)
    {
        NotificationSettings updated;
        lock (_gate)
        {
            updated = change(_current).Normalized();
            if (updated.Equals(_current))
                return updated;
            Save(updated);
            _current = updated;
        }
        Changed?.Invoke(this, updated);
        return updated;
    }

    private NotificationSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return (JsonSerializer.Deserialize<NotificationSettings>(File.ReadAllText(_path), s_json) ?? new NotificationSettings()).Normalized();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged file must not stop the app.
        }
        return new NotificationSettings();
    }

    private void Save(NotificationSettings settings)
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, s_json));
        File.Move(temp, _path, overwrite: true);
    }
}
