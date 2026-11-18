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
                (previous is null ? string.Empty : $"; previous pid {previous.ProcessId} reached {previous.LastStep}"));
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
                store.WriteMarker(Process.MyPid(), s_processStart, step, s_engine);
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
                store.WriteMarker(Process.MyPid(), s_processStart, s_lastStep, phase);
            }
        }
        catch (Exception)
        {
            // Diagnostics only.
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
