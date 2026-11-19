using System.Reflection;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Skua.App.Droid.Core.Diagnostics;

namespace Skua.App.Droid.Diagnostics;

/// <summary>
/// Crash capture that depends on neither Avalonia nor the engine. Installed first thing in the Application
/// constructor: managed unhandled exceptions (all threads, Java callbacks included), unobserved task exceptions and
/// uncaught Java throwables are written to an app-private crash report that <see cref="CrashActivity"/> shows on the
/// next launch. Startup milestones go to logcat (tag VibeSkua), to a rolling log and to a marker file, so a process
/// that dies without any exception (native crash, abort) is still localised.
/// </summary>
/// <remarks>
/// Nothing here may throw: every path is wrapped, and a failure to write only costs the diagnostic. All text passes
/// through <see cref="DiagnosticText.Scrub"/>; callers never pass credentials or tokens in the first place.
/// </remarks>
internal static class CrashCapture
{
    public const string Tag = "VibeSkua";

    private static readonly object s_lock = new();
    private static readonly DateTimeOffset s_processStart = DateTimeOffset.UtcNow;
    private static bool s_handlersInstalled;
    private static bool s_trackStartup;
    private static int s_fatalWritten;
    private static readonly object s_markerLock = new();
    private static StartupStep s_lastStep;
    private static EngineInitPhase s_engine;
    private static DiagnosticsStore? s_store;
    private static Context? s_context;
    private static ProcessActivity? s_activity;
    private static string? s_lastSummary;
    private static System.Threading.Timer? s_heartbeat;

    /// <summary>How often a running process records what it is doing (marker, events log, process state summary).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The engine's state for heartbeats: script state, connection phase. Set once the engine is composed; must be cheap
    /// and thread-safe (it reads the engine's immutable snapshot).
    /// </summary>
    public static Func<(string? Script, string? Connection)>? EngineState { get; set; }

    /// <summary>The diagnostics files, once a Context was available.</summary>
    public static DiagnosticsStore? Store => s_store;

    /// <summary>What the previous process left in the startup marker (read before this process overwrote it).</summary>
    public static StartupMarker? PreviousStartup { get; private set; }

    public static StartupStep LastStep => s_lastStep;

    /// <summary>Hooks every unhandled-exception path. Idempotent; needs no Context.</summary>
    public static void InstallHandlers()
    {
        lock (s_lock)
        {
            if (s_handlersInstalled)
                return;
            s_handlersInstalled = true;
        }
        TryRun("hook AppDomain", () => AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            OnFatal("AppDomain.UnhandledException" + (e.IsTerminating ? " (terminating)" : string.Empty), e.ExceptionObject as Exception, e.ExceptionObject?.ToString()));
        TryRun("hook AndroidEnvironment", () => AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            OnFatal("AndroidEnvironment.UnhandledExceptionRaiser", e.Exception, null));
        TryRun("hook TaskScheduler", () => TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Not fatal in .NET; recorded so a background failure is visible, then marked observed.
            NonFatal("unobserved task exception", e.Exception);
            e.SetObserved();
        });
        TryRun("hook Java default handler", () =>
        {
            Java.Lang.Thread.IUncaughtExceptionHandler? previous = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
            Java.Lang.Thread.DefaultUncaughtExceptionHandler = new JavaHandler(previous);
        });
    }

    /// <summary>
    /// Attaches the diagnostics directory (app-private files/diagnostics) and, for the main process, starts startup
    /// tracking: reads the previous marker, turns an unexpected previous end into a pending report, writes step 1.
    /// Idempotent; returns false while no Context directory is available yet.
    /// </summary>
    public static bool Attach(Context context, bool trackStartup)
    {
        lock (s_lock)
        {
            if (s_store is not null)
                return true;
            try
            {
                string? files = context.FilesDir?.AbsolutePath;
                if (string.IsNullOrEmpty(files))
                    return false;
                s_context = context.ApplicationContext ?? context;
                s_store = new DiagnosticsStore(Path.Combine(files, "diagnostics"));
            }
            catch (Exception e)
            {
                Android.Util.Log.Warn(Tag, "Diagnostics directory unavailable: " + e.GetType().Name);
                return false;
            }
            s_trackStartup = trackStartup;
        }

        if (trackStartup)
        {
            StartupMarker? previous = s_store.ReadMarker();
            PreviousStartup = previous;
            if (previous is { EndedUnexpectedly: true } && previous.ProcessId != Process.MyPid() && !s_store.HasPendingReport)
                s_store.WriteCrashReport("previous launch ended during startup", DiagnosticsStore.DescribeMarker(previous), Context());
            s_store.Log($"process start: {ProcessInfo.CurrentName()} pid {Process.MyPid()}" +
                (previous is null ? string.Empty : $"; previous pid {previous.ProcessId} reached {previous.LastStep}") +
                (previous?.Activity is { } last ? $"; its last record: {last.Describe()}" : string.Empty));
            Step(StartupStep.ProcessStarted);
        }
        return true;
    }

    /// <summary>Records a startup milestone (logcat, log file, marker). Steps only move forward.</summary>
    public static void Step(StartupStep step, string? detail = null)
    {
        try
        {
            string text = $"startup {(int)step} {step}{(detail is null ? string.Empty : ": " + detail)}";
            Android.Util.Log.Info(Tag, DiagnosticText.Scrub(text));
            lock (s_markerLock)
            {
                if (step <= s_lastStep)
                    return;
                s_lastStep = step;
                DiagnosticsStore? store = s_store;
                if (store is null || !s_trackStartup)
                    return;
                store.Log(text);
                store.WriteMarker(Process.MyPid(), s_processStart, step, s_engine, s_activity);
            }
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
    }

    /// <summary>
    /// Records the engine composition, which runs on a background thread next to the startup steps: in the marker
    /// alongside the last step, so a process that dies while composing is recognised whatever the UI reached.
    /// </summary>
    public static void Engine(EngineInitPhase phase, string? detail = null)
    {
        try
        {
            string text = $"startup engine composition {phase.ToString().ToLowerInvariant()}{(detail is null ? string.Empty : ": " + detail)}";
            Android.Util.Log.Info(Tag, DiagnosticText.Scrub(text));
            lock (s_markerLock)
            {
                s_engine = phase;
                DiagnosticsStore? store = s_store;
                if (store is null || !s_trackStartup)
                    return;
                store.Log(text);
                store.WriteMarker(Process.MyPid(), s_processStart, s_lastStep, phase, s_activity);
            }
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
    }

    /// <summary>
    /// Records a lifecycle transition (activity, surface, service, trim memory) or a heartbeat: what the process is doing
    /// now goes to the marker, one line to the events log and, when it changed, Android's process state summary (which
    /// Android returns with this process's exit record). So the next start can say what the app was doing just before it
    /// died. Never throws; cheap enough for lifecycle callbacks on the main thread (one small file write).
    /// </summary>
    /// <param name="evt">Short event name ("onStop", "surfaceDestroyed", "trimMemory 20", "heartbeat").</param>
    /// <param name="ui">The new UI state, when the event changes it.</param>
    public static void Lifecycle(string evt, UiState? ui = null)
    {
        try
        {
            ProcessActivity activity;
            lock (s_markerLock)
            {
                activity = Capture(evt, ui, planned: null);
                s_activity = activity;
                if (s_store is { } store && s_trackStartup)
                    store.WriteMarker(Process.MyPid(), s_processStart, s_lastStep, s_engine, activity);
            }
            string line = (evt == "heartbeat" ? "heartbeat: " : "lifecycle " + evt + ": ") + activity.Describe();
            Android.Util.Log.Info(Tag, line);
            s_store?.Log(line);
            UpdateStateSummary(activity);
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
    }

    /// <summary>
    /// Records that this process is about to end itself on purpose (restart), so the next start does not report the
    /// kill. Synchronous: call it right before the kill.
    /// </summary>
    public static void PlannedExit(string reason)
    {
        try
        {
            ProcessActivity activity;
            lock (s_markerLock)
            {
                activity = Capture("planned exit", null, reason);
                s_activity = activity;
                if (s_store is { } store && s_trackStartup)
                    store.WriteMarker(Process.MyPid(), s_processStart, s_lastStep, s_engine, activity);
            }
            s_store?.Log("planned exit: " + reason);
            UpdateStateSummary(activity);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Starts the periodic heartbeat (main process). Idempotent; off the main thread.</summary>
    public static void StartHeartbeat()
    {
        try
        {
            lock (s_lock)
            {
                if (s_heartbeat is not null || !s_trackStartup)
                    return;
                s_heartbeat = new System.Threading.Timer(_ => Lifecycle("heartbeat"), null, HeartbeatInterval, HeartbeatInterval);
            }
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn(Tag, "Heartbeat not started: " + e.GetType().Name);
        }
    }

    /// <summary>What the process was doing at the last heartbeat or lifecycle event (null before the first).</summary>
    public static ProcessActivity? CurrentActivity => s_activity;

    private static ProcessActivity Capture(string evt, UiState? ui, string? planned)
    {
        ProcessActivity previous = s_activity ?? ProcessActivity.Initial(DateTimeOffset.UtcNow);
        (string? script, string? connection) = (previous.Script, previous.Connection);
        try
        {
            if (EngineState is { } state)
                (script, connection) = state();
        }
        catch (Exception)
        {
        }
        return new ProcessActivity(ui ?? previous.Ui, script, connection, evt, DateTimeOffset.UtcNow, planned ?? previous.Planned, ReadRssMb());
    }

    /// <summary>ActivityManager.setProcessStateSummary (API 30+), only when the meaningful part changed (it is a binder call).</summary>
    private static void UpdateStateSummary(ProcessActivity activity)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return;
        // The heartbeat time and memory change every time; the summary is only refreshed on a state change or every
        // few minutes, so the exit record still carries a recent picture without a binder call per heartbeat.
        string key = $"{activity.Ui}|{activity.Script}|{activity.Connection}|{activity.Planned}|{activity.At.ToUnixTimeSeconds() / 300}";
        if (key == Volatile.Read(ref s_lastSummary) && activity.Event == "heartbeat")
            return;
        Volatile.Write(ref s_lastSummary, key);
        try
        {
            if (s_context?.GetSystemService(Android.Content.Context.ActivityService) is ActivityManager am)
                am.SetProcessStateSummary(activity.ToSummary());
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn(Tag, "Process state summary not set: " + e.GetType().Name);
        }
    }

    /// <summary>Resident memory from /proc/self/statm, in MB; -1 if unreadable.</summary>
    private static int ReadRssMb()
    {
        try
        {
            string[] fields = File.ReadAllText("/proc/self/statm").Split(' ');
            return fields.Length > 1 && long.TryParse(fields[1], out long pages)
                ? (int)(pages * System.Environment.SystemPageSize / (1024 * 1024))
                : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public static void Info(string message)
    {
        try
        {
            string text = DiagnosticText.Scrub(message);
            Android.Util.Log.Info(Tag, text);
            s_store?.Log(text);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>A failure the app recovered from: logged with its full stack, no crash screen.</summary>
    public static void NonFatal(string where, Exception exception)
    {
        try
        {
            string text = DiagnosticText.Scrub($"{where} failed: {DiagnosticText.Describe(exception)}");
            Android.Util.Log.Warn(Tag, text);
            s_store?.Log(text);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// A failure the app survived but that leaves it unusable (e.g. the engine could not be composed): logged and
    /// written as the pending report, so the crash screen explains it.
    /// </summary>
    public static void Recovered(string where, Exception exception)
    {
        NonFatal(where, exception);
        try
        {
            s_store?.WriteCrashReport($"{where} (the app kept running without it)", DiagnosticText.Describe(exception), Context());
        }
        catch (Exception)
        {
        }
    }

    private static void OnFatal(string source, Exception? exception, string? fallbackText)
    {
        try
        {
            string details = exception is not null ? DiagnosticText.Describe(exception) : fallbackText ?? "(no exception object)";
            Android.Util.Log.Error(Tag, DiagnosticText.Scrub($"FATAL ({source}) at startup step {s_lastStep}: {details}"));
            if (Interlocked.Exchange(ref s_fatalWritten, 1) != 0)
            {
                s_store?.Log($"also reported by {source}");
                return;
            }
            s_store?.WriteCrashReport(source, details, Context());
        }
        catch (Exception)
        {
        }
    }

    /// <summary>App version, device, API level and process facts for the report header. Never throws.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Context()
    {
        List<KeyValuePair<string, string>> items = new();
        void Add(string key, Func<string?> value)
        {
            try
            {
                items.Add(new(key, value() ?? "?"));
            }
            catch (Exception e)
            {
                items.Add(new(key, "? (" + e.GetType().Name + ")"));
            }
        }

        Add("app", () =>
        {
            Context? c = s_context;
            PackageInfo? info = c?.PackageManager?.GetPackageInfo(c.PackageName!, 0);
            long code = info is null ? -1 : OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;
            return $"{c?.PackageName} {info?.VersionName} ({code})";
        });
        Add("build", () =>
        {
            Assembly asm = typeof(CrashCapture).Assembly;
            string config = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "Configuration")?.Value ?? "?";
            return $"{config}, {asm.GetName().Version}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";
        });
        Add("device", () => $"{Build.Manufacturer} {Build.Model} ({Build.Device}, {Build.Hardware})");
        Add("android", () => $"{Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}), patch {Build.VERSION.SecurityPatch}");
        Add("abi", () => string.Join(",", Build.SupportedAbis ?? []));
        Add("process", () => $"{ProcessInfo.CurrentName()} pid {Process.MyPid()}, up {(DateTimeOffset.UtcNow - s_processStart).TotalSeconds:0.0} s");
        Add("startup step", () => $"{(int)s_lastStep} {s_lastStep}, engine composition {s_engine}");
        Add("thread", () => $"{System.Environment.CurrentManagedThreadId} {Thread.CurrentThread.Name}");
        Add("memory", () => $"managed {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
        Add("app state", () => s_activity?.Describe() ?? "(none recorded in this process)");
        return items;
    }

    private static void TryRun(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn(Tag, $"Crash capture: could not {what}: {e.GetType().Name}");
        }
    }

    /// <summary>Records uncaught Java throwables (including ones thrown in pure Java code), then lets the previous handler end the process.</summary>
    private sealed class JavaHandler : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
    {
        private readonly Java.Lang.Thread.IUncaughtExceptionHandler? _previous;

        public JavaHandler(Java.Lang.Thread.IUncaughtExceptionHandler? previous)
        {
            _previous = previous;
        }

        public void UncaughtException(Java.Lang.Thread t, Java.Lang.Throwable e)
        {
            try
            {
                OnFatal($"uncaught Java throwable on thread '{t.Name}'", null, Android.Util.Log.GetStackTraceString(e));
            }
            catch (Exception)
            {
            }
            _previous?.UncaughtException(t, e);
        }
    }
}
