using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Memory;
using Skua.App.Droid.Core.ViewModels;
using Skua.Core.Headless.Tests.Fakes;

namespace Skua.App.Droid.Tests;

/// <summary>The engine end to end over a real loopback socket (FakeAqwServer) and a faked web API.</summary>
public class EngineTests : IAsyncLifetime
{
    private readonly FakeAqw _aqw = new();
    private HeadlessBotEngine _engine = null!;

    public ValueTask InitializeAsync()
    {
        _engine = _aqw.NewEngine();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync();
        await _aqw.DisposeAsync();
    }

    [Fact]
    public async Task LoginBringsTheCharacterOnline()
    {
        await _engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        EngineSnapshot s = _engine.Snapshot;
        Assert.Equal(ConnectionPhase.Online, s.Connection);
        Assert.Equal("Sandbox", s.ServerName);
        Assert.NotNull(s.Character);
        Assert.Equal("Hero", s.Character!.Name, ignoreCase: true);
        Assert.True(s.Character.HpMax > 0);

        WorldLists lists = _engine.GetWorldLists();
        Assert.NotEmpty(lists.Inventory);
        Assert.False(lists.BankLoaded);
        await _engine.LoadBankAsync();
        Assert.True(_engine.GetWorldLists().BankLoaded);
    }

    [Fact]
    public async Task ServerListAndJoinMapWork()
    {
        IReadOnlyList<ServerChoice> servers = await _engine.GetServersAsync();
        Assert.Equal(["Gold", "Sandbox"], servers.Select(s => s.Name));
        await _engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, "Sandbox"));
        await _engine.JoinMapAsync("testmap", "r2", "Left");
        await Sandbox.Until(() => _engine.Snapshot.Character?.Cell == "r2");
        Assert.Contains(_engine.GetWorldLists().Monsters, m => m.Name.Contains("Pixie"));
    }

    [Fact]
    public async Task WrongPasswordFailsWithAMessage()
    {
        EngineException e = await Assert.ThrowsAsync<EngineException>(() => _engine.LoginAsync(new LoginRequest("hero", "nope-nope", null)));
        Assert.Contains("Wrong password", e.Message);
        Assert.Equal(ConnectionPhase.Failed, _engine.Snapshot.Connection);
    }

    [Fact]
    public async Task PasswordAndTokenNeverReachLogOrPackets()
    {
        _engine.PacketCaptureEnabled = true;
        await _engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        await _engine.SendPacketAsync($"%xt%zm%message%1%{FakeAqw.Password}%");
        await Sandbox.Until(() => _engine.GetRecentPackets().Any(p => p.Direction == PacketDirection.Out && p.Text.Contains("message")));

        string everything = string.Join('\n', _engine.GetRecentLog().Select(l => l.Display))
            + string.Join('\n', _engine.GetRecentPackets().Select(p => p.Display));
        Assert.DoesNotContain(FakeAqw.Password, everything);
        Assert.DoesNotContain(FakeAqwServer.Token, everything);
        Assert.Contains(_engine.GetRecentPackets(), p => p.Direction == PacketDirection.In);
    }

    [Fact]
    public async Task ReconnectsAfterTheServerDropsUs()
    {
        await _engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        _aqw.Server.LastSession!.Close();
        await Sandbox.Until(() => _engine.Snapshot.Connection is ConnectionPhase.Reconnecting or ConnectionPhase.Connecting);
        await Sandbox.Until(() => _engine.Snapshot.Connection == ConnectionPhase.Online && _aqw.Server.Sessions.Count == 2);
        Assert.Contains(_engine.GetRecentLog(), l => l.Message.Contains("Reconnected"));
    }

    [Fact]
    public async Task LogoutReplacesTheSessionAndGoesOffline()
    {
        await _engine.LoginAsync(new LoginRequest("hero", FakeAqw.Password, null));
        var first = _engine.Session;
        await _engine.LogoutAsync();
        Assert.NotSame(first, _engine.Session);
        Assert.Equal(ConnectionPhase.Offline, _engine.Snapshot.Connection);
        Assert.Null(_engine.Snapshot.Character);
        Assert.Same(WorldLists.Empty, _engine.GetWorldLists());
        _aqw.Server.LastSession!.Close();
        await Task.Delay(200);
        Assert.Equal(ConnectionPhase.Offline, _engine.Snapshot.Connection); // no reconnect after logout
    }

    [Fact]
    public async Task ScriptsReportUnavailableAndRefuseToStart()
    {
        Assert.False(_engine.ScriptSupport.Available);
        _engine.LoadScript("/tmp/x.cs");
        EngineException e = await Assert.ThrowsAsync<EngineException>(() => _engine.StartScriptAsync());
        Assert.Contains("no script host", e.Message);
    }

    [Fact]
    public async Task SnapshotsCarryMemorySamples()
    {
        await Sandbox.Until(() => _engine.Snapshot.Memory.RssBytes > 0);
        Assert.True(_engine.Snapshot.Memory.ManagedBytes > 0);
    }
}

public class EngineHostTests
{
    [Fact]
    public async Task RememberMeStoresInVaultAndEmptyPasswordUsesIt()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, "Sandbox", remember: true);
        Assert.True(app.Settings.Current.RememberPassword);
        Assert.Equal("hero", app.Settings.Current.AccountName);
        Assert.True(app.Host.HasRememberedPassword("hero"));
        Assert.DoesNotContain(FakeAqw.Password, File.ReadAllText(app.Paths.SettingsFile));

        await app.Host.LogoutAsync();
        await app.Host.LoginAsync("hero", "", null, remember: true);
        Assert.Equal(ConnectionPhase.Online, app.Engine.Snapshot.Connection);

        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        Assert.Empty(app.Store.Entries);
    }

    [Fact]
    public async Task RestartResumesTheLoginInTheNextProcess()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, "Sandbox", remember: true);
        Assert.True(app.Host.RequestRestart("test"));
        Assert.Single(app.Platform.Restarts);
        Assert.True(app.Platform.Restarts[0].Resume);
        await app.Host.LogoutAsync();

        // "Next process": same files, new host.
        await using HeadlessBotEngine engine = app.Aqw.NewEngine();
        EngineHost next = new(engine, new Core.Settings.SettingsStore(app.Paths.SettingsFile), new Core.Security.CredentialVault(app.Store), new ResumeStore(app.Paths.ResumeFile), app.Platform);
        ResumeResult? result = await next.ResumeAfterRestartAsync();
        Assert.NotNull(result);
        Assert.Contains("logged back in", result.Message);
        Assert.True(result.LoggedIn);
        Assert.False(result.NeedsUser);
        Assert.Equal(ConnectionPhase.Online, engine.Snapshot.Connection);
        Assert.Null(await next.ResumeAfterRestartAsync());
    }

    [Fact]
    public async Task ATransientKeystoreFailureKeepsThePasswordAndAsksTheUserToReconnect()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, "Sandbox", remember: true);
        Assert.True(app.Host.RequestRestart("test"));
        await app.Host.LogoutAsync();

        app.Store.Transient = true; // e.g. the Keystore is busy right after the restart
        await using HeadlessBotEngine engine = app.Aqw.NewEngine();
        EngineHost next = new(engine, new Core.Settings.SettingsStore(app.Paths.SettingsFile), new Core.Security.CredentialVault(app.Store), new ResumeStore(app.Paths.ResumeFile), app.Platform);
        ResumeResult? result = await next.ResumeAfterRestartAsync();

        Assert.NotNull(result);
        Assert.False(result.LoggedIn);
        Assert.True(result.NeedsUser); // the service shows the "tap to reconnect" notice
        Assert.Contains("could not be read right now", result.Message);
        Assert.Equal(ConnectionPhase.Offline, engine.Snapshot.Connection);
        // The encrypted entry and the "remembered" state survive for the next attempt.
        Assert.True(app.Store.Entries.ContainsKey(Core.Security.CredentialVault.EntryName));
        Assert.True(next.HasRememberedPassword("hero"));

        // An empty-password login now explains why instead of silently forgetting the password.
        EngineException e = await Assert.ThrowsAsync<EngineException>(() => next.LoginAsync("hero", "", null, remember: true));
        Assert.Contains("cannot be read right now", e.Message);

        app.Store.Transient = false;
        await next.LoginAsync("hero", "", null, remember: true);
        Assert.Equal(ConnectionPhase.Online, engine.Snapshot.Connection);
    }

    [Fact]
    public async Task RememberedPasswordStateNeverDecrypts()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("Hero", FakeAqw.Password, "Sandbox", remember: true);
        Assert.Equal("Hero", app.Settings.Current.RememberedAccount);
        int loads = app.Store.Loads;

        // What the login form does on every keystroke, the settings page on load and the watchdog per sample.
        foreach (string typed in new[] { "h", "he", "her", "hero", "HERO", "heroo", "" })
            _ = app.Host.HasRememberedPassword(typed);
        _ = app.Host.CanResumeAfterRestart();
        using ShellViewModel shell = new(app.Services);
        shell.Login.Account = "her";
        shell.Login.Account = "hero";
        Assert.True(shell.Login.HasStoredPassword);
        shell.Login.Account = "villain";
        Assert.False(shell.Login.HasStoredPassword);
        Assert.True(shell.Settings.HasRememberedPassword);

        Assert.Equal(loads, app.Store.Loads);
        Assert.True(app.Host.HasRememberedPassword(" hero "));
        Assert.False(app.Host.HasRememberedPassword("villain"));

        // Without the encrypted entry the metadata alone does not claim a password.
        app.Store.Entries.Clear();
        Assert.False(app.Host.HasRememberedPassword("hero"));
    }

    [Fact]
    public async Task SettingsFromBeforeTheMetadataFallBackToTheLastAccount()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, "Sandbox", remember: true);
        app.Settings.Update(s => s with { RememberedAccount = null }); // as written by 0.1.0
        Assert.True(app.Host.HasRememberedPassword("hero"));
        app.Host.ForgetPassword();
        Assert.False(app.Host.HasRememberedPassword("hero"));
        Assert.Null(app.Settings.Current.RememberedAccount);
    }

    [Fact]
    public async Task AFailureToStoreThePasswordDoesNotFailTheLogin()
    {
        await using TestApp app = new();
        app.Store.FailSaves = true;
        Exception? reported = null;
        app.Host.RememberFailed += (_, e) => reported = e;
        await app.Host.LoginAsync("hero", FakeAqw.Password, "Sandbox", remember: true);
        Assert.Equal(ConnectionPhase.Online, app.Engine.Snapshot.Connection);
        Assert.NotNull(reported);
        Assert.False(app.Settings.Current.RememberPassword);
        Assert.False(app.Host.HasRememberedPassword("hero"));
    }

    [Fact]
    public async Task NoAutomaticRestartWhenTheSessionCouldNotBeRestored()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        Assert.False(app.Host.CanResumeAfterRestart());
        app.Settings.Update(s => s with { MemoryThresholdMb = AppSettingsMin });
        Assert.Empty(app.Platform.Restarts);
    }

    private const int AppSettingsMin = Core.Settings.AppSettings.MinMemoryThresholdMb;
}
