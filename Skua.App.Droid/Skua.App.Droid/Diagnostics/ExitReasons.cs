using Android.App;
using Android.Content;
using Android.OS;
using Skua.App.Droid.Core.Diagnostics;

namespace Skua.App.Droid.Diagnostics;

/// <summary>
/// Reads how earlier runs of the app ended from Android's own records (ActivityManager.getHistoricalProcessExitReasons,
/// Android 11+), so a native crash, an ANR or a kill for memory is explained on the next start even though no code of
/// the app ran at the moment of death. Abnormal exits become the pending crash report that <see cref="CrashActivity"/>
/// shows (or are added to the report the dying process managed to write), every new exit is one line in the events log,
/// and the newest handled exit is remembered so nothing is reported twice.
/// </summary>
/// <remarks>
/// Runs once per process, early in Application.onCreate of the main process (so before the launcher, a task restored
/// from Recents, or the service restarted by Android after the death), on the main thread: one binder call, plus
/// reading a bounded trace only when there is something new to report. Below Android 11 the startup marker's last
/// heartbeat is the only evidence (see <see cref="StartupMarker.DiedWhileBusy"/>). Never throws.
/// </remarks>
internal static class ExitReasons
{
    /// <summary>How many records Android is asked for (it keeps a few per process name).</summary>
    public const int MaxRecords = 16;

    /// <summary>On the first run with this feature, exits older than this are listed in the log only.</summary>
    public static readonly TimeSpan FirstRunWindow = TimeSpan.FromDays(3);

    private static int s_scanned;

    public static void ScanOnce(Context context)
    {
        if (Interlocked.Exchange(ref s_scanned, 1) != 0)
            return;
        DiagnosticsStore? store = CrashCapture.Store;
        if (store is null)
            return;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                Scan(context, store);
            else
                ScanMarkerOnly(store);
        }
        catch (Exception e)
        {
            CrashCapture.NonFatal("reading the previous exit reasons", e);
        }
    }

    /// <summary>Below Android 11: a process that died while a script ran is reported from its last heartbeat.</summary>
    private static void ScanMarkerOnly(DiagnosticsStore store)
    {
        StartupMarker? previous = CrashCapture.PreviousStartup;
        if (previous is { DiedWhileBusy: true } && previous.ProcessId != Process.MyPid() && !store.HasPendingReport)
            store.WriteCrashReport("previous run ended while a script was running", DiagnosticsStore.DescribeBusyDeath(previous), CrashCapture.Context());
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private static void Scan(Context context, DiagnosticsStore store)
    {
        if (context.GetSystemService(Context.ActivityService) is not ActivityManager am)
            return;
        IList<ApplicationExitInfo> infos = am.GetHistoricalProcessExitReasons(null, 0, MaxRecords);
        if (infos.Count == 0)
            return;

        StartupMarker? previous = CrashCapture.PreviousStartup;
        List<(ProcessExitRecord Record, ApplicationExitInfo Info)> all = new();
        foreach (ApplicationExitInfo info in infos)
            all.Add((ToRecord(info, previous), info));

        ExitHistoryMark? seen = store.ReadExitHistoryMark();
        IReadOnlyList<ProcessExitRecord> fresh = ProcessExitClassifier.SelectNew(all.Select(a => a.Record), seen, Process.MyPid(), DateTimeOffset.UtcNow, FirstRunWindow);
        if (fresh.Count == 0)
            return;

        string mainProcess = context.PackageName ?? string.Empty;
        ProcessExitRecord? report = null;
        foreach (ProcessExitRecord record in fresh)
        {
            ExitVerdict verdict = ProcessExitClassifier.Classify(record, mainProcess, record.Summary);
            store.Log($"previous exit ({verdict}): {record.Describe()}; last state: {record.Summary?.Describe() ?? "(not recorded)"}");
            if (verdict == ExitVerdict.Report && report is null)
                report = record; // newest first: the most recent abnormal exit gets the full report
        }

        if (report is not null)
        {
            ApplicationExitInfo info = all.First(a => ReferenceEquals(a.Record, report)).Info;
            (string? trace, string? note) = ReadTrace(info, report.Reason);
            string details = ProcessExitReport.Format(report, report.Summary, trace, note, fresh);
            // A report the dying process wrote itself (managed or Java crash, startup marker) comes first; Android's
            // record of the same death is added to it. Otherwise this is the report.
            if (!store.AppendToPendingReport($"how Android says the run ended ({report.ReasonName})", details))
                store.WriteCrashReport(ProcessExitReport.Source(report), details, CrashCapture.Context());
        }
        store.WriteExitHistoryMark(ExitHistoryMark.Advance(seen, fresh)!);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private static ProcessExitRecord ToRecord(ApplicationExitInfo info, StartupMarker? previous)
    {
        ProcessActivity? summary = null;
        try
        {
            summary = ProcessActivity.FromSummary(info.GetProcessStateSummary());
        }
        catch (Exception)
        {
        }
        // The marker of the previous start describes the same process when the pids match (the summary is the better
        // source: it is attached by Android to exactly that process).
        if (summary is null && previous is not null && previous.ProcessId == info.Pid)
            summary = previous.Activity;
        return new ProcessExitRecord(
            DateTimeOffset.FromUnixTimeMilliseconds(info.Timestamp),
            info.Pid,
            info.ProcessName ?? "?",
            (int)info.Reason,
            info.Status,
            (int)info.Importance,
            info.Pss,
            info.Rss,
            info.Description,
            summary);
    }

    /// <summary>The trace Android kept for this exit (ANR traces; native tombstones on 12+), bounded and decoded.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private static (string? Trace, string? Note) ReadTrace(ApplicationExitInfo info, ExitReason reason)
    {
        try
        {
            using Stream? stream = info.TraceInputStream;
            if (stream is null)
            {
                string note = reason switch
                {
                    ExitReason.CrashNative when !OperatingSystem.IsAndroidVersionAtLeast(31) =>
                        "Android 11 keeps no native crash tombstone with the exit record (Android 12+ does). Developer options > Take bug report has it.",
                    ExitReason.CrashNative or ExitReason.Anr => "Android kept no trace for this exit (it may have been rotated out already).",
                    _ => "Android keeps traces only for ANRs and native crashes.",
                };
                return (null, note);
            }
            byte[] buffer = new byte[TraceText.MaxInputBytes];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0)
                    break;
                total += read;
            }
            bool truncated = total == buffer.Length && stream.ReadByte() >= 0;
            return (TraceText.Format(buffer[..total], truncated), null);
        }
        catch (Exception e)
        {
            return (null, $"The trace could not be read: {e.GetType().Name}");
        }
    }
}
