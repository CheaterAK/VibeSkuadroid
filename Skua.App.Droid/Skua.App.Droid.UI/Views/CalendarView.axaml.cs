using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Skua.App.Droid.UI.Views;

public partial class CalendarView : UserControl
{
    public CalendarView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
