using Skua.App.Droid.Core.Engine;
using System.Text.Json.Serialization;

namespace Skua.App.Droid.Core.Scheduler;

/// <summary>What the queue does when an entry fails to start or its script ends with an error.</summary>
public enum QueueErrorAction
{
    /// <summary>Mark the entry failed and go on with the next one (the desktop scheduler's only behaviour).</summary>
    Skip,

    /// <summary>Stop the queue.</summary>
    Stop,

    /// <summary>Run the entry again, up to <see cref="QueueEntry.MaxRetries"/> times, then skip it.</summary>
    Retry,
}

/// <summary>One saved CoreBots setting of an entry's CoreBots profile (a line of the CBO file).</summary>
public sealed record SavedOption(string Name, ScriptOptionKind Kind, string Value);

/// <summary>
/// One script in the queue: the desktop scheduler's ScriptItemViewModel (path, display name, its own option storage)
/// plus the per-entry run settings of this app.
/// </summary>
public sealed record QueueEntry
{
    /// <summary>Identity; also part of the option storage name, so a copy of the same script gets its own options.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The script, relative to the script library root ("Farm/Gold.cs").</summary>
    public string Script { get; init; } = string.Empty;

    /// <summary>The name shown in the queue (desktop default: the file name, " (2)" for a second copy).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>How many times the script runs (each run starts it again after it ends); 0 repeats until stopped or <see cref="StopAfter"/>.</summary>
    public int Runs { get; init; } = 1;

    /// <summary>Stops the entry once its runs took this long in all (paused time excluded); null for no limit.</summary>
    public TimeSpan? StopAfter { get; init; }

    /// <summary>Wait after a run before the next run or entry (the desktop waits about 2 s).</summary>
    public TimeSpan DelayAfter { get; init; } = TimeSpan.FromSeconds(3);

    public QueueErrorAction OnError { get; init; } = QueueErrorAction.Skip;

    /// <summary>Attempts after a failure when <see cref="OnError"/> is <see cref="QueueErrorAction.Retry"/>.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// CoreBots settings written to the account's CBO file right before each run of this entry; null leaves the file
    /// as it is (CoreBots then reads whatever was saved last).
    /// </summary>
    public IReadOnlyList<SavedOption>? CoreBots { get; init; }

    /// <summary>
    /// Skua's option storage of this entry (its script options profile): the desktop scheduler's
    /// <c>Queue_&lt;script&gt;_&lt;id&gt;</c>, saved by Skua as <c>options/&lt;storage&gt;.cfg</c>.
    /// </summary>
    [JsonIgnore]
    public string OptionStorage => $"Queue_{Path.GetFileNameWithoutExtension(Script)}_{Id}";

    /// <summary>"1 run", "3 runs", "repeat", plus the limit, the delay and the error rule.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            List<string> parts = new() { Runs == 0 ? "repeat" : Runs == 1 ? "1 run" : $"{Runs} runs" };
            if (StopAfter is TimeSpan limit)
                parts.Add($"stop after {QueueText.Duration(limit)}");
            parts.Add($"wait {QueueText.Duration(DelayAfter)}");
            parts.Add(OnError switch
            {
                QueueErrorAction.Stop => "on error stop",
                QueueErrorAction.Retry => $"on error retry {MaxRetries}x",
                _ => "on error skip",
            });
            if (CoreBots is not null)
                parts.Add("CoreBots profile");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Clamps values a hand-edited file could carry.</summary>
    public QueueEntry Normalized() => this with
    {
        Runs = Math.Clamp(Runs, 0, 10000),
        MaxRetries = Math.Clamp(MaxRetries, 0, 100),
        DelayAfter = DelayAfter < TimeSpan.Zero ? TimeSpan.Zero : DelayAfter > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : DelayAfter,
        StopAfter = StopAfter is TimeSpan t && t > TimeSpan.Zero ? t : null,
        Name = string.IsNullOrWhiteSpace(Name) ? Path.GetFileName(Script) : Name.Trim(),
    };
}

/// <summary>A saved queue (the desktop's playlist file, plus the per-entry settings).</summary>
public sealed record Playlist(string Name, IReadOnlyList<QueueEntry> Entries, bool Loop = false);

/// <summary>The state of one entry in the current or last queue run.</summary>
/// <param name="Status">"Queued", "Running", "Paused", "Completed", "Failed", "Crashed", "File Not Found", "Disconnected", "Retrying", "Stopped", "Skipped", "Interrupted".</param>
/// <param name="RunsDone">Runs completed.</param>
/// <param name="Elapsed">Time spent running this entry (paused time excluded).</param>
/// <param name="Error">Plain-English reason of the last failure.</param>
public sealed record EntryResult(string Status, int RunsDone, TimeSpan Elapsed, string? Error)
{
    public static EntryResult Queued { get; } = new("Queued", 0, TimeSpan.Zero, null);
}

/// <summary>What the queue is doing.</summary>
public enum QueuePhase
{
    /// <summary>Not running.</summary>
    Idle,

    /// <summary>A script of the queue runs (or is paused).</summary>
    Running,

    /// <summary>Waiting between runs.</summary>
    Waiting,

    /// <summary>The connection is down: waiting for Skua's relogin (or the user) before going on.</summary>
    WaitingForConnection,

    /// <summary>The engine restarts to give back memory; the queue resumes at the next run afterwards.</summary>
    Restarting,

    /// <summary>Stopping the queue's script.</summary>
    Stopping,
}

/// <summary>An entry with its state, for the queue list.</summary>
public sealed record QueueEntryView(int Index, QueueEntry Entry, EntryResult Result, bool IsCurrent);

/// <summary>An immutable picture of the queue for the UI and the notification.</summary>
public sealed record QueueStatus(
    QueuePhase Phase,
    string Text,
    int Index,
    int Count,
    string? CurrentName,
    string? NextName,
    int Run,
    int Runs,
    DateTimeOffset? StartedAt,
    TimeSpan EntryElapsed,
    bool Loop,
    string? PlaylistName,
    IReadOnlyList<QueueEntryView> Entries)
{
    public static QueueStatus Empty { get; } = new(QueuePhase.Idle, "The queue is empty.", 0, 0, null, null, 0, 0, null, TimeSpan.Zero, false, null, []);

    public bool IsRunning => Phase != QueuePhase.Idle;

    /// <summary>"Queue 2/5: Gold Farm (run 1/3)", for the notification.</summary>
    public string? NotificationLine => IsRunning && CurrentName is not null
        ? $"Queue {Math.Min(Index + 1, Count)}/{Count}: {CurrentName}{(Runs == 1 ? string.Empty : Runs == 0 ? $" (run {Run})" : $" (run {Run}/{Runs})")}"
        : null;
}

internal static class QueueText
{
    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min{(t.Seconds > 0 ? $" {t.Seconds} s" : string.Empty)}"
        : $"{(int)t.TotalSeconds} s";

    public static string Clock(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
}
