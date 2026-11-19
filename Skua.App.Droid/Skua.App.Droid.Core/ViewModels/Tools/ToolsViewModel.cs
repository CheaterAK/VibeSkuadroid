using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Plugins;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>One entry of the Tools hub.</summary>
/// <param name="Title">What the entry is called.</param>
/// <param name="Description">One or two lines on what it does.</param>
/// <param name="Open">Opens its page.</param>
public sealed record ToolEntry(string Title, string Description, ICommand Open);

/// <summary>
/// The Tools hub, opened from Settings: a list of tool pages (each a <see cref="ToolEntry"/>) and the open page.
/// </summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    public ToolsViewModel(AppServices services)
    {
        // Tool options (no secrets) next to the plugins' file, in their format.
        Store = new PluginStore(Path.Combine(Path.GetDirectoryName(services.Paths.PluginsFile) ?? ".", "tools.json"));
        FastTravel = new FastTravelViewModel(services, Store);
        Loader = new LoaderViewModel(services);
        Entries =
        [
            new ToolEntry("Fast Travel", "Saved map destinations (map, cell, pad) and Skua's presets: one tap to travel, in your private room if you like.",
                new RelayCommand(() => OpenPage(FastTravel))),
            new ToolEntry("Loader", "Load a shop or quests by ID; Skua's quest list with search, copy names and IDs, load and fake complete.",
                new RelayCommand(() => OpenPage(Loader))),
        ];
    }

    /// <summary>The tools' saved options (app-private <c>tools.json</c>).</summary>
    public PluginStore Store { get; }

    public ObservableCollection<ToolEntry> Entries { get; }

    public FastTravelViewModel FastTravel { get; }

    public LoaderViewModel Loader { get; }

    /// <summary>The hub is shown (instead of the settings).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool _isOpen;

    /// <summary>The open tool page, or null for the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListVisible), nameof(Title), nameof(BackText))]
    private object? _currentPage;

    public bool IsListVisible => CurrentPage is null;

    /// <summary>The back button's text.</summary>
    public string BackText => CurrentPage is null ? "\u2039 Settings" : "\u2039 Tools";

    public string Title => CurrentPage switch
    {
        FastTravelViewModel => "Fast Travel",
        LoaderViewModel => "Loader",
        _ => "Tools",
    };

    /// <summary>Opens a tool page (the hub opens with it).</summary>
    public void OpenPage(object page)
    {
        IsOpen = true;
        CurrentPage = page;
    }

    /// <summary>The back button: closes the open tool page, else the hub.</summary>
    [RelayCommand]
    private void Close() => GoBack();

    /// <summary>System back: closes an open question, then the tool page, then the hub. False when the hub was closed.</summary>
    public bool GoBack()
    {
        if (ConfirmOf(CurrentPage)?.CancelIfOpen() == true)
            return true;
        if (CurrentPage is not null)
        {
            CurrentPage = null;
            return true;
        }
        if (IsOpen)
        {
            IsOpen = false;
            return true;
        }
        return false;
    }

    private static ConfirmHost? ConfirmOf(object? page) => page switch
    {
        FastTravelViewModel ft => ft.Confirm,
        LoaderViewModel l => l.Confirm,
        _ => null,
    };

    partial void OnCurrentPageChanged(object? oldValue, object? newValue)
    {
        ConfirmOf(oldValue)?.CancelIfOpen();
        if (newValue is FastTravelViewModel ft)
            ft.Refresh();
        else if (newValue is LoaderViewModel loader)
            loader.Refresh();
    }

    internal void Apply(EngineSnapshot s)
    {
        FastTravel.Apply(s);
        Loader.Apply(s);
    }
}
