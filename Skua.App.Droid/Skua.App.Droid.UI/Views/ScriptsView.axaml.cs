using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Core.ViewModels;

namespace Skua.App.Droid.UI.Views;

public partial class ScriptsView : UserControl
{
    public ScriptsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // One tap opens folders and selects scripts (phone UX: no double tap needed).
    private void OnEntryTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ScriptsViewModel vm && (e.Source as Control)?.DataContext is ScriptEntry entry)
            vm.OpenCommand.Execute(entry);
    }
}
