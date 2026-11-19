using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Skua.App.Droid.UI.Views;

public partial class DailyTrackerView : UserControl
{
    public DailyTrackerView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
