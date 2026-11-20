using System.ComponentModel;
using Skua.App.Droid.Core.Engine;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>A tool page with levels of its own (an editor, a class, a question): back inside it, and its title.</summary>
public interface IToolPage
{
    /// <summary>Shown in the hub's header; changes as the page goes deeper.</summary>
    string Title { get; }

    /// <summary>Called each time the page is shown.</summary>
    void Activate();

    /// <summary>Back inside the page (answers a question, closes an editor or a class); false when at its top.</summary>
    bool GoBack();
}

/// <summary>
/// The skill tool pages of the Tools hub: Advanced Skills and CoreBots options (ports of the desktop windows). Created
/// once with the hub. A page is activated (reads its files) each time it is shown; its title follows its level.
/// </summary>
public sealed class SkillToolPages
{
    private readonly ToolsViewModel _tools;
    private object? _shown;

    private SkillToolPages(ToolsViewModel tools, AppServices services)
    {
        _tools = tools;
        AdvancedSkills = new AdvancedSkillsViewModel(services);
        CoreBots = new CoreBotsOptionsViewModel(services);
        tools.Entries.Add(new ToolEntry("Advanced Skills",
            "Class skill rotations scripts use: browse, edit, create and delete them (skill order, HP/MP/aura rules, wait or skip).",
            () => tools.Show(AdvancedSkills, AdvancedSkills.Title)));
        tools.Entries.Add(new ToolEntry("CoreBots options",
            "CoreBots' settings for this account: solo, farm, dodge and boss class with their rotation and gear, private rooms, delays and the other options.",
            () => tools.Show(CoreBots, CoreBots.Title)));
        tools.PropertyChanged += OnToolsChanged;
        AdvancedSkills.PropertyChanged += OnPageChanged;
        CoreBots.PropertyChanged += OnPageChanged;
    }

    public AdvancedSkillsViewModel AdvancedSkills { get; }

    public CoreBotsOptionsViewModel CoreBots { get; }

    /// <summary>Adds the pages to <paramref name="tools"/>.</summary>
    public static SkillToolPages AddTo(ToolsViewModel tools, AppServices services) => new(tools, services);

    /// <summary>System back inside the open page (question, editor, class). False when none of these pages is at a deeper level.</summary>
    public bool GoBack() => _tools.CurrentPage is IToolPage page && (ReferenceEquals(page, AdvancedSkills) || ReferenceEquals(page, CoreBots)) && page.GoBack();

    internal void Apply(EngineSnapshot s) => CoreBots.Apply(s);

    private void OnToolsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolsViewModel.CurrentPage))
            return;
        object? page = _tools.CurrentPage;
        if (ReferenceEquals(page, _shown))
            return;
        // A page that is left starts at its top next time (its questions are cancelled, editors closed).
        if (_shown is IToolPage left && (ReferenceEquals(left, AdvancedSkills) || ReferenceEquals(left, CoreBots)))
        {
            for (int i = 0; i < 8 && left.GoBack(); i++)
            {
            }
        }
        _shown = page;
        if (ReferenceEquals(page, AdvancedSkills) || ReferenceEquals(page, CoreBots))
            ((IToolPage)page!).Activate();
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IToolPage.Title) && sender is IToolPage page && ReferenceEquals(_tools.CurrentPage, page))
            _tools.CurrentTitle = page.Title;
    }
}
