using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.UI.Views;
using Skua.App.Droid.Tests;

[assembly: AvaloniaTestApplication(typeof(HeadlessUiApp))]

namespace Skua.App.Droid.Tests;

public static class HeadlessUiApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Skua.App.Droid.UI.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>Collects binding and XAML warnings/errors Avalonia reports at run time.</summary>
internal sealed class CollectingSink : ILogSink
{
    public List<string> Messages { get; } = new();

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Add(level, area, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        Add(level, area, messageTemplate, propertyValues);

    private void Add(LogEventLevel level, string area, string template, object?[] values)
    {
        lock (Messages)
            Messages.Add($"{level} {area}: {template} [{string.Join(", ", values)}]");
    }
}

/// <summary>Every page rendered in a headless window, offline and online, with no runtime binding errors.</summary>
public class HeadlessUiTests
{
    [AvaloniaFact]
    public async Task EveryPageRendersWithoutBindingErrors()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        await using TestApp app = new(scripts: new PluginOnlyScriptHost(new FakePluginBackends()));
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths));
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();

        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        foreach (string page in new[] { "Home", "Scripts", "Control", "World", "Console", "Plugins", "Settings" })
        {
            shell.NavigateCommand.Execute(page);
            Render();
            Assert.NotNull(window.GetVisualDescendantOfType(shell.CurrentContent.GetType().Name.Replace("ViewModel", "View")));
        }

        // Online: dashboard, world lists, console lines.
        shell.NavigateCommand.Execute("Home");
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        for (int i = 0; i < 200 && !shell.ShowDashboard; i++)
        {
            await Task.Delay(10);
            Render();
        }
        Assert.True(shell.ShowDashboard);
        Render();
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(DashboardView)));
        foreach (string page in new[] { "Control", "World", "Console", "Scripts", "Plugins", "Settings", "Home" })
        {
            shell.NavigateCommand.Execute(page);
            Render();
        }
        // Each plugin page, online; back returns to the plugin list, then home.
        shell.NavigateCommand.Execute("Plugins");
        foreach (PluginEntry entry in shell.Plugins.Entries.Where(e => e.CanOpen))
        {
            shell.Plugins.OpenCommand.Execute(entry.Id);
            Render();
            Assert.NotNull(shell.Plugins.CurrentPage);
            Assert.NotNull(window.GetVisualDescendantOfType(shell.Plugins.CurrentPage!.GetType().Name.Replace("ViewModel", "View")));
            Assert.Equal(shell.Plugins.Title, shell.Title);
            await shell.Plugins.DailyTracker.RefreshCoreAsync();
            Render();
            Assert.True(shell.GoBack());
            Assert.True(shell.Plugins.IsListVisible);
            Render();
        }
        Assert.True(shell.GoBack());
        Assert.Equal(AppPage.Home, shell.CurrentPage);
        // The options editor with every kind of option, and the CoreBots settings editor.
        File.WriteAllText(Path.Combine(app.Paths.ScriptsDir, "CoreBots.cs"), "class CoreBots { void R() { CBOBool(\"PrivateRooms\", out bool a); CBOInt(\"ActionDelay\", out int b); CBOString(\"SoloClassSelect\", out string c); } }");
        shell.NavigateCommand.Execute("Scripts");
        shell.Scripts.EditCoreBotsOptionsCommand.NotifyCanExecuteChanged();
        shell.Scripts.EditCoreBotsOptionsCommand.Execute(null);
        Render();
        Assert.True(shell.Scripts.IsEditingOptions, shell.Scripts.Message);
        Assert.Equal(3, shell.Scripts.OptionsEditor!.Items.Count);
        shell.Scripts.OptionsEditor = new ScriptOptionsViewModel(new Skua.App.Droid.Core.Engine.ScriptOptionSet("/x/S.cs", "S",
        [
            new("Options", "a", "A", "toggle", Skua.App.Droid.Core.Engine.ScriptOptionKind.Toggle, "True", [], false),
            new("Group_One", "b", "B", "number", Skua.App.Droid.Core.Engine.ScriptOptionKind.Number, "3", [], false),
            new("Options", "c", "C", "", Skua.App.Droid.Core.Engine.ScriptOptionKind.Text, "x", [], true),
            new("Options", "d", "D", "choice", Skua.App.Droid.Core.Engine.ScriptOptionKind.Choice, "Two", ["One", "Two"], false),
        ]), "S.cs");
        Render();
        Assert.True(shell.GoBack()); // back closes the editor first
        Assert.False(shell.Scripts.IsEditingOptions);
        Render();
        shell.World.SelectTabCommand.Execute("Quests");
        shell.Console.ShowPacketsCommand.Execute(null);
        Render();

        window.Close();
        List<string> problems;
        lock (sink.Messages)
            problems = sink.Messages.Where(m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase) || m.Contains("Error", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// The Control page with a whole little world (tiles, pads, monsters, players, drops, skills), every action tab and
    /// the confirmation with a quantity, rendered at phone size with no binding errors; a tile tapped through the UI
    /// moves the character there.
    /// </summary>
    [AvaloniaFact]
    public async Task TheControlPageRendersItsMapCellAndActions()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        FakePluginBackends plugins = new();
        await using TestApp app = new(scripts: new PluginOnlyScriptHost(plugins));
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths));
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();

        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        shell.NavigateCommand.Execute("Control");
        ControlViewModel page = shell.Control;
        for (int i = 0; i < 300 && page.Tiles.Count == 0; i++)
        {
            await Task.Delay(10);
            Render();
        }
        Render();
        Control view = Assert.IsAssignableFrom<Control>(window.GetVisualDescendantOfType(nameof(ControlView)));
        List<Button> tiles = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Button>().Where(b => b.Classes.Contains("tile")).ToList();
        Assert.Equal(3, tiles.Count);
        Assert.Contains("current", tiles[0].Classes);
        Assert.Contains("monsters", tiles[1].Classes);
        Assert.Equal(2, page.Monsters.Count);

        // The r2 tile's bound command and parameter go through the page to the backend.
        Assert.Equal("r2", tiles[1].CommandParameter);
        tiles[1].Command!.Execute(tiles[1].CommandParameter);
        await page.LastAction;
        Assert.Equal("jump r2 -", plugins.FakeControl.Calls[^1]);

        foreach (string tab in new[] { "Shop", "Quest", "Bank", "Travel" })
        {
            page.SelectTabCommand.Execute(tab);
            if (tab == "Quest")
            {
                page.QuestIdInput = "1000";
                page.LoadQuestCommand.Execute(null);
                await page.LastAction;
            }
            await page.RefreshNowAsync();
            Render();
        }
        Assert.NotNull(page.Quest);

        // The confirmation, with its quantity box, over the page.
        page.SelectTabCommand.Execute("Shop");
        await page.RefreshNowAsync();
        page.BuyCommand.Execute(page.ShopItems[0].Item);
        for (int i = 0; i < 100 && !page.IsConfirming; i++)
        {
            await Task.Delay(10);
            Render();
        }
        Render();
        NumericUpDown quantity = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<NumericUpDown>().Single();
        Assert.True(quantity.IsEffectivelyVisible);
        Assert.True(shell.GoBack()); // back cancels it
        await page.LastAction;
        Render();
        Assert.False(page.IsConfirming);

        // The World lists open from the page and Back returns to it.
        page.OpenListsCommand.Execute(null);
        Render();
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(WorldView)));
        Assert.True(shell.GoBack());
        Render();
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(ControlView)));

        window.Close();
        List<string> problems;
        lock (sink.Messages)
            problems = sink.Messages.Where(m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase) || m.Contains("Error", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [AvaloniaFact]
    public async Task ARecreatedViewShowsTheSameState()
    {
        await using TestApp app = new();
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths));
        shell.NavigateCommand.Execute("Settings");
        Window first = new() { Content = new MainView { DataContext = shell } };
        first.Show();
        first.Close(); // the activity goes away

        Window second = new() { Content = new MainView { DataContext = shell } };
        second.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(second.GetVisualDescendantOfType(nameof(SettingsView)));
        second.Close();
    }

    [AvaloniaFact]
    public void AMainViewThatCannotBeCreatedFallsBackToAnErrorViewAndIsReported()
    {
        Func<AppServices>? factory = Skua.App.Droid.UI.AppBootstrap.Factory;
        Action<string, Exception>? report = Skua.App.Droid.UI.AppBootstrap.ReportError;
        List<string> reported = new();
        try
        {
            Skua.App.Droid.UI.AppBootstrap.Reset();
            Skua.App.Droid.UI.AppBootstrap.Factory = () => throw new InvalidOperationException("engine unavailable, password=hunter22");
            Skua.App.Droid.UI.AppBootstrap.ReportError = (where, e) => reported.Add($"{where}: {e.Message}");

            Control view = Skua.App.Droid.UI.App.CreateMainView();

            Assert.IsNotType<MainView>(view);
            Window window = new() { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextBlock text = Assert.IsType<TextBlock>(((ScrollViewer)view).Content);
            Assert.Contains("engine unavailable", text.Text);
            Assert.DoesNotContain("hunter22", text.Text);
            window.Close();
            Assert.Single(reported);
        }
        finally
        {
            Skua.App.Droid.UI.AppBootstrap.Factory = factory;
            Skua.App.Droid.UI.AppBootstrap.ReportError = report;
            Skua.App.Droid.UI.AppBootstrap.Reset();
        }
    }
}

internal sealed class ImmediateUiDispatcher : Skua.App.Droid.Core.Platform.IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

internal static class VisualSearch
{
    public static Control? GetVisualDescendantOfType(this Visual root, string typeName)
    {
        foreach (Visual v in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root))
        {
            if (v.GetType().Name == typeName)
                return v as Control;
        }
        return null;
    }
}
