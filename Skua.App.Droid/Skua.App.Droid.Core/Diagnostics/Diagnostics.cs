using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Skua.App.Droid.Core.Security;

namespace Skua.App.Droid.Core.Diagnostics;

/// <summary>
/// Startup milestones, in order. The Android head records the last one reached in a marker file, so a launch that
/// dies without a managed exception (native crash, runtime abort, killed) is still localised on the next launch.
/// </summary>
public enum StartupStep
{
    None = 0,

    /// <summary>The managed Application object exists (its constructor ran).</summary>
    ProcessStarted = 1,

    /// <summary>Application.OnCreate began.</summary>
    ApplicationCreate = 2,

    /// <summary>Engine composition (paths, settings, Keystore, script references, engine) began.</summary>
    EngineInitStarted = 3,

    /// <summary>Engine composition finished (or failed and was recorded).</summary>
    EngineInitFinished = 4,

    /// <summary>Application.OnCreate finished. A process started only for the service or a receiver stops here.</summary>
    ApplicationReady = 5,

    /// <summary>The launcher activity (plain Android) runs.</summary>
    LauncherActivity = 6,

    /// <summary>The Avalonia activity's OnCreate began.</summary>
    MainActivityCreate = 7,

    /// <summary>Avalonia's AppBuilder setup began.</summary>
    AvaloniaInitStarted = 8,

    /// <summary>Avalonia's AppBuilder setup finished.</summary>
    AvaloniaInitFinished = 9,

    /// <summary>The main view (and the root ViewModel) was created.</summary>
    MainViewCreated = 10,

    /// <summary>The Avalonia activity's OnCreate finished.</summary>
    MainActivityCreated = 11,

    /// <summary>The Avalonia activity resumed.</summary>
    MainActivityResumed = 12,

    /// <summary>The first layout pass after resuming ran: startup is complete.</summary>
    FirstFrame = 13,
}

/// <summary>What the marker file says about one process start.</summary>
public sealed record StartupMarker(int ProcessId, DateTimeOffset StartedAt, StartupStep LastStep, DateTimeOffset LastStepAt)
{
    public bool Completed => LastStep >= StartupStep.FirstFrame;

    /// <summary>
    /// The process ended at a point where it should not have: inside Application.OnCreate, or while the UI was being
    /// brought up. A process that reached <see cref="StartupStep.ApplicationReady"/> or
    /// <see cref="StartupStep.LauncherActivity"/> and then went away is a normal background process (service or
    /// receiver only) that the system later reclaimed.
    /// </summary>
    public bool EndedUnexpectedly => LastStep is > StartupStep.None and < StartupStep.ApplicationReady
        || (LastStep >= StartupStep.MainActivityCreate && LastStep < StartupStep.FirstFrame);

    /// <summary>The previous process died while composing the engine: skip it once so the crash screen can show.</summary>
    public bool DiedInEngineInit => LastStep == StartupStep.EngineInitStarted;
}

/// <summary>Removes secrets from any diagnostic text before it is written or shown.</summary>
/// <remarks>
/// Two layers: every value registered with <see cref="Secrets"/> (the engine registers the password and the login
/// tokens there), and key/value patterns that look like credentials (<c>pass=...</c>, <c>"token": "..."</c>,
/// <c>Authorization: ...</c>). Diagnostics must never quote credentials in the first place; this is the safety net.
/// </remarks>
public static class DiagnosticText
{
    /// <summary>Process-wide registry of secrets that must never appear in diagnostics.</summary>
    public static SecretScrubber Secrets { get; } = new();

    // key [quote] separator [quote] value. Keys are matched case-insensitively, as whole words or with a prefix
    // (sToken, loginToken, user_password). The value stops at whitespace, quotes, & , ; < > or a closing bracket.
    private static readonly Regex s_keyValue = new(
        @"(?i)(\b[a-z_]*(?:pass(?:word|wd)?|pwd|token|secret|api[_-]?key|session(?:id)?|cookie|authorization|credentials?)\b[""']?\s*[:=]\s*[""']?)(?:(?:bearer|basic)\s+)?[^\s""'&,;<>)\]}]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        string result = Secrets.Scrub(text);
        try
        {
            result = s_keyValue.Replace(result, m => m.Groups[1].Value + SecretScrubber.Marker);
        }
        catch (RegexMatchTimeoutException)
        {
            return "<diagnostic text removed: could not be checked for secrets>";
        }
        return result;
    }

    /// <summary>The full exception: type, message, stack, inner exceptions (AggregateException flattened).</summary>
    public static string Describe(Exception exception)
    {
        Exception e = exception is AggregateException agg ? agg.Flatten() : exception;
        string text;
        try
        {
            text = e.ToString();
        }
        catch (Exception inner)
        {
            // A broken ToString override must not stop the report.
            text = $"{e.GetType().FullName}: (ToString failed: {inner.GetType().Name})";
        }
        return text;
    }
}

/// <summary>
/// App-private diagnostics files: one pending crash report (shown by the crash screen on the next launch), a rolling
/// startup/event log and the startup marker. Every write is scrubbed, best effort and never throws: a failure here must
/// never become a crash of its own.
/// </summary>
public sealed class DiagnosticsStore
{
    public const string CrashReportFile = "crash-report.txt";
    public const string LogFile = "events.log";
    public const string MarkerFile = "startup-marker.txt";

    /// <summary>The log rolls over at this size (one previous file is kept).</summary>
    public const int MaxLogBytes = 128 * 1024;

    private readonly object _lock = new();
    private readonly TimeProvider _time;

    public DiagnosticsStore(string directory, TimeProvider? time = null)
    {
        Directory = directory;
        _time = time ?? TimeProvider.System;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public string Directory { get; }

    public string CrashReportPath => Path.Combine(Directory, CrashReportFile);

    public string LogPath => Path.Combine(Directory, LogFile);

    public string MarkerPath => Path.Combine(Directory, MarkerFile);

    /// <summary>A crash report is waiting to be shown.</summary>
    public bool HasPendingReport
    {
        get
        {
            try
            {
                return File.Exists(CrashReportPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Writes the pending crash report. Only the first report is kept until it has been shown and cleared: the first
    /// failure is the one that matters, later ones (a crash loop) are appended to the log instead.
    /// </summary>
    /// <returns>True when this call wrote the report.</returns>
    public bool WriteCrashReport(string source, string details, IEnumerable<KeyValuePair<string, string>> context)
    {
        try
        {
            StringBuilder sb = new();
            sb.Append("VibeSkua crash report").Append('\n');
            sb.Append("time: ").Append(_time.GetUtcNow().ToString("u", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("source: ").Append(source).Append('\n');
            foreach (KeyValuePair<string, string> item in context)
                sb.Append(item.Key).Append(": ").Append(item.Value).Append('\n');
            sb.Append('\n').Append(details).Append('\n');
            string text = DiagnosticText.Scrub(sb.ToString());

            lock (_lock)
            {
                if (File.Exists(CrashReportPath))
                {
                    AppendLogLocked($"another failure while a report is pending ({source}): {FirstLine(details)}");
                    return false;
                }
                WriteAtomic(CrashReportPath, text);
                AppendLogLocked($"crash report written ({source})");
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The pending report (scrubbed again on the way out), or null.</summary>
    public string? ReadPendingReport()
    {
        try
        {
            lock (_lock)
                return File.Exists(CrashReportPath) ? DiagnosticText.Scrub(File.ReadAllText(CrashReportPath)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void ClearPendingReport()
    {
        try
        {
            lock (_lock)
            {
                File.Delete(CrashReportPath);
                AppendLogLocked("crash report cleared");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Appends one timestamped, scrubbed line to the rolling log.</summary>
    public void Log(string message)
    {
        try
        {
            lock (_lock)
                AppendLogLocked(message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The last <paramref name="lines"/> lines of the log (previous file included), scrubbed.</summary>
    public IReadOnlyList<string> ReadLogTail(int lines)
    {
        try
        {
            lock (_lock)
            {
                List<string> all = new();
                string previous = LogPath + ".1";
                if (File.Exists(previous))
                    all.AddRange(File.ReadAllLines(previous));
                if (File.Exists(LogPath))
                    all.AddRange(File.ReadAllLines(LogPath));
                return all.Skip(Math.Max(0, all.Count - lines)).Select(DiagnosticText.Scrub).ToList();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>What the previous process recorded, or null when there is no (readable) marker.</summary>
    public StartupMarker? ReadMarker()
    {
        try
        {
            string path = MarkerPath;
            if (!File.Exists(path))
                return null;
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                    values[line[..eq]] = line[(eq + 1)..];
            }
            if (!values.TryGetValue("pid", out string? pid) || !int.TryParse(pid, NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId)
                || !values.TryGetValue("step", out string? step) || !int.TryParse(step, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stepNumber)
                || !Enum.IsDefined((StartupStep)stepNumber))
                return null;
            return new StartupMarker(processId, ParseTime(values, "started"), (StartupStep)stepNumber, ParseTime(values, "at"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Records that process <paramref name="processId"/> reached <paramref name="step"/>.</summary>
    public void WriteMarker(int processId, DateTimeOffset startedAt, StartupStep step)
    {
        try
        {
            string text = string.Create(CultureInfo.InvariantCulture,
                $"pid={processId}\nstarted={startedAt:O}\nstep={(int)step}\nname={step}\nat={_time.GetUtcNow():O}\n");
            lock (_lock)
                WriteAtomic(MarkerPath, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Human-readable summary of a marker for the crash report.</summary>
    public static string DescribeMarker(StartupMarker marker) => FormattableString.Invariant(
        $"The previous start (pid {marker.ProcessId}, {marker.StartedAt:u}) last reached step {(int)marker.LastStep} ({marker.LastStep}) at {marker.LastStepAt:u} and then ended without a captured exception. That points to a native crash, a runtime abort or the process being killed during startup.");

    private void AppendLogLocked(string message)
    {
        string line = $"{_time.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{Environment.ProcessId}] {DiagnosticText.Scrub(message).Replace('\n', ' ').Replace('\r', ' ')}\n";
        string path = LogPath;
        FileInfo info = new(path);
        if (info.Exists && info.Length + line.Length > MaxLogBytes)
            File.Move(path, path + ".1", overwrite: true);
        File.AppendAllText(path, line);
    }

    private static void WriteAtomic(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    private static DateTimeOffset ParseTime(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset t)
            ? t
            : DateTimeOffset.MinValue;

    private static string FirstLine(string details)
    {
        string first = details.Split('\n', 2)[0];
        return first.Length > 300 ? first[..300] : first;
    }
}
