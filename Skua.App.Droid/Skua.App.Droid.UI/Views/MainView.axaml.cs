using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.UI.Services;

namespace Skua.App.Droid.UI.Views;

/// <summary>
/// The shell view. A new instance is created for every Android activity; it only reads from the shared
/// <see cref="ShellViewModel"/>, so recreating it loses nothing.
/// </summary>
public partial class MainView : UserControl
{
    private IInsetsManager? _insets;
    private TopLevel? _topLevel;

    public MainView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        AvaloniaFilePicker.CurrentTopLevel = _topLevel;
        if (_topLevel is not null)
            _topLevel.BackRequested += OnBackRequested;

        // Android 15+ draws apps edge to edge: keep content out from under the status and navigation bars.
        _insets = _topLevel?.InsetsManager;
        if (_insets is not null)
        {
            _insets.SafeAreaChanged += OnSafeAreaChanged;
            ApplySafeArea(_insets.SafeAreaPadding);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insets is not null)
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
        if (_topLevel is not null)
            _topLevel.BackRequested -= OnBackRequested;
        if (ReferenceEquals(AvaloniaFilePicker.CurrentTopLevel, _topLevel))
            AvaloniaFilePicker.CurrentTopLevel = null;
        _insets = null;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => ApplySafeArea(e.SafeAreaPadding);

    private void ApplySafeArea(Thickness padding) => Padding = padding;

    // Android back: up one level / back to Home; at Home the event stays unhandled and the activity goes to the
    // background (the engine keeps running in the foreground service).
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell && shell.GoBack())
            e.Handled = true;
    }
}
