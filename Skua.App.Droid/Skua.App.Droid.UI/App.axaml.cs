using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.UI.Views;

namespace Skua.App.Droid.UI;

/// <summary>
/// The Avalonia application. The platform head sets <see cref="AppBootstrap.Factory"/> before Avalonia starts;
/// the root ViewModel is created once per process and every new view (one per Android activity) binds to it.
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            case IActivityApplicationLifetime activity:
                // Avalonia 12 on Android: one view per activity instance; the ViewModel outlives them.
                activity.MainViewFactory = CreateMainView;
                break;
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new Window
                {
                    Title = "VibeSkua (Android UI preview)",
                    Width = 420,
                    Height = 860,
                    Content = CreateMainView(),
                };
                break;
            case ISingleViewApplicationLifetime single:
                single.MainView = CreateMainView();
                break;
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The main view bound to the shared root ViewModel. If the ViewModel or the view cannot be created (for example the
    /// engine failed to start), the failure is reported and a plain error view is shown instead of crashing the
    /// activity: on Android the crash screen with the full report appears on the next launch.
    /// </summary>
    /// <remarks>
    /// While the platform is still composing the engine (<see cref="AppBootstrap.EngineReady"/>), this returns a
    /// <see cref="StartingView"/> at once and swaps the main view (or the error view) into it on the UI thread when the
    /// composition completes, so the first frame never waits for the engine.
    /// </remarks>
    public static Control CreateMainView()
    {
        Task<bool>? ready;
        try
        {
            ready = AppBootstrap.EngineReady?.Invoke();
        }
        catch (Exception e)
        {
            AppBootstrap.ReportError?.Invoke("starting the engine", e);
            ready = null;
        }
        if (ready is null || ready.IsCompleted)
            return CreateShellView();

        StartingView starting = new();
        ready.ContinueWith(_ => Dispatcher.UIThread.Post(() => starting.Content = CreateShellView()),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return starting;
    }

    /// <summary>The main view on the root ViewModel, or the error view when it cannot be created.</summary>
    private static Control CreateShellView()
    {
        try
        {
            Control view = new MainView { DataContext = AppBootstrap.Shell };
            AppBootstrap.MainViewCreated?.Invoke();
            return view;
        }
        catch (Exception e)
        {
            AppBootstrap.ReportError?.Invoke("creating the main view", e);
            return ErrorView(e);
        }
    }

    public static Control ErrorView(Exception e) => new ScrollViewer
    {
        Content = new TextBlock
        {
            Margin = new Thickness(16, 48, 16, 16),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = "VibeSkua could not open its main screen.\n\n"
                + DiagnosticText.Scrub($"{e.GetType().Name}: {e.Message}")
                + "\n\nClose the app and open it again to see the full crash report, which you can share from there.",
        },
    };
}
