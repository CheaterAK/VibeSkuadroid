using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.GameView;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.UI.Views;

namespace Skua.App.Droid.Tests.GameView;

/// <summary>The Game view card on the Dashboard and the Control page, rendered headless, with its question and options.</summary>
public class GameViewUiTests
{
    [AvaloniaFact]
    public async Task TheCardRendersOnBothPagesAsksWhileAScriptRunsAndOpens()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        QueueScriptHost scripts = new();
        scripts.Scripts["farm.cs"] = new FakeScript(Forever: true);
        await using TestApp app = new(scripts: scripts);
        FakeLauncher launcher = new();
        using GameViewSwitch sw = new(app.Host, launcher, GameViewSwitchTests.Fast);
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths)
        {
            GameView = sw,
        });
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();
        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        for (int i = 0; i < 200 && !shell.ShowDashboard; i++)
        {
            await Task.Delay(10);
            Render();
        }
        Render();
        Assert.True(shell.GameView.IsAvailable);
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(GameViewCard)));

        // Options: shown on demand, saved to the settings at once.
        shell.GameView.ToggleOptionsCommand.Execute(null);
        shell.GameView.FrameRate = "8";
        shell.GameView.KillLag = true;
        Render();
        Assert.Equal(8, app.Settings.Current.GameView!.FrameRate);
        Assert.True(app.Settings.Current.GameView.KillLag);
        Assert.Equal("webgl", app.Settings.Current.GameView.Renderer);

        shell.NavigateCommand.Execute("Control");
        Render();
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(GameViewCard)));

        // A script runs: the question comes first; Cancel leaves everything as it was.
        await app.Engine.StartScriptAsync(QueueTesting.Script(app, "farm.cs"));
        Task open = shell.GameView.OpenCommand.ExecuteAsync(null);
        Render();
        Assert.True(shell.GameView.Confirm.IsOpen);
        Assert.Contains("farm", shell.GameView.Confirm.Text);
        Assert.True(shell.GoBack()); // system back cancels the question
        await open;
        Assert.Empty(launcher.Launched);
        Assert.True(app.Engine.Snapshot.Script.IsBusy);

        open = shell.GameView.OpenCommand.ExecuteAsync(null);
        Render();
        shell.GameView.Confirm.ConfirmCommand.Execute(null);
        await open;
        Render();
        Assert.Single(launcher.Launched);
        Assert.True(shell.GameView.IsBusy);
        Assert.Equal("Game view", shell.ConnectionText);
        shell.NavigateCommand.Execute("Home");
        Render();
        Assert.True(shell.ShowDashboard); // the dashboard stays while the view has the account

        sw.Connect(launcher.Launched[0]);
        sw.ViewClosing(launcher.Launched[0], "closed by the user", true, null);
        await sw.Returning;
        Render();
        Assert.False(shell.GameView.IsBusy);
        Assert.True(shell.GameView.HasRestartOffer);
        Assert.Equal("farm", shell.GameView.RestartScript);
        Assert.Equal(ConnectionPhase.Online, app.Engine.Snapshot.Connection);
        Render();
        Assert.DoesNotContain(sink.Messages, m => m.Contains("Binding", StringComparison.Ordinal) && m.Contains("GameView", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task WithoutAGameViewTheCardIsHidden()
    {
        await using TestApp app = new();
        GameViewViewModel vm = new(app.Services);
        GameViewCard card = new() { DataContext = vm };
        Window window = new() { Content = card };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsAvailable);
        Assert.False(card.IsVisible);
    }
}
