using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Skua.App.Droid.Core.ViewModels;

namespace Skua.App.Droid.UI.Views;

public partial class ConsoleView : UserControl
{
    private ConsoleViewModel? _vm;

    public ConsoleView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
            _vm.LinesAppended -= OnLinesAppended;
        _vm = DataContext as ConsoleViewModel;
        if (_vm is not null)
            _vm.LinesAppended += OnLinesAppended;
        base.OnDataContextChanged(e);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_vm is not null)
            _vm.LinesAppended -= OnLinesAppended;
        _vm = null;
        base.OnDetachedFromVisualTree(e);
    }

    // Follow the tail of whichever list is showing.
    private void OnLinesAppended(object? sender, EventArgs e)
    {
        if (_vm is null)
            return;
        ListBox? list = this.FindControl<ListBox>(_vm.IsPacketsTab ? "PacketList" : "LogList");
        int count = _vm.IsPacketsTab ? _vm.PacketLines.Count : _vm.LogLines.Count;
        if (list is not null && count > 0)
            list.ScrollIntoView(count - 1);
    }
}
