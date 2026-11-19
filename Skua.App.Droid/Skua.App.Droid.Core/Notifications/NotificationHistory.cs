using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.App.Droid.Core.Notifications;

/// <summary>The desktop Notification Center's kinds (and filter chips).</summary>
public enum NotificationKind
{
    Activity,
    Item,
    System,
    Warning,
    Error,
}

/// <summary>One Notification Center entry (the desktop's NotificationEntry, plus whether a watched item caused it).</summary>
public sealed record NotificationEntry(DateTimeOffset Timestamp, NotificationKind Kind, string Message, bool Watched = false)
{
    [JsonIgnore]
    public string TimeDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    [JsonIgnore]
    public string DateDisplay => Timestamp.ToLocalTime().ToString("MMM d");

    /// <summary>How the entry reads when copied or shared: time, kind, message (the desktop's format).</summary>
    [JsonIgnore]
    public string Line => $"{TimeDisplay} [{KindName}] {Message}";

    [JsonIgnore]
    public string KindName => Kind switch
    {
        NotificationKind.Item => "Item",
        NotificationKind.Warning => "Warning",
        NotificationKind.Error => "Error",
        NotificationKind.System => "Map",
        _ => "Activity",
    };

    [JsonIgnore]
    public bool IsItem => Kind == NotificationKind.Item;

    [JsonIgnore]
    public bool IsWarning => Kind == NotificationKind.Warning;

    [JsonIgnore]
    public bool IsError => Kind == NotificationKind.Error;

    [JsonIgnore]
    public bool IsSystem => Kind == NotificationKind.System;

    /// <summary>The desktop's text for an event (its NotificationCenterViewModel handlers), or null for events it does not list.</summary>
    public static NotificationEntry? From(GameEvent e) => e.Kind switch
    {
        GameEventKind.ItemDropped => new(e.At, NotificationKind.Item, $"{e.Name}{Quantity(e)} (dropped)"),
        GameEventKind.ItemObtained => new(e.At, NotificationKind.Item, $"{e.Name}{Quantity(e)} (picked up)"),
        GameEventKind.DropRejected => new(e.At, NotificationKind.Item, $"{e.Name} (rejected: junk)"),
        GameEventKind.ItemSold => new(e.At, NotificationKind.Item, $"Sold {e.Quantity:N0} item(s) for {e.Amount:N0} {(e.Flag ? "AC" : "gold")}"),
        GameEventKind.PlayerDied => new(e.At, NotificationKind.Warning, "Your character died."),
        GameEventKind.Relogin => new(e.At, NotificationKind.Warning, e.Flag ? "Connection interrupted; reconnecting." : "Reconnecting to the game."),
        GameEventKind.MapChanged => new(e.At, NotificationKind.System, $"Entered {e.Name}"),
        GameEventKind.QuestAccepted => new(e.At, NotificationKind.Activity, $"Accepted quest #{e.Id}"),
        GameEventKind.QuestCompleted => new(e.At, NotificationKind.Activity, $"Completed quest #{e.Id}"),
        GameEventKind.ScriptStarted => new(e.At, NotificationKind.Activity, e.Name is null ? "Script started" : $"Script started: {e.Name}"),
        GameEventKind.ScriptStopped => new(e.At, NotificationKind.Activity, e.Name is null ? "Script stopped" : $"Script stopped: {e.Name}"),
        GameEventKind.ScriptError => new(e.At, NotificationKind.Error, e.Name is null ? $"Script error: {e.Detail}" : $"Script error ({e.Name}): {e.Detail}"),
        _ => null,
    };

    private static string Quantity(GameEvent e) => e.Quantity > 1 ? $" x{e.Quantity}" : string.Empty;
}

/// <summary>
/// The Notification Center's history: newest first, at most <see cref="Capacity"/> entries (the desktop keeps 500),
/// kept across restarts in <c>files/droid/notification-history.json</c> (app-private, excluded from backup like
/// everything of the app; item, map, quest and script names only, messages scrubbed of registered secrets).
/// </summary>
/// <remarks>
/// Thread safe. <see cref="Add"/> only touches memory; the file is written at most every <see cref="SaveDelay"/> on a
/// timer thread (and by <see cref="Flush"/>), so a burst of events never waits for storage.
/// </remarks>
public sealed class NotificationHistory : IDisposable
{
    public const int Capacity = 500;

    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string? _path;
    private readonly Func<string, string> _scrub;
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly LinkedList<NotificationEntry> _entries = new();
    private readonly Timer? _timer;
    private bool _dirty;
    private bool _disposed;

    /// <param name="path">The file; null keeps the history in memory only.</param>
    /// <param name="scrub">Applied to every message before it is kept (removes registered secrets).</param>
    /// <param name="saveDelay">How long after a change the file is written. Default 2 s.</param>
    public NotificationHistory(string? path, Func<string, string>? scrub = null, TimeSpan? saveDelay = null)
    {
        _path = path;
        _scrub = scrub ?? (s => s);
        SaveDelay = saveDelay ?? TimeSpan.FromSeconds(2);
        if (path is not null)
        {
            foreach (NotificationEntry entry in Load(path).Take(Capacity))
                _entries.AddLast(entry);
            _timer = new Timer(_ => Flush());
        }
    }

    public TimeSpan SaveDelay { get; }

    /// <summary>A new entry (any thread).</summary>
    public event EventHandler<NotificationEntry>? Added;

    /// <summary>The history was cleared.</summary>
    public event EventHandler? Cleared;

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>The entries, newest first.</summary>
    public IReadOnlyList<NotificationEntry> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToList();
        }
    }

    public NotificationEntry Add(NotificationEntry entry)
    {
        entry = entry with { Message = _scrub(entry.Message) };
        lock (_gate)
        {
            if (_disposed)
                return entry;
            _entries.AddFirst(entry);
            while (_entries.Count > Capacity)
                _entries.RemoveLast();
            ScheduleSave();
        }
        Added?.Invoke(this, entry);
        return entry;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            ScheduleSave();
        }
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes pending changes now (atomic: temp file, then rename). Never throws.</summary>
    public void Flush()
    {
        if (_path is null)
            return;
        lock (_saveGate)
        {
            List<NotificationEntry> snapshot;
            lock (_gate)
            {
                if (!_dirty)
                    return;
                _dirty = false;
                snapshot = _entries.ToList();
            }
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, s_json));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The history is a convenience: losing a write must not disturb the engine.
            }
        }
    }

    // Under _gate.
    private void ScheduleSave()
    {
        if (_timer is null)
            return;
        if (!_dirty)
        {
            _dirty = true;
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private static List<NotificationEntry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<List<NotificationEntry>>(File.ReadAllText(path), s_json) ?? new())
                    .Where(e => !string.IsNullOrEmpty(e.Message))
                    .ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged file starts an empty history.
        }
        return new();
    }

    public void Dispose()
    {
        Flush();
        lock (_gate)
            _disposed = true;
        _timer?.Dispose();
    }
}
