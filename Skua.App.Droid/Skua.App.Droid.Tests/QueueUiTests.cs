using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Scheduler;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.UI.Views;

namespace Skua.App.Droid.Tests;

/// <summary>The Scripts page's Queue tab, the entry editor, its profiles and the paused state, rendered headless.</summary>
public class QueueUiTests
{
    [AvaloniaFact]
    public async Task TheQueueTabAndPausedStateRenderWithoutBindingErrors()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        QueueScriptHost scripts = new();
        scripts.Scripts["Endless.cs"] = new FakeScript(Forever: true);
        await using TestApp app = new(scripts: scripts, withQueue: true);
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths));
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();

        async Task Render(Func<bool>? until = null)
        {
            for (int i = 0; i < 300; i++)
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (until is null || until())
                    return;
                await Task.Delay(10);
            }
            Assert.Fail("The UI did not reach the expected state.");
        }

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        string endless = QueueTesting.Script(app, "Endless.cs");
        QueueTesting.Script(app, "Next.cs");
        shell.NavigateCommand.Execute("Scripts");
        await Render();
        ScriptsViewModel page = shell.Scripts;
        Assert.True(page.HasQueue);

        // Library: select a script and add it twice.
        page.Refresh();
        page.OpenCommand.Execute(page.Entries.First(e => e.Name == "Endless.cs"));
        Assert.True(page.AddToQueueCommand.CanExecute(null));
        page.AddToQueueCommand.Execute(null);
        page.OpenCommand.Execute(page.Entries.First(e => e.Name == "Next.cs"));
        page.AddToQueueCommand.Execute(null);
        page.SelectTabCommand.Execute("Queue");
        QueueViewModel queue = page.Queue!;
        await Render(() => queue.Items.Count == 2);
        Assert.False(page.IsBrowsing);
        Assert.True(page.IsQueueVisible);
        Assert.Equal("Queue (2)", queue.TabTitle);

        // Entry editor, its script options profile (not offered by the fake host) and CoreBots profile.
        queue.SelectedItem = queue.Items[0];
        queue.EditCommand.Execute(null);
        await Render(() => queue.IsEditing);
        QueueEntryEditorViewModel editor = queue.Editor!;
        editor.Name = "Forever";
        editor.Runs = "x";
        queue.SaveEntryCommand.Execute(null);
        Assert.NotNull(editor.Error);
        editor.Runs = "2";
        editor.OnError = QueueEntryEditorViewModel.ErrorChoices[2];
        editor.Retries = "3";
        editor.StopAfterMinutes = "90";
        await Render();
        File.WriteAllText(Path.Combine(app.Paths.ScriptsDir, "CoreBots.cs"), "class CoreBots { void R() { CBOBool(\"PrivateRooms\", out bool a); } }");
        queue.EditCoreBotsOptionsCommand.Execute(null);
        await Render(() => page.IsEditingOptions);
        Assert.True(page.OptionsEditor!.IsCoreBots);
        Assert.Contains("queue entry", page.OptionsEditor.StorageText);
        page.OptionsEditor.Items[0].BoolValue = true;
        await page.SaveOptionsCommand.ExecuteAsync(null);
        await Render(() => !page.IsEditingOptions);
        Assert.True(editor.HasCoreBotsProfile);
        queue.SaveEntryCommand.Execute(null);
        await Render(() => queue.IsListing);
        QueueEntry saved = app.Host.Scheduler!.Entries[0];
        Assert.Equal("Forever", saved.Name);
        Assert.Equal(2, saved.Runs);
        Assert.Equal(QueueErrorAction.Retry, saved.OnError);
        Assert.Equal(3, saved.MaxRetries);
        Assert.Equal(TimeSpan.FromMinutes(90), saved.StopAfter);
        Assert.Equal("True", Assert.Single(saved.CoreBots!).Value);
        Assert.Equal(endless, app.Host.Scheduler.FullPath(saved));

        // Playlists.
        queue.PlaylistName = "Evening";
        queue.SavePlaylistCommand.Execute(null);
        Assert.Equal(["Evening"], queue.Playlists);

        // Running: status card, the current entry, Start disabled in the library; paused everywhere.
        queue.StartCommand.Execute(null);
        await Render(() => queue.IsRunning && queue.CurrentName == "Forever" && app.Engine.Snapshot.Script.State == ScriptRunState.Running);
        Assert.StartsWith("Entry 1 of 2 · run 1/2", queue.ProgressText);
        Assert.Equal("Next: Next.cs", queue.NextText);
        Assert.False(queue.ClearCommand.CanExecute(null));
        await app.Engine.PauseScriptAsync();
        await Render(() => page.IsPaused && page.ScriptStatus.StartsWith("Paused Endless", StringComparison.Ordinal) && queue.Items[0].Status == "Paused");
        Assert.True(page.ResumeCommand.CanExecute(null));
        Assert.False(page.PauseCommand.CanExecute(null));
        Assert.False(page.StartCommand.CanExecute(null));
        shell.NavigateCommand.Execute("Home");
        await Render(() => shell.Dashboard.IsPaused);
        Assert.StartsWith("Paused Endless", shell.Dashboard.ScriptState);
        Assert.Equal("Queue 1/2: Forever (run 1/2)", shell.Dashboard.QueueLine);
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(DashboardView)));
        await shell.Dashboard.ResumeScriptCommand.ExecuteAsync(null);
        await Render(() => shell.Dashboard.CanPause);

        // Stop the queue from its tab; back closes the entry editor, then leaves the page.
        shell.NavigateCommand.Execute("Scripts");
        await queue.StopCommand.ExecuteAsync(null);
        await Render(() => !queue.IsRunning);
        Assert.Equal("Stopped", queue.Items[0].Status);
        queue.SelectedItem = queue.Items[1];
        queue.EditCommand.Execute(null);
        await Render(() => queue.IsEditing);
        Assert.True(shell.GoBack());
        Assert.False(queue.IsEditing);
        Assert.True(shell.GoBack());
        Assert.Equal(AppPage.Home, shell.CurrentPage);
        await Render();

        window.Close();
        List<string> problems;
        lock (sink.Messages)
            problems = sink.Messages.Where(m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase) || m.Contains("Error", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
