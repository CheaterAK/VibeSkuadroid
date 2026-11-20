using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Skua.App.Droid.UI.Views;

public partial class LoaderView : UserControl
{
    public LoaderView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
