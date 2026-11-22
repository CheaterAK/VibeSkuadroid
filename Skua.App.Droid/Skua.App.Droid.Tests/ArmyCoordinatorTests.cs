using Skua.App.Droid.Core.Army;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;

namespace Skua.App.Droid.Tests;

/// <summary>
/// The army commands against several fake AQW servers at once: slot 1 (this process's engine), a full slot "process"
/// (its engine behind the real protocol, in memory: Skua.Core's process-wide statics allow one Skua host per process,
/// so the full slot here has no script host; scripts are covered by the app's single-engine tests) and light sessions.
/// </summary>
public class ArmyCoordinatorTests
{
    [Fact]
    public async Task StartAllJumpAllLoadScriptStopAllAndLogoutAll()
    {
        await using ArmyRig rig = await ArmyRig.CreateAsync(lights: 3);
        IReadOnlyList<ArmyResult> started = await rig.Army.StartAllAsync();
        Assert.All(started, r => Assert.True(r.Ok, $"{r.Member}: {r.Message}"));
        Assert.All(rig.Army.Members, m => Assert.True(m.IsOnline, m.Account.Label));
        Assert.Equal(5, rig.Army.Members.Count);
        Assert.Contains(rig.Army.Members, m => m.Slot == 2 && m.Remote is not null);
        Assert.Contains(rig.Army.Members, m => m.Slot == 1 && m.Engine == rig.Main);
        Assert.Equal(3, rig.Army.Members.Count(m => m.Light is not null));
        Assert.True(rig.Platform.KeptAlive);

        // Staggered: each fake server saw its login at least the spacing after the previous one.
        List<long> firsts = rig.AllServers.Select(s => s.Server.Sessions[0].Received.First().Timestamp).Order().ToList();
        for (int i = 1; i < firsts.Count; i++)
            Assert.True((firsts[i] - firsts[i - 1]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= 90, "logins not spaced");

        IReadOnlyList<ArmyResult> jumped = await rig.Army.JumpAllAsync("testmap", "r2", "Left");
        Assert.All(jumped, r => Assert.True(r.Ok, $"{r.Member}: {r.Message}"));
        await Sandbox.Until(() => rig.Army.Members.All(m => m.Snapshot.Character?.Cell == "r2"), 10000);
        Assert.Equal("testmap|r2|Left", rig.Accounts.Current.RecentJumps![0]);
        await Assert.ThrowsAsync<EngineException>(() => rig.Army.JumpAllAsync("bad map!"));

        await rig.Army.JumpAllToPlayerAsync("Someone");
        await Sandbox.Until(() => rig.AllServers.All(s => s.Server.LastSession!.Received.Any(r => r.Text.Contains("%goto%someone%"))));

        File.WriteAllText(Path.Combine(rig.Paths.ScriptsDir, "Farm.cs"), "class F {}");
        rig.Slot2!.Layout.Prepare();
        IReadOnlyList<ArmyResult> loaded = await rig.Army.LoadScriptToAllAsync(Path.Combine(rig.Paths.ScriptsDir, "Farm.cs"),
            m => new ScriptStartOptions($"Army_{m.Account.Username}", Silent: true));
        Assert.Equal(3, loaded.Count(r => r.Message.StartsWith("Skipped: light accounts", StringComparison.Ordinal)));
        // No Skua host in this test process for the full slots: their own refusal comes back per member.
        Assert.Equal(2, loaded.Count(r => !r.Ok && r.Message.Contains("Scripts cannot run")));

        rig.Army.FightAll("Pixie");
        Assert.All(rig.Army.Members.Where(m => m.Light is not null), m => Assert.NotNull(m.Light!.Fight));
        await rig.Army.StopAllAsync();
        Assert.All(rig.Army.Members.Where(m => m.Light is not null), m => Assert.Null(m.Light!.Fight));
        Assert.All(rig.Army.Members, m => Assert.True(m.IsOnline));

        await rig.Army.LogoutAllAsync();
        await Sandbox.Until(() => rig.Army.Members.All(m => !m.IsOnline));
        Assert.Contains(2, rig.Platform.Stopped);
        Assert.False(rig.Platform.KeptAlive);
        Assert.NotNull(rig.Platform.LastSummary);
    }

    [Fact]
    public async Task ButlerSummonsFollowersAndSyncsQuests()
    {
        await using ArmyRig rig = await ArmyRig.CreateAsync(lights: 3);
        await rig.Army.StartAllAsync();
        ArmyMember master = rig.Army.Members.First(m => m.Light is not null);
        List<ArmyMember> followers = rig.Army.Members.Where(m => m != master && m.Slot != SlotIds.MainSlot).ToList();
        rig.Army.SetRoster(master.Account.Id, followers.Select(f => f.Account.Id));
        Assert.Equal(master, rig.Army.Master);
        Assert.Equal(followers.Count, rig.Army.Followers.Count);

        IReadOnlyList<ArmyResult> summoned = await rig.Army.SummonAllAsync();
        Assert.All(summoned, r => Assert.True(r.Ok, r.Message));
        Assert.All(followers.Where(f => f.Light is not null), f => Assert.Equal(master.Name, f.Light!.FollowingName));
        // The full follower /goto'es the master.
        FakeAqw slotServer = rig.ServerOf(followers.Single(f => f.Remote is not null));
        await Sandbox.Until(() => slotServer.Server.LastSession!.Received.Any(r => r.Text.Contains("%goto%")));

        master.Light!.AcceptQuests([1000]);
        await Sandbox.Until(() => master.Light.World.GetAcceptedQuestIds().Contains(1000));
        IReadOnlyList<ArmyResult> synced = await rig.Army.SyncQuestsAsync();
        Assert.All(synced, r => Assert.True(r.Ok, r.Message));
        foreach (ArmyMember f in followers)
            await Sandbox.Until(() => rig.ServerOf(f).Server.LastSession!.Accepted.Contains(1000));

        rig.Army.ReleaseAll();
        Assert.All(followers.Where(f => f.Light is not null), f => Assert.Null(f.Light!.FollowingName));
        await Assert.ThrowsAsync<EngineException>(async () =>
        {
            rig.Army.SetRoster(null, []);
            await rig.Army.SummonAllAsync();
        });
    }

    [Fact]
    public async Task MemoryAdmissionRefusesWhatTheDeviceCannotHold()
    {
        await using ArmyRig rig = await ArmyRig.CreateAsync(lights: 2);
        // 1.2 GB available: no room for a full slot (450 + 430 MB + 512 MB reserve), room for light sessions.
        rig.Platform.Memory = new DeviceMemory(8L << 30, 1200L << 20, 200L << 20);
        IReadOnlyList<ArmyResult> started = await rig.Army.StartAllAsync();
        ArmyResult slot2 = started.Single(r => r.Member == "slotted");
        Assert.False(slot2.Ok);
        Assert.Contains("Not enough memory", slot2.Message);
        Assert.Empty(rig.Platform.Started);
        Assert.Equal(2, started.Count(r => r.Ok && rig.Army.Members.Single(m => m.Account.Label == r.Member).IsLight));
        Assert.True(started.Single(r => r.Member == "main").Ok); // slot 1 is this process: already paid for

        Assert.Equal(0, MemoryAdmission.HowManyMore(true, rig.Platform.Memory, MemoryEstimates.Default, 1, 2));
        Assert.True(MemoryAdmission.HowManyMore(false, rig.Platform.Memory, MemoryEstimates.Default, 1, 2) > 50);
        Admission none = MemoryAdmission.Check(true, new DeviceMemory(10L << 30, 8L << 30, 300L << 20), MemoryEstimates.Default, fullRunning: 2, maxFull: 2);
        Assert.Contains("All 2 full slots", none.Reason);
        Assert.True(MemoryAdmission.Check(true, new DeviceMemory(10L << 30, 4L << 30, 300L << 20), MemoryEstimates.Default, 1, 2).Allowed);
        Assert.False(MemoryAdmission.Check(true, new DeviceMemory(10L << 30, 4L << 30, 300L << 20), MemoryEstimates.Default, 1, 2, pendingBytes: 3L << 30).Allowed);
    }

    [Fact]
    public async Task SwitchingAnAccountBetweenLightAndFullLogsItOutWhereItRan()
    {
        await using ArmyRig rig = await ArmyRig.CreateAsync(lights: 1);
        await rig.Army.StartAllAsync();
        ArmyMember light = rig.Army.Members.Single(m => m.Light is not null);
        LightAccount session = light.Light!;
        rig.Accounts.Save(light.Account with { Mode = AccountMode.Full, FullSlot = 2 });
        // Slot 2 belonged to another account: the switch is normalized (one account per full slot).
        await Sandbox.Until(() => session.Engine.Snapshot.Connection == ConnectionPhase.Offline);
        ArmyMember now = rig.Army.Find(light.Account.Id)!;
        Assert.False(now.IsLight);
        Assert.Equal(2, now.Slot);
        Assert.True(rig.Accounts.Current.Accounts.Single(a => a.Username == "slotted").Mode == AccountMode.Light);
    }

    [Fact]
    public async Task AFullSlotWhosePasswordIsMissingFailsClearlyWithoutRetries()
    {
        await using ArmyRig rig = await ArmyRig.CreateAsync(lights: 0);
        ArmyAccount slotted = rig.Accounts.Current.Accounts.Single(a => a.Username == "slotted");
        rig.Vault.Forget(slotted.Id);
        ArmyResult r = await rig.Army.LoginAsync(rig.Army.Find(slotted.Id)!);
        Assert.False(r.Ok);
        Assert.Contains("No password is saved", r.Message);
    }
}

/// <summary>An army on fake servers: slot 1, one full slot (2) behind the real protocol, and light sessions.</summary>
internal sealed class ArmyRig : IAsyncDisposable
{
    private readonly Dictionary<string, FakeAqw> _servers = new();

    private ArmyRig()
    {
    }

    public AppPaths Paths { get; private set; } = null!;

    public MemorySecretStore Secrets { get; } = new();

    public AccountStore Accounts { get; private set; } = null!;

    public AccountVault Vault { get; private set; } = null!;

    public HeadlessBotEngine Main { get; private set; } = null!;

    public FakeArmyPlatform Platform { get; private set; } = null!;

    public ArmyCoordinator Army { get; private set; } = null!;

    public SlotHost? Slot2 => Platform.Hosts.GetValueOrDefault(2);

    public IEnumerable<FakeAqw> AllServers => _servers.Values;

    public FakeAqw ServerOf(ArmyMember m) => _servers[m.Account.Id];

    public static Task<ArmyRig> CreateAsync(int lights)
    {
        ArmyRig rig = new();
        rig.Paths = AppPaths.UnderRoot(Sandbox.NewDir());
        rig.Paths.EnsureCreated();
        rig.Accounts = new AccountStore(SlotLayout.AccountsFileOf(rig.Paths));
        rig.Vault = new AccountVault(rig.Secrets);
        ArmyAccount main = rig.Add(new ArmyAccount(AccountStore.NewId(), "main", Mode: AccountMode.Full, FullSlot: 1));
        rig.Main = rig._servers[main.Id].NewEngine();
        rig.Add(new ArmyAccount(AccountStore.NewId(), "slotted", Mode: AccountMode.Full, FullSlot: 2));
        for (int i = 0; i < lights; i++)
            rig.Add(new ArmyAccount(AccountStore.NewId(), $"light{i}", Tags: ["farm"]));
        rig.Platform = new FakeArmyPlatform(rig);
        LoginStagger stagger = new(new StaggerOptions { Spacing = TimeSpan.FromMilliseconds(100), JitterMinMs = 0, JitterMaxMs = 20 });
        rig.Army = new ArmyCoordinator(rig.Platform, rig.Accounts, rig.Vault, rig.Main, stagger,
            a => ArmyLightTests.NewLight(rig._servers[a.Id], a.Username, stagger), rig.Paths.ScriptsDir);
        return Task.FromResult(rig);
    }

    private ArmyAccount Add(ArmyAccount account)
    {
        Accounts.Save(account);
        Vault.Save(account, FakeAqw.Password);
        _servers[account.Id] = new FakeAqw();
        return account;
    }

    public async ValueTask DisposeAsync()
    {
        await Army.DisposeAsync();
        await Platform.DisposeAsync();
        await Main.DisposeAsync();
        foreach (FakeAqw s in _servers.Values)
            await s.DisposeAsync();
    }

    /// <summary>Slot "processes" in memory: the slot's real host and endpoint on its own folders, linked by a loopback channel.</summary>
    internal sealed class FakeArmyPlatform : IArmyPlatform, IAsyncDisposable
    {
        private readonly ArmyRig _rig;
        private readonly List<IAsyncDisposable> _owned = new();

        public FakeArmyPlatform(ArmyRig rig)
        {
            _rig = rig;
        }

        public DeviceMemory Memory { get; set; } = new(12L << 30, 8L << 30, 300L << 20);

        public Dictionary<int, SlotHost> Hosts { get; } = new();

        public List<int> Started { get; } = new();

        public List<int> Stopped { get; } = new();

        public bool KeptAlive { get; private set; }

        public string? LastSummary { get; private set; }

        public ISlotChannel? StartSlot(int slot)
        {
            Started.Add(slot);
            SlotLayout layout = SlotLayout.For(_rig.Paths, slot);
            layout.Prepare();
            ArmyAccount account = _rig.Accounts.Current.Accounts.Single(a => a.FullSlot == slot);
            HeadlessBotEngine engine = _rig._servers[account.Id].NewEngine();
            EngineHost host = new(engine, new SettingsStore(layout.Paths.SettingsFile), new CredentialVault(_rig.Secrets), new ResumeStore(layout.Paths.ResumeFile), new NullPlatformServices());
            SlotHost slotHost = new(slot, host, layout, new AccountStore(layout.AccountsFile), new AccountVault(_rig.Secrets));
            SlotEndpoint endpoint = new(slotHost, eventInterval: TimeSpan.FromMilliseconds(20));
            Hosts[slot] = slotHost;
            _owned.Add(host);
            return new LoopbackSlotChannel(endpoint);
        }

        /// <summary>Links to a running slot (as after a restart of the main process).</summary>
        public bool AttachExisting { get; set; }

        public ISlotChannel? AttachSlot(int slot) =>
            AttachExisting && Hosts.TryGetValue(slot, out SlotHost? host)
                ? new LoopbackSlotChannel(new SlotEndpoint(host, eventInterval: TimeSpan.FromMilliseconds(20)))
                : null;

        public void StopSlot(int slot)
        {
            Stopped.Add(slot);
            if (Hosts.Remove(slot, out SlotHost? host))
                _ = host.Engine.LogoutAsync();
        }

        public DeviceMemory ReadDeviceMemory() => Memory;

        public void KeepAlive(bool active) => KeptAlive = active;

        public void PublishSummary(string? summary) => LastSummary = summary ?? LastSummary;

        public async ValueTask DisposeAsync()
        {
            foreach (IAsyncDisposable d in _owned)
                await d.DisposeAsync();
        }
    }
}
