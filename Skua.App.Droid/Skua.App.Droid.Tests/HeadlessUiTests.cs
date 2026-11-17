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
        await using TestApp app = new();
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths));
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();

        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        foreach (string page in new[] { "Home", "Scripts", "World", "Console", "Settings" })
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
        foreach (string page in new[] { "World", "Console", "Scripts", "Settings", "Home" })
        {
            shell.NavigateCommand.Execute(page);
            Render();
        }
        shell.World.SelectTabCommand.Execute("Quests");
        shell.Console.ShowPacketsCommand.Execute(null);
        Render();

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
