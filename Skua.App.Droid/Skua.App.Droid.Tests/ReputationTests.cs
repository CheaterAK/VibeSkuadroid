using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.Core.World;
using Skua.App.Droid.Scripting;
using Skua.App.Droid.UI.Views;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Interfaces;
using Skua.Core.Models.Factions;

namespace Skua.App.Droid.Tests;

/// <summary>The Reputation tab of the World lists: Skua's faction list, sorted, searchable, throttled, live.</summary>
public class ReputationTests
{
    internal static Faction Faction(int id, string name, int rank, int rep, int required, int total) =>
        new() { ID = id, Name = name, Rank = rank, Rep = rep, RequiredRep = required, TotalRep = total };

    internal static List<Faction> SampleFactions() =>
    [
        Faction(2, "Good", 2, 100, 2700, 1000),
        Faction(3, "Evil", 10, 0, 0, 302500),
        Faction(6, "Chaos", 4, 6400, 12500, 23000),
        Faction(9, "Arcangrove", 2, 2600, 2700, 3500),
    ];

    internal static IServiceProvider Skua(List<Faction> factions) => new ServiceCollection()
        .AddSingleton(SkuaStub.Create<IScriptFaction>(new() { ["FactionList"] = factions }))
        .BuildServiceProvider();

    [Fact]
    public void RowsAreSortedByRankSearchableAndMaxAtRankTen()
    {
        IReadOnlyList<FactionRow> rows = ReputationList.Read(Skua(SampleFactions()))!;
        Assert.Equal(["Evil", "Chaos", "Arcangrove", "Good"], rows.Select(r => r.Name));

        FactionRow evil = rows[0];
        Assert.True(evil.IsMax);
        Assert.Equal(1, evil.Progress);
        Assert.Equal("MAX", evil.ProgressText);
        Assert.Equal("Rank 10 · MAX", evil.RankText);
        Assert.Equal("#3 · total 302,500", evil.TotalText);

        FactionRow chaos = rows[1];
        Assert.False(chaos.IsMax);
        Assert.Equal((6, "Chaos", 4, 6400, 12500, 6100, 23000), (chaos.Id, chaos.Name, chaos.Rank, chaos.Rep, chaos.RequiredRep, chaos.RemainingRep, chaos.TotalRep));
        Assert.Equal(0.512, chaos.Progress, 3);
        Assert.Equal("6,400 / 12,500 (6,100 to rank 5)", chaos.ProgressText);

        Assert.Equal(["Chaos"], ReputationList.Filter(rows, "cha").Select(r => r.Name));
        Assert.Equal(["Good"], ReputationList.Filter(rows, " 2 ").Select(r => r.Name));
        Assert.Same(rows, ReputationList.Filter(rows, ""));
        Assert.Null(ReputationList.Read(null));
        Assert.Null(ReputationList.Read(new ServiceCollection().BuildServiceProvider()));
    }

    [Fact]
    public async Task TheTabReadsOnlyWhileShownAndFollowsChanges()
    {
        List<Faction> factions = SampleFactions();
        await using TestApp app = new(scripts: new ToolsScriptHost(Skua(factions)));
        WorldViewModel world = new(app.Services);
        world.Apply(app.Engine.Snapshot);
        Assert.Equal(0, world.ReputationReads);

        // Shown but offline: says so.
        world.IsActive = true;
        world.SelectTabCommand.Execute("Reputation");
        Assert.True(world.IsReputationTab);
        await Sandbox.Until(() => world.ReputationSummary.Length > 0);
        Assert.Equal("Log in to see your reputation.", world.ReputationSummary);
        Assert.Empty(world.FactionRows);

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        world.Apply(app.Engine.Snapshot);
        await Sandbox.Until(() => world.FactionRows.Count == 4);
        Assert.Equal("4 factions · 1 at rank 10", world.ReputationSummary);
        Assert.Equal("Evil", world.FactionRows[0].Name);

        // Search uses the page's filter.
        world.Filter = "good";
        Assert.Equal(["Good"], world.FactionRows.Select(r => r.Name));
        world.Filter = string.Empty;

        // A change of the world is read again, at most every ReputationInterval.
        factions[0].Rank = 3;
        factions[0].TotalRep = 3600;
        int reads = world.ReputationReads;
        world.Apply(app.Engine.Snapshot with { WorldVersion = app.Engine.Snapshot.WorldVersion + 100 });
        world.Apply(app.Engine.Snapshot with { WorldVersion = app.Engine.Snapshot.WorldVersion + 101 });
        world.Apply(app.Engine.Snapshot with { WorldVersion = app.Engine.Snapshot.WorldVersion + 102 });
        await Sandbox.Until(() => world.FactionRows.Any(r => r.Name == "Good" && r.Rank == 3));
        await Task.Delay(WorldViewModel.ReputationInterval * 2);
        Assert.InRange(world.ReputationReads - reads, 1, 3);

        // Another tab, the page left, or the app in the background: nothing is read.
        reads = world.ReputationReads;
        world.SelectTabCommand.Execute("Inventory");
        world.Apply(app.Engine.Snapshot with { WorldVersion = app.Engine.Snapshot.WorldVersion + 200 });
        world.SelectTabCommand.Execute("Reputation");
        await Sandbox.Until(() => world.ReputationReads > reads);
        await Task.Delay(WorldViewModel.ReputationInterval * 2);
        reads = world.ReputationReads;
        world.SetUiVisible(false);
        world.Apply(app.Engine.Snapshot with { WorldVersion = app.Engine.Snapshot.WorldVersion + 300 });
        world.IsActive = false;
        world.Apply(app.Engine.Snapshot with { WorldVersion = app.Engine.Snapshot.WorldVersion + 400 });
        await Task.Delay(WorldViewModel.ReputationInterval * 2);
        Assert.Equal(reads, world.ReputationReads);
    }

    [AvaloniaFact]
    public async Task TheReputationTabRendersFromControlsLists()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        await using TestApp app = new(scripts: new ToolsScriptHost(Skua(SampleFactions())));
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths));
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();
        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        shell.NavigateCommand.Execute("Control");
        Render();
        shell.Control.OpenListsCommand.Execute(null);
        Render();
        Assert.Equal(AppPage.World, shell.CurrentPage);
        shell.World.SelectTabCommand.Execute("Reputation");
        for (int i = 0; i < 300 && shell.World.FactionRows.Count < 4; i++)
        {
            await Task.Delay(10);
            Render();
        }
        Render();
        Control view = window.GetVisualDescendantOfType(nameof(WorldView))!;
        ListBox list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.IsVisible);
        Assert.Equal(4, list.ItemCount);
        Assert.Contains(view.GetVisualDescendants().OfType<ProgressBar>(), p => p.IsVisible && p.Value == 100);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Rank 10 · MAX");
        Assert.True(shell.GoBack());
        Assert.Equal(AppPage.Control, shell.CurrentPage);

        window.Close();
        List<string> problems;
        lock (sink.Messages)
            problems = sink.Messages.Where(m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase) || m.Contains("Error", StringComparison.Ordinal)).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}

public partial class SessionIntegrationTests
{
    /// <summary>An app around an engine the test made (for the pages over the real Skua host).</summary>
    internal static AppServices AppOver(HeadlessBotEngine engine, IUiDispatcher? dispatcher = null, IPlatformServices? platform = null)
    {
        AppPaths paths = AppPaths.UnderRoot(Sandbox.NewDir());
        paths.EnsureCreated();
        EngineHost host = new(engine, new SettingsStore(paths.SettingsFile), new CredentialVault(new MemorySecretStore()), new ResumeStore(paths.ResumeFile), platform ?? new RecordingPlatform());
        ScriptLibrary library = new(paths.ScriptsDir);
        return new AppServices(host, dispatcher ?? new InlineDispatcher(), new NoPicker(), library, new ScriptRepositorySync(library, paths.SkuaDir, new FakeHttp()), paths);
    }

    /// <summary>
    /// The Reputation tab on the real Skua host over the fake server: Skua's FactionList (rank math of the headless
    /// client) and a reputation gain the server reports, shown without reopening the tab.
    /// </summary>
    [Fact]
    public async Task TheReputationTabShowsSkuasFactionsAndALiveGain()
    {
        await using FakeAqw aqw = new();
        (SkuaScriptHostAdapter _, HeadlessBotEngine engine) = ToolsHost(aqw);
        try
        {
            AppServices services = AppOver(engine);
            WorldViewModel world = new(services);
            engine.SnapshotChanged += (_, s) => world.Apply(s);
            world.IsActive = true;
            world.SelectTabCommand.Execute("Reputation");

            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => world.FactionRows.Count == 1, 15000);
            FactionRow good = world.FactionRows[0];
            Assert.Equal((2, "Good", 2, 100, 2700, 2600, 1000), (good.Id, good.Name, good.Rank, good.Rep, good.RequiredRep, good.RemainingRep, good.TotalRep));

            // The server reports 2,600 more: rank 3 starts at 3,600.
            FakeAqwServer.Session game = aqw.Server.LastSession!;
            game.Json(new JsonObject { ["cmd"] = "addGoldExp", ["intGold"] = 0, ["intExp"] = 0, ["FactionID"] = 2, ["iRep"] = 2600 });
            await Sandbox.Until(() => world.FactionRows.SingleOrDefault() is { Rank: 3, TotalRep: 3600, Rep: 0 }, 15000);
            Assert.Equal("0 / 6,400 (6,400 to rank 4)", world.FactionRows[0].ProgressText);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }
}
