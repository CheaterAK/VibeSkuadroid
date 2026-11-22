using Skua.App.Droid.Core.Army;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;

namespace Skua.App.Droid.Tests;

/// <summary>Restarts in an army: a slot restarts only its own process and logs its account back in; the coordinator re-links running slots.</summary>
public class ArmyRestartTests
{
    [Fact]
    public async Task ASlotRestartRestartsOnlyThatSlotAndItLogsBackIn()
    {
        await using FakeAqw aqw = new();
        AppPaths main = AppPaths.UnderRoot(Sandbox.NewDir());
        main.EnsureCreated();
        SlotLayout layout = SlotLayout.For(main, 3);
        layout.Prepare();
        MemorySecretStore secrets = new();
        AccountStore accounts = new(layout.AccountsFile);
        ArmyAccount account = accounts.Save(new ArmyAccount(AccountStore.NewId(), "hero", Mode: AccountMode.Full, FullSlot: 2));
        new AccountVault(secrets).Save(account, FakeAqw.Password);
        RecordingPlatform platform = new();

        HeadlessBotEngine engine = aqw.NewEngine();
        await using (EngineHost host = new(engine, new SettingsStore(layout.Paths.SettingsFile), new CredentialVault(secrets), new ResumeStore(layout.Paths.ResumeFile), platform))
        {
            SlotHost slot = new(3, host, layout, accounts, new AccountVault(secrets));
            await slot.LoginAsync(account.Id, null, CancellationToken.None);
            Assert.True(host.CanResumeAfterRestart()); // the army vault has the password
            Assert.True(slot.RequestRestart("memory"));
            Assert.Single(platform.Restarts); // the slot's own platform: its own process
            await engine.LogoutAsync(); // what the restarter does before ending the process
            Assert.True(slot.State.Online); // still to be resumed
        }

        // The slot's next process: same folders, new engine.
        HeadlessBotEngine next = aqw.NewEngine();
        await using EngineHost nextHost = new(next, new SettingsStore(layout.Paths.SettingsFile), new CredentialVault(secrets), new ResumeStore(layout.Paths.ResumeFile), platform);
        SlotHost resumed = new(3, nextHost, layout, new AccountStore(layout.AccountsFile), new AccountVault(secrets));
        string? message = await resumed.ResumeAsync();
        Assert.Equal("Slot 3 logged back in.", message);
        Assert.True(next.Snapshot.IsOnline);

        // A logout by the user is not resumed.
        await next.LogoutAsync();
        Assert.False(resumed.State.Online);
        Assert.Null(await resumed.ResumeAsync());
    }

    [Fact]
    public async Task TheMainProcessDoesNotRestartItselfWhileLightSessionsLiveInIt()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: true);
        app.Host.AutomaticRestartVeto = () => "light army sessions are online in this process";
        Assert.False(app.Host.TryRestartBetweenQueueRuns("queue"));
        app.Host.AutomaticRestartVeto = null;
        // The manual restart is the user's choice and stays possible.
        Assert.True(app.Host.RequestRestart("manual"));
    }

    [Fact]
    public async Task ARestartedCoordinatorRelinksSlotsThatKeptRunning()
    {
        await using ArmyRig rig = await ArmyRig.CreateAsync(lights: 0);
        await rig.Army.StartAllAsync();
        ArmyMember slot2 = rig.Army.Members.Single(m => m.Slot == 2);
        Assert.True(slot2.IsOnline);

        // The main process restarted: a new coordinator on the same files; slot 2's process still runs.
        rig.Platform.AttachExisting = true;
        await using ArmyCoordinator again = new(rig.Platform, new AccountStore(SlotLayout.AccountsFileOf(rig.Paths)), rig.Vault, rig.Main, new LoginStagger(),
            a => throw new InvalidOperationException("no light"), rig.Paths.ScriptsDir);
        ArmyMember relinked = again.Members.Single(m => m.Slot == 2);
        Assert.NotNull(relinked.Remote);
        await relinked.Remote!.HelloAsync();
        Assert.True(relinked.IsOnline);
        Assert.Equal(slot2.Account.Id, relinked.Remote.AccountId);
    }
}
