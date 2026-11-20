using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Skua.App.Droid.UI.Views;

public partial class PacketInterceptorView : UserControl
{
    public PacketInterceptorView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
