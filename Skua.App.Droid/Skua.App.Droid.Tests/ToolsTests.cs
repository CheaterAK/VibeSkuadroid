using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Plugins;
using Skua.App.Droid.Core.Tools;
using Skua.App.Droid.Core.ViewModels;
using Skua.App.Droid.Scripting;
using Skua.App.Droid.UI.Views;
using Skua.Core.Headless;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Tests.Fakes;
using Skua.Core.Models;

namespace Skua.App.Droid.Tests;

/// <summary>The Tools hub (from Settings) and its pages: Fast Travel, Loader, Grabber.</summary>
public partial class ToolsTests
{
    internal static (TestApp App, FakeGameTools Tools) NewApp()
    {
        FakePluginBackends plugins = new();
        return (new TestApp(scripts: new PluginOnlyScriptHost(plugins)), plugins.FakeTools);
    }

    internal static async Task AnswerAsync(ConfirmHost confirm, bool yes, string? title = null)
    {
        await Sandbox.Until(() => confirm.IsOpen && (title is null || confirm.Title == title));
        if (yes)
            confirm.ConfirmCommand.Execute(null);
        else
            confirm.CancelCommand.Execute(null);
    }

    internal static EngineSnapshot ScriptRunning { get; } =
        EngineSnapshot.Initial with { Connection = ConnectionPhase.Online, Script = new ScriptStatus(ScriptRunState.Running, "/s/Farm.cs", DateTimeOffset.UtcNow, null) };

    [Fact]
    public async Task TheToolsHubOpensFromSettingsAndBackClosesItStepByStep()
    {
        TestApp app = NewApp().App;
        await using TestApp _ = app;
        using ShellViewModel shell = new(app.Services);
        shell.NavigateCommand.Execute("Settings");
        Assert.Equal("Settings", shell.Title);
        shell.Settings.OpenToolsCommand.Execute(null);
        ToolsViewModel tools = shell.Settings.Tools;
        GameToolPages pages = shell.Settings.GamePages;
        Assert.True(shell.Settings.ShowTools);
        Assert.Equal("Tools", shell.Title);
        Assert.Equal(["Fast Travel", "Loader", "Grabber"], tools.Entries.Select(e => e.Title).Take(3));
        ToolEntry entry = Assert.Single(tools.Entries, e => e.Title == "Fast Travel");
        Assert.False(string.IsNullOrWhiteSpace(entry.Description));
        tools.OpenCommand.Execute(entry);
        Assert.Same(pages.FastTravel, tools.CurrentPage);
        Assert.Equal("Fast Travel", shell.Title);
        Assert.Equal(2, pages.FastTravel.Destinations.Count); // refreshed when shown

        // Back: an open question first, then the page, then the hub, then Home.
        Task delete = pages.FastTravel.Destinations[0].DeleteCommand.ExecuteAsync(null);
        Assert.True(pages.FastTravel.Confirm.IsOpen);
        Assert.True(shell.GoBack());
        await delete;
        Assert.False(pages.FastTravel.Confirm.IsOpen);
        Assert.Same(pages.FastTravel, tools.CurrentPage);
        Assert.True(shell.GoBack());
        Assert.True(shell.Settings.ShowTools);
        Assert.Null(tools.CurrentPage);
        Assert.True(shell.GoBack());
        Assert.False(shell.Settings.ShowTools);
        Assert.Equal("Settings", shell.Title);
        Assert.True(shell.GoBack());
        Assert.Equal(AppPage.Home, shell.CurrentPage);
    }

    [Fact]
    public async Task FastTravelListsSearchesAndTravelsInOneTap()
    {
        (TestApp app, FakeGameTools tools) = NewApp();
        await using TestApp _ = app;
        FakeFastTravel backend = tools.FakeFastTravel;
        FastTravelViewModel page = new(app.Services, new PluginStore(Path.Combine(app.Paths.SkuaDir, "tools.json")));
        page.Refresh();
        Assert.Equal(["Nulgath", "Shop"], page.Destinations.Select(d => d.Name));
        Assert.Equal("tercessuinotlim · Boss2 · Right", page.Destinations[0].Where);

        page.SearchText = "SHOPMAP";
        Assert.Equal(["Shop"], page.Destinations.Select(d => d.Name));
        page.SearchText = "boss2";
        Assert.Equal(["Nulgath"], page.Destinations.Select(d => d.Name));
        page.SearchText = string.Empty;

        // Travel is one tap (it moves only the character), in the private room by default (Skua's 111111).
        await page.Destinations[1].TravelCommand.ExecuteAsync(null);
        Assert.False(page.Confirm.IsOpen);
        Assert.Equal("travel shopmap Enter Spawn 111111", backend.Calls[^1]);
        Assert.Contains("shopmap-111111", page.Status);

        page.UsePrivateRoom = false;
        await page.Destinations[0].TravelCommand.ExecuteAsync(null);
        Assert.Equal("travel tercessuinotlim Boss2 Right public", backend.Calls[^1]);
        page.UsePrivateRoom = true;
        page.PrivateRoomNumber = 4242;

        // While a script runs it asks first.
        page.Apply(ScriptRunning);
        Task travel = page.Destinations[1].TravelCommand.ExecuteAsync(null);
        await AnswerAsync(page.Confirm, yes: false, ConfirmHost.ScriptRunningTitle);
        await travel;
        Assert.DoesNotContain("4242", string.Join(",", backend.Calls));
        travel = page.Destinations[1].TravelCommand.ExecuteAsync(null);
        Assert.Contains("do it anyway?", page.Confirm.Text);
        await AnswerAsync(page.Confirm, yes: true);
        await travel;
        Assert.Equal("travel shopmap Enter Spawn 4242", backend.Calls[^1]);

        // The private room is remembered.
        FastTravelViewModel again = new(app.Services, new PluginStore(Path.Combine(app.Paths.SkuaDir, "tools.json")));
        Assert.True(again.UsePrivateRoom);
        Assert.Equal(4242m, again.PrivateRoomNumber);
    }

    [Fact]
    public async Task FastTravelAddsEditsDeletesAndAsksBeforeRemoving()
    {
        (TestApp app, FakeGameTools tools) = NewApp();
        await using TestApp _ = app;
        FakeFastTravel backend = tools.FakeFastTravel;
        FastTravelViewModel page = new(app.Services, new PluginStore(Path.Combine(app.Paths.SkuaDir, "tools.json")));
        page.Refresh();

        // Add from the current location.
        page.UseCurrentLocationCommand.Execute(null);
        Assert.Equal(("testmap", "r2", "Left"), (page.EditMap, page.EditCell, page.EditPad));
        page.EditName = "Pixies, again";
        page.SaveEditCommand.Execute(null);
        Assert.Contains("Commas", page.Message);
        page.EditName = "Pixies";
        page.SaveEditCommand.Execute(null);
        Assert.Null(page.Message);
        Assert.Equal(["Nulgath", "Shop", "Pixies"], backend.Saved.Select(d => d.Name));
        Assert.Equal(3, page.Count);

        // Edit in place.
        page.Destinations[1].EditCommand.Execute(null);
        Assert.True(page.IsEditing);
        page.EditCell = "r9";
        page.SaveEditCommand.Execute(null);
        Assert.False(page.IsEditing);
        Assert.Equal(new TravelDestination("Shop", "shopmap", "r9", "Spawn"), backend.Saved[1]);

        // Delete asks; cancel keeps it.
        Task delete = page.Destinations[0].DeleteCommand.ExecuteAsync(null);
        await AnswerAsync(page.Confirm, yes: false, "Delete destination");
        await delete;
        Assert.Equal(3, backend.Saved.Count);
        delete = page.Destinations[0].DeleteCommand.ExecuteAsync(null);
        await AnswerAsync(page.Confirm, yes: true);
        await delete;
        Assert.Equal(["Shop", "Pixies"], backend.Saved.Select(d => d.Name));

        // Presets: only the missing ones, after asking.
        Task presets = page.AddPresetsCommand.ExecuteAsync(null);
        await AnswerAsync(page.Confirm, yes: true, "Add presets");
        await presets;
        Assert.Equal(["Shop", "Pixies", "Tercessuinotlim", "Nulgath"], backend.Saved.Select(d => d.Name));

        // Clear asks (the desktop's question).
        Task clear = page.ClearCommand.ExecuteAsync(null);
        await Sandbox.Until(() => page.Confirm.IsOpen);
        Assert.Equal("This will clear all fast travels from the list. Continue?", page.Confirm.Text);
        page.Confirm.ConfirmCommand.Execute(null);
        await clear;
        Assert.Empty(backend.Saved);
        Assert.Empty(page.Destinations);

        // Errors are shown.
        backend.Here = null;
        page.UseCurrentLocationCommand.Execute(null);
        Assert.Contains("Log in", page.Message);
    }

    [Fact]
    public async Task WithoutASkuaHostTheToolsSaySo()
    {
        await using TestApp app = new();
        FastTravelViewModel page = new(app.Services, new PluginStore(Path.Combine(app.Paths.SkuaDir, "tools.json")));
        page.Refresh();
        Assert.False(page.IsAvailable);
        Assert.Contains("Skua script host", page.Message);
    }

    [AvaloniaFact]
    public async Task TheToolsPagesRenderWithoutBindingErrors()
    {
        CollectingSink sink = new();
        Logger.Sink = sink;
        TestApp app = NewApp().App;
        await using TestApp _ = app;
        using ShellViewModel shell = new(new AppServices(app.Host, new ImmediateUiDispatcher(), app.Picker, app.Services.Library, app.Services.RepositorySync, app.Paths));
        Window window = new() { Width = 412, Height = 915, Content = new MainView { DataContext = shell } };
        window.Show();

        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        shell.NavigateCommand.Execute("Settings");
        Render();
        shell.Settings.OpenToolsCommand.Execute(null);
        Render();
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(ToolsView)));
        ToolsViewModel tools = shell.Settings.Tools;
        GameToolPages pages = shell.Settings.GamePages;
        foreach (ToolEntry entry in tools.Entries.ToList())
        {
            tools.OpenCommand.Execute(entry);
            Render();
            tools.CloseCommand.Execute(null);
            Render();
        }

        tools.Show(pages.FastTravel, "Fast Travel");
        Render();
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(FastTravelView)));
        Assert.Equal(2, pages.FastTravel.Destinations.Count);
        Task delete = pages.FastTravel.Destinations[0].DeleteCommand.ExecuteAsync(null);
        Render();
        Control overlay = Assert.IsAssignableFrom<Control>(window.GetVisualDescendantOfType(nameof(ConfirmOverlay)));
        Assert.True(overlay.IsEffectivelyVisible);
        pages.FastTravel.Confirm.CancelCommand.Execute(null);
        await delete;
        Render();
        Assert.False(overlay.IsEffectivelyVisible);

        // The Grabber with rows, its actions and a quantity question; the Loader with its list.
        tools.Show(pages.Grabber, "Grabber");
        await pages.Grabber.GrabCommand.ExecuteAsync(null);
        Render();
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(GrabberView)));
        pages.Grabber.Rows[0].IsSelected = true;
        Task buy = pages.Grabber.Actions[0].Command.ExecuteAsync(null);
        Render();
        Assert.True(pages.Grabber.Confirm.HasQuantity);
        pages.Grabber.Confirm.CancelCommand.Execute(null);
        await buy;
        Render();
        tools.Show(pages.Loader, "Loader");
        await Sandbox.Until(() => pages.Loader.Quests.Count == 3);
        Render();
        Assert.NotNull(window.GetVisualDescendantOfType(nameof(LoaderView)));

        lock (sink.Messages)
            Assert.DoesNotContain(sink.Messages, m => m.Contains("Binding", StringComparison.OrdinalIgnoreCase));
        window.Close();
    }
}

public partial class SessionIntegrationTests
{
    /// <summary>
    /// Fast Travel on the real Skua host over the fake server: Skua's saved destinations and presets, saving, the
    /// current location, and travel through Skua's map service into a private room.
    /// </summary>
    [Fact]
    public async Task FastTravelUsesSkuasSettingsAndMapService()
    {
        await using FakeAqw aqw = new();
        (SkuaScriptHostAdapter host, HeadlessBotEngine engine) = ToolsHost(aqw);
        IGameTools tools = Assert.IsAssignableFrom<IGameTools>(engine.Plugins?.GameTools);
        try
        {
            IFastTravelBackend ft = tools.FastTravel;
            Assert.Contains(ft.Presets, p => p.Name == "Nulgath" && p.Map == "tercessuinotlim" && p.Cell == "Boss2");
            Assert.Null(ft.CurrentLocation());
            Assert.Contains("Log in", Assert.Throws<EngineException>(() => ft.Travel(new("x", "shopmap", "Enter", "Spawn"), false, 1)).Message);

            List<TravelDestination> saved = [new("Shop", "shopmap", "Enter", "Spawn"), new("Pixies", "testmap", "r2", "Left")];
            ft.Save(saved);
            Assert.Equal(saved, ft.Load());
            Assert.Contains("Shop,shopmap,Enter,Spawn", host.Skua!.Services.GetService(typeof(Skua.Core.Interfaces.ISettingsService)) is Skua.Core.Interfaces.ISettingsService s
                ? s.Get<System.Collections.Specialized.StringCollection>("FastTravels")!.Cast<string>() : []);

            await engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
            await Sandbox.Until(() => engine.Snapshot.Character is not null && host.Skua!.Bot.Map.Loaded);
            FakeAqwServer.Session game = aqw.Server.LastSession!;
            Assert.Equal(new TravelLocation("testmap", "Enter", "Spawn"), ft.CurrentLocation());

            ft.Travel(saved[0], usePrivateRoom: true, roomNumber: 4242);
            await Sandbox.Until(() => game.Received.Any(r => r.Text.Contains("%tfer%", StringComparison.Ordinal) && r.Text.Contains("%shopmap-4242%", StringComparison.Ordinal)), 15000);
            await Sandbox.Until(() => host.Skua!.Bot.Map.Name == "shopmap", 15000);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    internal static (SkuaScriptHostAdapter Host, HeadlessBotEngine Engine) ToolsHost(FakeAqw aqw)
    {
        SkuaScriptHostAdapter host = new(null, new ScriptAssemblyStore(), ClientFileSources.SkuaScriptsDIR, o =>
        {
            o.WebApi = new AqwWebApi(new AqwWebApiOptions { BaseUri = FakeAqw.BaseUri }, aqw.Http);
            o.Game = new HeadlessGameOptions
            {
                Session = aqw.NewSession().Options,
                Client = new GameClientOptions { MapLoadTime = TimeSpan.FromMilliseconds(200) },
                DownloadMapCells = false,
            };
        });
        HeadlessBotEngine engine = new(new HeadlessEngineOptions
        {
            SessionFactory = () => throw new InvalidOperationException("the host owns the session"),
            PublishInterval = TimeSpan.FromMilliseconds(20),
            MemorySampleInterval = TimeSpan.FromMilliseconds(100),
        }, host);
        return (host, engine);
    }
}
