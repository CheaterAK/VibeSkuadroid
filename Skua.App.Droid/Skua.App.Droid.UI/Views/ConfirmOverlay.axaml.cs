using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Skua.App.Droid.UI.Views;

public partial class ConfirmOverlay : UserControl
{
    public ConfirmOverlay()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
