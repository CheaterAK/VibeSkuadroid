using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Skua.App.Droid.Core.Skills;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.UI.Views;

namespace Skua.App.Droid.Tests;

/// <summary>The Tools pages rendered headless inside the real shell, with no runtime binding errors.</summary>
public class ToolsUiTests
{
    private static void Render(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Window window, Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
            Render(window);
        }
        Assert.True(condition());
    }

    private static List<string> Problems(CollectingSink sink)
    {
        lock (sink.Messages)
            return sink.Messages.Where(m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase) || m.Contains("Error", StringComparison.Ordinal)).ToList();
    }

    [AvaloniaFact]
    public async Task CoreBotsOptionsPagesRenderOfflineAndOnline()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        await using TestApp app = new(scripts: new ToolsScriptHost(FakeCharacter.Services(out _)));
        File.Copy(AdvancedSkillsTests.Fixture("CoreBots.sample.cs.txt"), Path.Combine(app.Paths.ScriptsDir, "CoreBots.cs"));
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths));
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();
        shell.NavigateCommand.Execute("Settings");
        shell.Settings.OpenToolsCommand.Execute(null);
        ToolsViewModel tools = shell.Settings.Tools;
        CoreBotsOptionsViewModel page = shell.Settings.SkillPages.CoreBots;

        // Logged out: the page says why.
        tools.OpenCommand.Execute(tools.Entries.Single(e => e.Title == "CoreBots options"));
        Render(window);
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(CoreBotsOptionsView)));
        Assert.False(page.IsReady);
        Assert.True(shell.GoBack());

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        await Until(window, () => page.IsReady);
        tools.OpenCommand.Execute(tools.Entries.Single(e => e.Title == "CoreBots options"));
        await Until(window, () => page.Slots.Count == 4);
        Control view = window.GetVisualDescendantOfType(nameof(CoreBotsOptionsView))!;

        // The class of the solo slot chosen in its combo box; its modes follow.
        Render(window);
        view = window.GetVisualDescendantOfType(nameof(CoreBotsOptionsView))!;
        Assert.True(view.GetVisualDescendants().OfType<ComboBox>().Any(), $"no combo boxes; tab {page.Tab}, slots {page.Slots.Count}, {view.GetVisualDescendants().Count()} visuals, {page.Message}");
        ComboBox soloClass = view.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsVisible);
        soloClass.SelectedItem = "Void Highlord";
        Render(window);
        Assert.Equal("Void Highlord", page.Slots[0].ClassName);
        Assert.Equal(["Base", "Solo"], page.Slots[0].ModeChoices);

        foreach (string tab in new[] { "Equipment", "Options", "Other", "Classes" })
        {
            page.SelectTabCommand.Execute(tab);
            Render(window);
            Assert.Equal(tab, page.Tab);
        }
        page.SelectTabCommand.Execute("Equipment");
        Render(window);
        ComboBox helm = view.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && c.ItemsSource is IReadOnlyList<string> l && l.Contains("Sample Helm"));
        helm.SelectedItem = "Sample Helm";
        Render(window);
        Assert.Equal("Sample Helm", page.Slots[0].Helm);

        page.General.Single(o => o.Key == "PrivateRooms").BoolValue = false;
        page.SaveCommand.Execute(null);
        Render(window);
        Assert.True(page.ConfirmPublicRooms);
        Assert.True(shell.GoBack()); // back answers the warning first
        Assert.False(page.ConfirmPublicRooms);
        page.SaveCommand.Execute(null);
        page.KeepPrivateRoomsCommand.Execute(null);
        Render(window);
        Assert.Contains("Saved", page.Message);

        window.Close();
        List<string> problems = Problems(sink);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [AvaloniaFact]
    public async Task AdvancedSkillsPagesRenderAndEditThroughTheControls()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        await using TestApp app = new();
        File.Copy(AdvancedSkillsTests.Fixture("AdvancedSkills.sample.json"), Path.Combine(app.Paths.SkuaDir, SkillSetStore.DefaultsFileName));
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths));
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();

        // Settings -> Tools -> Advanced Skills.
        shell.NavigateCommand.Execute("Settings");
        Render(window);
        shell.Settings.OpenToolsCommand.Execute(null);
        Render(window);
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(ToolsView)));
        ToolsViewModel tools = shell.Settings.Tools;
        tools.OpenCommand.Execute(tools.Entries.Single(e => e.Title == "Advanced Skills"));
        AdvancedSkillsViewModel page = shell.Settings.SkillPages.AdvancedSkills;
        await Until(window, () => page.Classes.Count == 3);
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(AdvancedSkillsView)));

        // A class, its rotations, the editor and the rules page.
        page.OpenClassCommand.Execute(page.Classes.Single(c => c.ClassName == "Sample Tarot Caster"));
        Render(window);
        page.EditCommand.Execute(page.SelectedClass!.Sets[0]);
        Render(window);
        Control editorView = window.GetVisualDescendantOfType(nameof(SkillRotationEditorView))!;
        Assert.NotNull(editorView);
        SkillRotationEditorViewModel editor = page.Editor!;
        Assert.Equal(4, editor.Skills.Count);
        editor.EditRulesCommand.Execute(editor.Skills[1]);
        Render(window);
        SkillRulesEditorViewModel rules = editor.RulesEditor!;
        Assert.True(rules.MultiAura);
        Assert.Equal(2, rules.AuraChecks.Count);

        // Typing into the controls reaches the desktop's rules (numbers through the bindings' conversion).
        TextBox health = editorView.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "70");
        health.Text = "45";
        Render(window);
        Assert.Equal(45, rules.Health);
        rules.AddAuraCheckCommand.Execute(null);
        Render(window);
        TextBox auraName = editorView.GetVisualDescendants().OfType<TextBox>().Last(t => t.IsVisible && t.PlaceholderText == "Aura name");
        auraName.Text = "Gamma";
        TextBox stacks = editorView.GetVisualDescendants().OfType<TextBox>().Last(t => t.IsVisible && t.PlaceholderText == "stacks");
        stacks.Text = "2.5";
        Render(window);
        Assert.Equal("Gamma", rules.AuraChecks[2].AuraName);
        Assert.Equal(2.5f, rules.AuraChecks[2].StackCount);
        rules.DoneCommand.Execute(null);
        Render(window);
        Assert.Contains("4 H>45% MA<\"Mirrored Sample\" 0: MA<\"Arcane Sample\" 0 TARGET: MA>\"Gamma\" 2.5", editor.Preview);
        await editor.SaveCommand.ExecuteAsync(null);
        Render(window);
        Assert.Null(page.Editor);

        // A new rotation renders empty; back closes the editor, the class, the page, then Tools.
        page.NewCommand.Execute(null);
        Render(window);
        Assert.True(shell.GoBack());
        Assert.True(shell.GoBack());
        Render(window);
        Assert.True(page.IsListVisible);
        Assert.True(shell.GoBack());
        Assert.True(tools.IsListVisible);
        Assert.True(shell.GoBack());
        Assert.False(shell.Settings.ShowTools);
        Render(window);
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(SettingsView)));

        window.Close();
        List<string> problems = Problems(sink);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
