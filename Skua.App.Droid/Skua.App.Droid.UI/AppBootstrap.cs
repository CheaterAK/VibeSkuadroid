using Skua.App.Droid.Core.ViewModels;

namespace Skua.App.Droid.UI;

/// <summary>Process-wide hand-over from the platform head to the views.</summary>
public static class AppBootstrap
{
    private static readonly object s_lock = new();
    private static ShellViewModel? s_shell;

    /// <summary>Creates the services; set by the platform head before the first view is created.</summary>
    public static Func<AppServices>? Factory { get; set; }

    /// <summary>
    /// Records a failure the UI recovered from (the platform head writes it to the crash report). Set by the head;
    /// null in tests and the desktop preview.
    /// </summary>
    public static Action<string, Exception>? ReportError { get; set; }

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

    /// <summary>For tests: forget the root ViewModel.</summary>
    public static void Reset()
    {
        lock (s_lock)
        {
            s_shell?.Dispose();
            s_shell = null;
        }
    }
}
