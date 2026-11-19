using Skua.App.Droid.Core.ViewModels;

namespace Skua.App.Droid.UI;

/// <summary>Process-wide hand-over from the platform head to the views.</summary>
public static class AppBootstrap
{
    private static readonly object s_lock = new();
    private static ShellViewModel? s_shell;
    private static bool s_uiVisible = true;

    /// <summary>Creates the services; set by the platform head before the first view is created.</summary>
    public static Func<AppServices>? Factory { get; set; }

    /// <summary>
    /// Records a failure the UI recovered from (the platform head writes it to the crash report). Set by the head;
    /// null in tests and the desktop preview.
    /// </summary>
    public static Action<string, Exception>? ReportError { get; set; }

    /// <summary>
    /// Starts (if needed) and returns the platform's engine composition, which completes with whether the engine is
    /// available and never faults. While it runs, a new main view shows a short "starting" state and becomes the main
    /// view (or the error view, if the engine is unavailable) when it completes. Null: the engine is composed before
    /// the UI (desktop preview, tests).
    /// </summary>
    public static Func<Task<bool>>? EngineReady { get; set; }

    /// <summary>Called after a main view was created (startup milestone for the platform head).</summary>
    public static Action? MainViewCreated { get; set; }

    /// <summary>The root ViewModel (created on first use, on the UI thread, then shared by every view).</summary>
    public static ShellViewModel Shell
    {
        get
        {
            lock (s_lock)
            {
                if (s_shell is null)
                {
                    AppServices services = (Factory ?? throw new InvalidOperationException("AppBootstrap.Factory was not set by the platform head."))();
                    s_shell = new ShellViewModel(services);
                    if (!s_uiVisible)
                        s_shell.SetUiVisible(false);
                }
                return s_shell;
            }
        }
    }

    /// <summary>The root ViewModel if it exists already.</summary>
    public static ShellViewModel? ShellIfCreated
    {
        get
        {
            lock (s_lock)
                return s_shell;
        }
    }

    /// <summary>
    /// The platform head reports whether the UI is showing (Android: the activity was started or stopped); applied to
    /// the root ViewModel now or when it is created.
    /// </summary>
    public static void SetUiVisible(bool visible)
    {
        ShellViewModel? shell;
        lock (s_lock)
        {
            s_uiVisible = visible;
            shell = s_shell;
        }
        shell?.SetUiVisible(visible);
    }

    /// <summary>For tests: forget the root ViewModel.</summary>
    public static void Reset()
    {
        lock (s_lock)
        {
            s_shell?.Dispose();
            s_shell = null;
            s_uiVisible = true;
        }
    }
}
