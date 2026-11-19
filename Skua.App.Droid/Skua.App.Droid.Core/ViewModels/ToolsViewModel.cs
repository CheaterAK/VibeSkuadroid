using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>An entry of the Tools list: what it is and how to open it.</summary>
public sealed record ToolEntry(string Title, string Description, Action Open);

/// <summary>A page opened from the Tools list.</summary>
public interface IToolPage
{
    /// <summary>Shown in the page header.</summary>
    string Title { get; }

    /// <summary>Called each time the page is opened.</summary>
    void Activate();

    /// <summary>Back inside the page (closes an editor, a sub-page); false when at its top.</summary>
    bool GoBack();
}

/// <summary>
/// The Tools hub (opened from Settings): pages that are not a tab of their own, ports of the desktop's tool windows.
/// </summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    public ToolsViewModel(AppServices services)
    {
        AdvancedSkills = new AdvancedSkillsViewModel(services);
        Entries =
        [
            new ToolEntry("Advanced Skills",
                "Class skill rotations scripts use: browse, edit, create and delete them (skill order, HP/MP/aura rules, wait or skip).",
                () => Show(AdvancedSkills)),
        ];
    }

    public ObservableCollection<ToolEntry> Entries { get; }

    public AdvancedSkillsViewModel AdvancedSkills { get; }

    /// <summary>The open tool page, or null for the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListVisible), nameof(Title))]
    private object? _currentPage;

    public bool IsListVisible => CurrentPage is null;

    public string Title => (CurrentPage as IToolPage)?.Title ?? "Tools";

    [RelayCommand]
    private void Open(ToolEntry? entry) => entry?.Open();

    [RelayCommand]
    private void Close() => CurrentPage = null;

    /// <summary>The header's back button: one level up inside the page, or back to the list.</summary>
    [RelayCommand]
    private void Back() => GoBack();

    partial void OnCurrentPageChanged(object? oldValue, object? newValue)
    {
        if (oldValue is INotifyPropertyChanged before)
            before.PropertyChanged -= OnPagePropertyChanged;
        if (newValue is INotifyPropertyChanged after)
            after.PropertyChanged += OnPagePropertyChanged;
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IToolPage.Title))
            OnPropertyChanged(nameof(Title));
    }

    /// <summary>Opens <paramref name="page"/>.</summary>
    public void Show(object page)
    {
        CurrentPage = page;
        (page as IToolPage)?.Activate();
    }

    /// <summary>Back: inside the open page first, then to the list; false when the list is shown.</summary>
    public bool GoBack()
    {
        if (CurrentPage is IToolPage page && page.GoBack())
            return true;
        if (CurrentPage is null)
            return false;
        CurrentPage = null;
        return true;
    }

    internal void Apply(EngineSnapshot s)
    {
    }
}
