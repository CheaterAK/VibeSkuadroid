using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Skua.App.Droid.UI.Views;

public partial class ControlView : UserControl
{
    public ControlView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
