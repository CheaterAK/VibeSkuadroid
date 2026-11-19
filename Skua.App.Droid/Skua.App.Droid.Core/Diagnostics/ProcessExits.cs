using System.Globalization;
using System.Text;

namespace Skua.App.Droid.Core.Diagnostics;

/// <summary>Android's ApplicationExitInfo.REASON_* values (API 30+), by number so this compiles without Android.</summary>
public enum ExitReason
{
    Unknown = 0,
    ExitSelf = 1,
    Signaled = 2,
    LowMemory = 3,
    Crash = 4,
    CrashNative = 5,
    Anr = 6,
    InitializationFailure = 7,
    PermissionChange = 8,
    ExcessiveResourceUsage = 9,
    UserRequested = 10,
    UserStopped = 11,
    DependencyDied = 12,
    Other = 13,
    Freezer = 14,
    PackageStateChange = 15,
    PackageUpdated = 16,
}

/// <summary>Whether the UI was showing.</summary>
public enum UiState
{
    /// <summary>No activity was ever started in this process (service or receiver only).</summary>
    None = 0,
    Foreground = 1,
    Background = 2,
}

/// <summary>
/// What a process was doing at its last heartbeat or lifecycle transition. Written to the startup marker and (API 30+)
/// to Android's process state summary, which the system hands back with the exit record of that very process.
/// </summary>
/// <param name="Ui">Activity visible or not.</param>
/// <param name="Script">The script state ("Idle", "Running", "Compiling"...), or null when the engine is not composed.</param>
/// <param name="Connection">The connection phase, or null.</param>
/// <param name="Event">The last lifecycle event ("onStop", "surfaceDestroyed", "heartbeat"...).</param>
/// <param name="At">When this was recorded.</param>
/// <param name="Planned">Set just before the app ends its own process on purpose (restart), else null.</param>
/// <param name="RssMb">Resident memory at that moment, or -1.</param>
public sealed record ProcessActivity(UiState Ui, string? Script, string? Connection, string? Event, DateTimeOffset At, string? Planned = null, int RssMb = -1)
{
    /// <summary>Android rejects summaries of 128 bytes or more.</summary>
    public const int MaxSummaryBytes = 127;

    public static ProcessActivity Initial(DateTimeOffset at) => new(UiState.None, null, null, "start", at);

    /// <summary>A script was compiling, running or stopping.</summary>
    public bool ScriptBusy => Script is "Running" or "Compiling" or "Stopping";

    public string UiText => Ui switch
    {
        UiState.Foreground => "foreground",
        UiState.Background => "background",
        _ => "no UI",
    };

    /// <summary>One line for logs and reports.</summary>
    public string Describe() => FormattableString.Invariant(
        $"UI {UiText}, script {Script ?? "n/a"}, connection {Connection ?? "n/a"}, last event {Event ?? "n/a"} at {At:u}{(RssMb >= 0 ? $", RSS {RssMb} MB" : string.Empty)}{(Planned is null ? string.Empty : $", planned exit ({Planned})")}");

    /// <summary>Compact ASCII form for ActivityManager.setProcessStateSummary (at most <see cref="MaxSummaryBytes"/> bytes).</summary>
    public byte[] ToSummary()
    {
        string text = string.Create(CultureInfo.InvariantCulture,
            $"v1|ui={(int)Ui}|s={Clean(Script)}|c={Clean(Connection)}|e={Clean(Event)}|t={At.ToUnixTimeSeconds()}|m={RssMb}|p={Clean(Planned)}");
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        return bytes.Length <= MaxSummaryBytes ? bytes : bytes[..MaxSummaryBytes];
    }

    /// <summary>Parses <see cref="ToSummary"/> output; null for anything else (another version, truncated past the fields).</summary>
    public static ProcessActivity? FromSummary(byte[]? summary)
    {
        if (summary is null || summary.Length == 0)
            return null;
        string text;
        try
        {
            text = Encoding.ASCII.GetString(summary);
        }
        catch (ArgumentException)
        {
            return null;
        }
        string[] parts = text.Split('|');
        if (parts.Length < 2 || parts[0] != "v1")
            return null;
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string part in parts.Skip(1))
        {
            int eq = part.IndexOf('=');
            if (eq > 0)
                values[part[..eq]] = part[(eq + 1)..];
        }
        if (!values.TryGetValue("ui", out string? ui) || !int.TryParse(ui, NumberStyles.Integer, CultureInfo.InvariantCulture, out int uiValue)
            || !Enum.IsDefined((UiState)uiValue))
            return null;
        DateTimeOffset at = values.TryGetValue("t", out string? t) && long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.MinValue;
        int rss = values.TryGetValue("m", out string? m) && int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mb) ? mb : -1;
        return new ProcessActivity((UiState)uiValue, Empty(values, "s"), Empty(values, "c"), Empty(values, "e"), at, Empty(values, "p"), rss);
    }

    /// <summary>The marker file lines for this state (key=value, no line breaks in values).</summary>
    internal string ToMarkerLines() => string.Create(CultureInfo.InvariantCulture,
        $"ui={(int)Ui}\nscript={Clean(Script)}\nconn={Clean(Connection)}\nevent={Clean(Event)}\nhb={At:O}\nrss={RssMb}\nplanned={Clean(Planned)}\n");

    internal static ProcessActivity? FromMarker(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("ui", out string? ui) || !int.TryParse(ui, NumberStyles.Integer, CultureInfo.InvariantCulture, out int uiValue)
            || !Enum.IsDefined((UiState)uiValue))
            return null;
        DateTimeOffset at = values.TryGetValue("hb", out string? hb)
            && DateTimeOffset.TryParse(hb, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
        int rss = values.TryGetValue("rss", out string? r) && int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mb) ? mb : -1;
        return new ProcessActivity((UiState)uiValue, Empty(values, "script"), Empty(values, "conn"), Empty(values, "event"), at, Empty(values, "planned"), rss);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        StringBuilder sb = new(Math.Min(value.Length, 24));
        foreach (char c in value)
        {
            if (sb.Length >= 24)
                break;
            sb.Append(c is >= ' ' and < (char)127 and not '|' and not '=' ? c : '_');
        }
        return sb.ToString();
    }

    private static string? Empty(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? v) && v.Length > 0 ? v : null;
}

/// <summary>One record of Android's process exit history (ApplicationExitInfo), platform neutral.</summary>
/// <param name="At">When the process ended.</param>
/// <param name="ProcessId">Its pid.</param>
/// <param name="ProcessName">Its process name.</param>
/// <param name="ReasonCode">ApplicationExitInfo.getReason().</param>
/// <param name="Status">ApplicationExitInfo.getStatus(): the signal or exit code.</param>
/// <param name="Description">ApplicationExitInfo.getDescription().</param>
/// <param name="Importance">RunningAppProcessInfo importance at death (100 foreground ... 400 cached).</param>
/// <param name="PssKb">Proportional set size at death, in kB (0 when unknown).</param>
/// <param name="RssKb">Resident set size at death, in kB (0 when unknown).</param>
/// <param name="Summary">The process state summary the process set for itself, parsed, if any.</param>
public sealed record ProcessExitRecord(
    DateTimeOffset At,
    int ProcessId,
    string ProcessName,
    int ReasonCode,
    int Status,
    int Importance,
    long PssKb,
    long RssKb,
    string? Description,
    ProcessActivity? Summary = null)
{
    public ExitReason Reason => Enum.IsDefined((ExitReason)ReasonCode) ? (ExitReason)ReasonCode : ExitReason.Unknown;

    /// <summary>The Android constant name (CRASH_NATIVE, ANR...).</summary>
    public string ReasonName => Enum.IsDefined((ExitReason)ReasonCode) ? ReasonConstant((ExitReason)ReasonCode) : $"UNKNOWN({ReasonCode})";

    /// <summary>What <see cref="Status"/> means for this reason: the signal for SIGNALED / CRASH_NATIVE, the exit code for EXIT_SELF.</summary>
    public string StatusText => Reason switch
    {
        ExitReason.Signaled or ExitReason.CrashNative => $"signal {Status}{SignalName(Status)}",
        ExitReason.ExitSelf => $"exit code {Status}",
        _ => Status.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>One line for the events log.</summary>
    public string Describe() => FormattableString.Invariant(
        $"pid {ProcessId} ({ProcessName}) ended {At:u}: {ReasonName}, {StatusText}, importance {Importance} ({ImportanceName(Importance)}), PSS {PssKb / 1024} MB, RSS {RssKb / 1024} MB{(string.IsNullOrEmpty(Description) ? string.Empty : $", \"{OneLine(Description)}\"")}");

    public static string ReasonConstant(ExitReason reason) => reason switch
    {
        ExitReason.Unknown => "UNKNOWN",
        ExitReason.ExitSelf => "EXIT_SELF",
        ExitReason.Signaled => "SIGNALED",
        ExitReason.LowMemory => "LOW_MEMORY",
        ExitReason.Crash => "CRASH",
        ExitReason.CrashNative => "CRASH_NATIVE",
        ExitReason.Anr => "ANR",
        ExitReason.InitializationFailure => "INITIALIZATION_FAILURE",
        ExitReason.PermissionChange => "PERMISSION_CHANGE",
        ExitReason.ExcessiveResourceUsage => "EXCESSIVE_RESOURCE_USAGE",
        ExitReason.UserRequested => "USER_REQUESTED",
        ExitReason.UserStopped => "USER_STOPPED",
        ExitReason.DependencyDied => "DEPENDENCY_DIED",
        ExitReason.Other => "OTHER",
        ExitReason.Freezer => "FREEZER",
        ExitReason.PackageStateChange => "PACKAGE_STATE_CHANGE",
        ExitReason.PackageUpdated => "PACKAGE_UPDATED",
        _ => reason.ToString(),
    };

    /// <summary>RunningAppProcessInfo.IMPORTANCE_* names.</summary>
    public static string ImportanceName(int importance) => importance switch
    {
        100 => "foreground",
        125 => "foreground service",
        150 => "top sleeping (pre-28)",
        200 => "visible",
        230 => "perceptible",
        300 => "service",
        325 => "top sleeping",
        350 => "can't save state",
        400 => "cached",
        1000 => "gone",
        _ => "?",
    };

    private static string SignalName(int signal) => signal switch
    {
        4 => " SIGILL",
        6 => " SIGABRT",
        7 => " SIGBUS",
        8 => " SIGFPE",
        9 => " SIGKILL",
        11 => " SIGSEGV",
        15 => " SIGTERM",
        _ => string.Empty,
    };

    internal static string OneLine(string text)
    {
        string s = text.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 300 ? s[..300] + "..." : s;
    }
}

/// <summary>What to do with one exit record.</summary>
public enum ExitVerdict
{
    /// <summary>Not this app's main process or not new: nothing.</summary>
    Ignore,

    /// <summary>A normal end (user, system housekeeping, planned restart): one line in the events log.</summary>
    LogOnly,

    /// <summary>Abnormal: a crash report for the crash screen.</summary>
    Report,
}

/// <summary>Decides which exits deserve the crash screen.</summary>
public static class ProcessExitClassifier
{
    /// <summary>RunningAppProcessInfo.IMPORTANCE_CACHED: at or above it the process was idle in the background.</summary>
    public const int ImportanceCached = 400;

    /// <param name="exit">The record.</param>
    /// <param name="mainProcessName">The app's main process name (the package name).</param>
    /// <param name="activity">What that process last recorded about itself (state summary, else the marker if it is for that pid).</param>
    public static ExitVerdict Classify(ProcessExitRecord exit, string mainProcessName, ProcessActivity? activity)
    {
        // The ":restart" trampoline (and any other auxiliary process) ends itself by design.
        if (!string.Equals(exit.ProcessName, mainProcessName, StringComparison.Ordinal))
            return ExitVerdict.LogOnly;
        if (activity?.Planned is not null)
            return ExitVerdict.LogOnly;

        // Was the app doing something the user cares about: visible, foreground service (a session or script) or a
        // script running? Then even a "system housekeeping" kill is worth explaining.
        bool busy = exit.Importance is > 0 and < ImportanceCached || activity is { ScriptBusy: true } || activity is { Ui: UiState.Foreground };

        return exit.Reason switch
        {
            ExitReason.Crash or ExitReason.CrashNative or ExitReason.Anr or ExitReason.InitializationFailure
                or ExitReason.ExcessiveResourceUsage or ExitReason.DependencyDied => ExitVerdict.Report,
            // A signal while idle in the background is ordinary process management (vendor task killers included).
            ExitReason.Signaled => busy ? ExitVerdict.Report : ExitVerdict.LogOnly,
            ExitReason.ExitSelf => exit.Status != 0 ? ExitVerdict.Report : ExitVerdict.LogOnly,
            ExitReason.LowMemory or ExitReason.Freezer => busy ? ExitVerdict.Report : ExitVerdict.LogOnly,
            // OTHER covers "remove task" (swiped away), app updates and similar on older Android versions: only when
            // a script was running or the UI was up, and only by our own record (importance alone is not enough).
            ExitReason.Other or ExitReason.Unknown => activity is { ScriptBusy: true } || activity is { Ui: UiState.Foreground } ? ExitVerdict.Report : ExitVerdict.LogOnly,
            // The user or the package manager ended it.
            _ => ExitVerdict.LogOnly,
        };
    }

    /// <summary>
    /// The records not handled yet: newer than the last one handled (by timestamp, then pid), not the current process,
    /// newest first. Without any history (first run of a version with this feature) only the last
    /// <paramref name="firstRunWindow"/> counts, so the exit that prompted the update is still explained.
    /// </summary>
    public static IReadOnlyList<ProcessExitRecord> SelectNew(IEnumerable<ProcessExitRecord> records, ExitHistoryMark? seen, int currentPid, DateTimeOffset now, TimeSpan firstRunWindow)
    {
        DateTimeOffset since = seen?.At ?? now - firstRunWindow;
        return records
            .Where(r => r.ProcessId != currentPid)
            .Where(r => seen is null ? r.At >= since : r.At > seen.At || (r.At == seen.At && !seen.ProcessIds.Contains(r.ProcessId)))
            .OrderByDescending(r => r.At)
            .ToList();
    }
}

/// <summary>The newest exit record already handled: its time and the pids handled at exactly that time.</summary>
public sealed record ExitHistoryMark(DateTimeOffset At, IReadOnlyList<int> ProcessIds)
{
    /// <summary>The mark after handling <paramref name="handled"/> on top of <paramref name="previous"/>.</summary>
    public static ExitHistoryMark? Advance(ExitHistoryMark? previous, IReadOnlyCollection<ProcessExitRecord> handled)
    {
        if (handled.Count == 0)
            return previous;
        DateTimeOffset newest = handled.Max(r => r.At);
        if (previous is not null && previous.At > newest)
            return previous;
        IEnumerable<int> pids = handled.Where(r => r.At == newest).Select(r => r.ProcessId);
        if (previous is not null && previous.At == newest)
            pids = pids.Concat(previous.ProcessIds);
        return new ExitHistoryMark(newest, pids.Distinct().ToList());
    }

    public string Serialize() => string.Create(CultureInfo.InvariantCulture,
        $"at={At.ToUnixTimeMilliseconds()}\npids={string.Join(',', ProcessIds)}\n");

    public static ExitHistoryMark? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        long? at = null;
        List<int> pids = new();
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("at=", StringComparison.Ordinal) && long.TryParse(line[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
                at = ms;
            else if (line.StartsWith("pids=", StringComparison.Ordinal))
            {
                foreach (string p in line[5..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                        pids.Add(pid);
                }
            }
        }
        if (at is null)
            return null;
        try
        {
            return new ExitHistoryMark(DateTimeOffset.FromUnixTimeMilliseconds(at.Value), pids);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>Builds the crash report text for an abnormal process exit.</summary>
public static class ProcessExitReport
{
    /// <summary>Bound for the trace part of a report (the whole report stays far below Android's share-intent limit).</summary>
    public const int MaxTraceChars = 64 * 1024;

    /// <summary>The report's "source" line.</summary>
    public static string Source(ProcessExitRecord exit) => exit.Reason switch
    {
        ExitReason.CrashNative => "previous run ended by a native crash (CRASH_NATIVE)",
        ExitReason.Crash => "previous run ended by a crash (CRASH)",
        ExitReason.Anr => "previous run was ended as not responding (ANR)",
        ExitReason.LowMemory => "previous run was killed by Android for memory (LOW_MEMORY)",
        _ => $"previous run ended abnormally ({exit.ReasonName})",
    };

    /// <summary>
    /// The details: what Android recorded about the death, what the app last recorded about itself, the trace (or why
    /// there is none), and the other exits since the last report.
    /// </summary>
    /// <param name="exit">The exit to report.</param>
    /// <param name="trace">Readable trace text (see <see cref="TraceText"/>), or null.</param>
    /// <param name="traceNote">Why there is no trace, or a note about it.</param>
    /// <param name="activity">The process's own last record (state summary or marker), or null.</param>
    /// <param name="others">The other new exits (any reason), newest first.</param>
    public static string Format(ProcessExitRecord exit, ProcessActivity? activity, string? trace, string? traceNote, IEnumerable<ProcessExitRecord> others)
    {
        StringBuilder sb = new();
        sb.Append("Android recorded how the previous run of the app ended (ApplicationExitInfo):\n");
        sb.Append("  reason: ").Append(exit.ReasonName).Append(" (").Append(exit.ReasonCode.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        sb.Append("  status: ").Append(exit.StatusText).Append('\n');
        sb.Append("  time: ").Append(exit.At.ToString("u", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("  process: ").Append(exit.ProcessName).Append(" pid ").Append(exit.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("  importance at death: ").Append(exit.Importance.ToString(CultureInfo.InvariantCulture)).Append(" (").Append(ProcessExitRecord.ImportanceName(exit.Importance)).Append(")\n");
        sb.Append("  memory at death: PSS ").Append((exit.PssKb / 1024).ToString(CultureInfo.InvariantCulture)).Append(" MB, RSS ")
            .Append((exit.RssKb / 1024).ToString(CultureInfo.InvariantCulture)).Append(" MB\n");
        sb.Append("  description: ").Append(string.IsNullOrEmpty(exit.Description) ? "(none)" : ProcessExitRecord.OneLine(exit.Description)).Append('\n');
        sb.Append("  last app state: ").Append(activity?.Describe() ?? "(not recorded)").Append('\n');
        sb.Append("  meaning: ").Append(Meaning(exit)).Append('\n');

        List<ProcessExitRecord> rest = others.Where(o => !ReferenceEquals(o, exit)).Take(15).ToList();
        if (rest.Count > 0)
        {
            sb.Append("\nOther exits since the last report (newest first):\n");
            foreach (ProcessExitRecord o in rest)
                sb.Append("  ").Append(o.Describe()).Append('\n');
        }

        sb.Append("\n--- trace ---\n");
        if (!string.IsNullOrEmpty(traceNote))
            sb.Append(traceNote).Append('\n');
        if (!string.IsNullOrEmpty(trace))
            sb.Append(trace.Length <= MaxTraceChars ? trace : trace[..MaxTraceChars] + "\n...(trace truncated)").Append('\n');
        else if (string.IsNullOrEmpty(traceNote))
            sb.Append("(Android kept no trace for this exit)\n");
        return sb.ToString();
    }

    private static string Meaning(ProcessExitRecord exit) => exit.Reason switch
    {
        ExitReason.CrashNative => "native code crashed (runtime, graphics driver, Skia, HarfBuzz...); the trace below is the tombstone",
        ExitReason.Crash => "an uncaught Java exception; the crash report of that run, if written, came first",
        ExitReason.Anr => "the main thread stopped responding and Android ended the app; the trace below shows what the threads were doing",
        ExitReason.LowMemory => "Android's low memory killer ended the process (the phone ran out of memory)",
        ExitReason.Signaled => "the process was killed by a signal (by Android or the vendor's task management)",
        ExitReason.ExcessiveResourceUsage => "Android ended the process for using too much CPU or memory in the background",
        ExitReason.Freezer => "the process was frozen and then ended by Android",
        ExitReason.DependencyDied => "a process the app depended on died",
        ExitReason.InitializationFailure => "the process failed to initialise",
        ExitReason.ExitSelf => "the process exited by itself with an error code",
        _ => "Android did not give a specific reason",
    };
}
