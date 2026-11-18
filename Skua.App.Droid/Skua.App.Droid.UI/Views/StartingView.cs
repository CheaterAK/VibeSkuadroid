using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skua.App.Droid.UI.Views;

/// <summary>
/// Shown for the moment the engine is still being composed at startup (see <see cref="AppBootstrap.EngineReady"/>);
/// its <see cref="ContentControl.Content"/> is replaced by the main view (or the error view) when that completes.
/// Code only, no bindings: it must work before any ViewModel exists.
/// </summary>
public sealed class StartingView : ContentControl
{
    public StartingView()
    {
        Background = Brush(Application.Current, "BgDarkBrush", Colors.Black);
        Content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "VibeSkua",
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = Brush(Application.Current, "TextMainBrush", Colors.WhiteSmoke),
                },
                new ProgressBar { IsIndeterminate = true, Width = 160, Foreground = Brush(Application.Current, "PrimaryBrush", Colors.MediumSeaGreen) },
                new TextBlock
                {
                    Text = "Starting the engine...",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = Brush(Application.Current, "TextMutedBrush", Colors.Gray),
                },
            },
        };
    }

    /// <summary>The view replaced this placeholder's content (the engine composition completed).</summary>
    public bool IsStarting => Content is StackPanel;

    private static IBrush Brush(Application? app, string key, Color fallback) =>
        app is not null && app.TryGetResource(key, app.ActualThemeVariant, out object? value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);
}
