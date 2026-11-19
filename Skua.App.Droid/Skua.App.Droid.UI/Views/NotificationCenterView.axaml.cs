using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Skua.App.Droid.UI.Views;

public partial class NotificationCenterView : UserControl
{
    public NotificationCenterView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
