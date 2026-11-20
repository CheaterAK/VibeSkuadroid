using System.ComponentModel;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Plugins;
using Skua.App.Droid.Core.Tools;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// The game tool pages of the Tools hub: Fast Travel, Loader and Grabber (ports of the desktop windows). Created once
/// with the hub (the shell outlives activities). A page is refreshed when it is shown; an open question of the page
/// being left is cancelled.
/// </summary>
public sealed class GameToolPages
{
    private readonly ToolsViewModel _tools;
    private object? _shown;

    private GameToolPages(ToolsViewModel tools, AppServices services)
    {
        _tools = tools;
        // Tool options (no secrets) next to the plugins' file, in their format.
        Store = new PluginStore(Path.Combine(Path.GetDirectoryName(services.Paths.PluginsFile) ?? ".", "tools.json"));
        FastTravel = new FastTravelViewModel(services, Store);
        Loader = new LoaderViewModel(services);
        Grabber = new GrabberViewModel(services);
        Loader.ViewInGrabberRequested += (_, kind) =>
        {
            tools.Show(Grabber, "Grabber");
            _ = Grabber.ShowAsync(kind == LoaderKind.Shop ? GrabberCategory.ShopItems : GrabberCategory.Quests);
        };

        tools.Entries.Add(new ToolEntry("Fast Travel", "Saved map destinations (map, cell, pad) and Skua's presets: one tap to travel, in your private room if you like.",
            () => tools.Show(FastTravel, "Fast Travel")));
        tools.Entries.Add(new ToolEntry("Loader", "Load a shop or quests by ID; Skua's quest list with search, copy names and IDs, load and fake complete.",
            () => tools.Show(Loader, "Loader")));
        tools.Entries.Add(new ToolEntry("Grabber", "Browse what the game holds now (shop items, shops, quests, map items, inventory, bank, monsters): search, copy IDs and names, and the desktop's item actions.",
            () => tools.Show(Grabber, "Grabber")));
        tools.PropertyChanged += OnToolsChanged;
    }

    /// <summary>The tools' saved options (app-private <c>tools.json</c>).</summary>
    public PluginStore Store { get; }

    public FastTravelViewModel FastTravel { get; }

    public LoaderViewModel Loader { get; }

    public GrabberViewModel Grabber { get; }

    /// <summary>Adds the pages to <paramref name="tools"/>.</summary>
    public static GameToolPages AddTo(ToolsViewModel tools, AppServices services) => new(tools, services);

    /// <summary>System back: cancels the open page's question. False when none was open.</summary>
    public bool CancelQuestion() => ConfirmOf(_tools.CurrentPage)?.CancelIfOpen() == true;

    private ConfirmHost? ConfirmOf(object? page) => page switch
    {
        FastTravelViewModel ft => ft.Confirm,
        LoaderViewModel l => l.Confirm,
        GrabberViewModel g => g.Confirm,
        _ => null,
    };

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolsViewModel.CurrentPage))
            return;
        object? page = _tools.CurrentPage;
        if (ReferenceEquals(page, _shown))
            return;
        ConfirmOf(_shown)?.CancelIfOpen();
        _shown = page;
        switch (page)
        {
            case FastTravelViewModel ft:
                ft.Refresh();
                break;
            case LoaderViewModel loader:
                loader.Refresh();
                break;
            case GrabberViewModel grabber:
                grabber.Refresh();
                break;
        }
    }

    internal void Apply(EngineSnapshot s)
    {
        FastTravel.Apply(s);
        Loader.Apply(s);
        Grabber.Apply(s);
    }
}
