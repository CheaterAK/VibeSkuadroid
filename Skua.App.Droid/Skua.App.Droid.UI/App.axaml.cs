using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

    private static Control CreateMainView() => new MainView { DataContext = AppBootstrap.Shell };
}
