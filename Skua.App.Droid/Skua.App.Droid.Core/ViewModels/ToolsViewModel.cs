using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>One entry of the Tools list: what it is and what opening it does.</summary>
public sealed record ToolEntry(string Title, string Description, Action Open);

/// <summary>
/// The Tools hub, opened from the Settings page: a list of tool pages (<see cref="Entries"/>), one of which may be
/// open (<see cref="CurrentPage"/>, a ViewModel the view maps to its view).
/// </summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    public ObservableCollection<ToolEntry> Entries { get; } = new();

    /// <summary>The open tool page, or null for the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListVisible))]
    private object? _currentPage;

    [ObservableProperty]
    private string _currentTitle = "Tools";

    public bool IsListVisible => CurrentPage is null;

    /// <summary>Shows a tool page (called by an entry's <see cref="ToolEntry.Open"/>).</summary>
    public void Show(object page, string title)
    {
        CurrentTitle = title;
        CurrentPage = page;
    }

    [RelayCommand]
    private void Open(ToolEntry? entry) => entry?.Open();

    [RelayCommand]
    private void Close() => CloseSubPage();

    /// <summary>Back: closes the open tool page; false when the list is shown.</summary>
    public bool CloseSubPage()
    {
        if (CurrentPage is null)
            return false;
        CurrentPage = null;
        CurrentTitle = "Tools";
        return true;
    }
}
