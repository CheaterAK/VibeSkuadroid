using Skua.App.Droid.Core.Plugins;
using Skua.App.Droid.Core.ViewModels;

namespace Skua.App.Droid.Tests;

/// <summary>The Plugins page: the built-in plugins, their on/off switches (saved) and their pages.</summary>
public class PluginListTests
{
    [Fact]
    public async Task SwitchingAPluginOffIsSavedClosesItsPageAndStopsItsWork()
    {
        FakePluginBackends plugins = new();
        await using TestApp app = new(scripts: new PluginOnlyScriptHost(plugins));
        using ShellViewModel shell = new(app.Services);
        PluginsViewModel list = shell.Plugins;
        Assert.Equal([PluginsViewModel.DailyTrackerId, PluginsViewModel.ButlerId], list.Entries.Where(e => e.IsAvailable).Select(e => e.Id));
        Assert.All(list.Entries.Where(e => e.IsAvailable), e => Assert.True(e.IsEnabled)); // on by default
        PluginEntry cosmetics = list.Entries.Single(e => e.Id == PluginsViewModel.CosmeticsId);
        Assert.False(cosmetics.IsAvailable);
        Assert.False(cosmetics.HasPage);
        Assert.Equal("Not available", cosmetics.StateText);
        Assert.Contains("never asks the server", cosmetics.Note);
        cosmetics.IsEnabled = true;
        Assert.False(cosmetics.IsEnabled);

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        await Sandbox.Until(() => shell.IsOnline);

        shell.NavigateCommand.Execute("Plugins");
        PluginEntry butler = list.Entries.Single(e => e.Id == PluginsViewModel.ButlerId);
        butler.OpenCommand.Execute(null);
        Assert.Same(list.Butler, list.CurrentPage);
        Assert.Equal("ButlerV5", shell.Title);
        list.Butler.PlayerName = "Alpha";
        list.Butler.FollowCommand.Execute(null);
        Assert.True(list.Butler.IsFollowing);

        // Off: saved, page closed, the follow stopped, nothing can start.
        butler.IsEnabled = false;
        Assert.Null(list.CurrentPage);
        Assert.False(butler.CanOpen);
        Assert.False(butler.OpenCommand.CanExecute(null));
        Assert.False(list.Store.IsEnabled(PluginsViewModel.ButlerId));
        await Sandbox.Until(() => plugins.FakeButler.Calls.Contains("stop"));
        Assert.False(list.Butler.FollowCommand.CanExecute(null));

        // The tracker off: no reads, turn-ins ignored.
        PluginEntry tracker = list.Entries.Single(e => e.Id == PluginsViewModel.DailyTrackerId);
        tracker.IsEnabled = false;
        int reads = plugins.FakeDailyTracker.Reads;
        await list.DailyTracker.RefreshCoreAsync();
        plugins.FakeDailyTracker.TurnIn(1);
        await Task.Delay(100);
        Assert.Equal(reads, plugins.FakeDailyTracker.Reads);
        Assert.Null(list.DailyTracker.Groups[0].Quests[0].IsDone);
        Assert.False(list.DailyTracker.RefreshCommand.CanExecute(null));

        // The next start reads the switches back; switching on again works.
        using ShellViewModel again = new(app.Services);
        Assert.False(again.Plugins.Entries.Single(e => e.Id == PluginsViewModel.ButlerId).IsEnabled);
        Assert.False(again.Plugins.Entries.Single(e => e.Id == PluginsViewModel.DailyTrackerId).IsEnabled);
        again.Plugins.Entries.Single(e => e.Id == PluginsViewModel.DailyTrackerId).IsEnabled = true;
        Assert.True(new PluginStore(app.Paths.PluginsFile).IsEnabled(PluginsViewModel.DailyTrackerId));
        Assert.True(again.Plugins.DailyTracker.IsEnabled);

        // Back on the list: back leaves the tab.
        Assert.True(shell.GoBack());
        Assert.Equal(AppPage.Home, shell.CurrentPage);
    }

    [Fact]
    public async Task WithoutAPluginHostNothingCanBeSwitchedOn()
    {
        await using TestApp app = new();
        using ShellViewModel shell = new(app.Services);
        Assert.All(shell.Plugins.Entries, e =>
        {
            Assert.False(e.IsAvailable);
            Assert.False(e.IsEnabled);
            Assert.False(e.CanOpen);
            Assert.Equal("Not available", e.StateText);
            e.IsEnabled = true;
            Assert.False(e.IsEnabled);
        });
    }
}
