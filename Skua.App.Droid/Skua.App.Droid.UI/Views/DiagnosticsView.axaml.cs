using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Skua.App.Droid.UI.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
