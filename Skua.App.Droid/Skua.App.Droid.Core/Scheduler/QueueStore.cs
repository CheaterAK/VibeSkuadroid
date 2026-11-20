using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Skua.App.Droid.Core.Scheduler;

/// <summary>The queue as persisted between processes (<c>droid/queue.json</c>). No secrets.</summary>
public sealed record QueueState
{
    public string? PlaylistName { get; init; }

    public IReadOnlyList<QueueEntry> Entries { get; init; } = [];

    /// <summary>Start again at the first entry after the last one.</summary>
    public bool Loop { get; init; }

    /// <summary>The queue was running when this was saved.</summary>
    public bool Running { get; init; }

    /// <summary>A planned engine restart (memory) interrupted the queue: resume after the restart's login.</summary>
    public bool ResumeAfterRestart { get; init; }

    /// <summary>The entry to run (or running).</summary>
    public int Index { get; init; }

    /// <summary>Completed runs of the entry at <see cref="Index"/>.</summary>
    public int RunsDone { get; init; }

    /// <summary>Failed attempts of the current run (Retry).</summary>
    public int Attempts { get; init; }

    /// <summary>Time spent on the entry at <see cref="Index"/> (paused time excluded): the entry's stop-after clock.</summary>
    public TimeSpan EntryElapsed { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Per entry (by id): its state in the current or last run.</summary>
    public IReadOnlyDictionary<Guid, EntryResult> Results { get; init; } = new Dictionary<Guid, EntryResult>();

    public DateTimeOffset SavedAt { get; init; }
}

/// <summary>
/// App-private storage of the queue: its live state (<c>droid/queue.json</c>, rewritten at every step so a restart
/// can resume it) and named playlists (<c>droid/playlists/&lt;name&gt;.json</c>). Writes are atomic (temp file + move).
/// </summary>
public sealed partial class QueueStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();

    public QueueStore(string stateFile, string playlistsDir)
    {
        StateFile = stateFile;
        PlaylistsDir = playlistsDir;
    }

    public string StateFile { get; }

    public string PlaylistsDir { get; }

    [GeneratedRegex(@"^[A-Za-z0-9 _()\-][A-Za-z0-9 _().\-]{0,59}\z")]
    private static partial Regex NamePattern();

    /// <summary>A playlist name that is also a safe file name: letters, digits, spaces, _ - ( ) and dots (not first), at most 60.</summary>
    public static bool IsValidName(string? name) => name is not null && name.Trim().Length > 0 && NamePattern().IsMatch(name.Trim());

    public QueueState? LoadState()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(StateFile))
                    return null;
                QueueState? state = JsonSerializer.Deserialize<QueueState>(File.ReadAllText(StateFile), Json);
                return state is null ? null : Sanitize(state);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }
    }

    public void SaveState(QueueState state)
    {
        lock (_lock)
            WriteAtomic(StateFile, JsonSerializer.Serialize(state, Json));
    }

    public IReadOnlyList<string> ListPlaylists()
    {
        lock (_lock)
        {
            if (!Directory.Exists(PlaylistsDir))
                return [];
            return Directory.EnumerateFiles(PlaylistsDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(IsValidName)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
    }

    /// <exception cref="ArgumentException">The name is not valid.</exception>
    public void SavePlaylist(Playlist playlist)
    {
        string file = PlaylistFile(playlist.Name);
        lock (_lock)
            WriteAtomic(file, JsonSerializer.Serialize(playlist with { Name = playlist.Name.Trim() }, Json));
    }

    /// <summary>The playlist, or null when it does not exist or cannot be read.</summary>
    public Playlist? LoadPlaylist(string name)
    {
        string file = PlaylistFile(name);
        lock (_lock)
        {
            try
            {
                if (!File.Exists(file))
                    return null;
                Playlist? p = JsonSerializer.Deserialize<Playlist>(File.ReadAllText(file), Json);
                return p is null ? null : p with { Name = name.Trim(), Entries = p.Entries?.Where(e => e is not null).Select(e => e.Normalized()).ToList() ?? [] };
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }
    }

    public bool DeletePlaylist(string name)
    {
        string file = PlaylistFile(name);
        lock (_lock)
        {
            if (!File.Exists(file))
                return false;
            File.Delete(file);
            return true;
        }
    }

    private string PlaylistFile(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException("Use letters, digits, spaces, _ - ( ) and dots (at most 60).", nameof(name));
        string dir = Path.GetFullPath(PlaylistsDir);
        string file = Path.GetFullPath(Path.Combine(dir, name.Trim() + ".json"));
        if (!string.Equals(Path.GetDirectoryName(file), Path.TrimEndingDirectorySeparator(dir), StringComparison.Ordinal))
            throw new ArgumentException("Not a valid playlist name.", nameof(name));
        return file;
    }

    private static QueueState Sanitize(QueueState s)
    {
        List<QueueEntry> entries = s.Entries?.Where(e => e is not null).Select(e => e.Normalized()).ToList() ?? [];
        return s with
        {
            Entries = entries,
            Index = Math.Clamp(s.Index, 0, entries.Count),
            RunsDone = Math.Max(0, s.RunsDone),
            Attempts = Math.Max(0, s.Attempts),
            EntryElapsed = s.EntryElapsed < TimeSpan.Zero ? TimeSpan.Zero : s.EntryElapsed,
            Results = s.Results ?? new Dictionary<Guid, EntryResult>(),
        };
    }

    private static void WriteAtomic(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, file, overwrite: true);
    }
}
