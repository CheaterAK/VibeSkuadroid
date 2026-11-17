using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Skua.App.Droid.UI.Views;

public partial class WorldView : UserControl
{
    public WorldView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
