using System.Text;
using System.Text.Json.Nodes;
using Skua.App.Droid.Core.Army;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;

namespace Skua.App.Droid.Tests;

/// <summary>The coordinator/slot protocol, the remote engine proxy over it, the per-slot data layout and the account vault.</summary>
public class ArmySlotTests
{
    [Fact]
    public void MessagesRoundTripAndBadOnesAreRefused()
    {
        SlotMessage m = new() { Kind = SlotMessageKind.Request, Id = 7, Op = SlotOps.JoinMap, Args = new JsonObject { ["map"] = "yulgar" } };
        SlotMessage back = SlotProtocol.Decode(SlotProtocol.Encode(m));
        Assert.Equal(7, back.Id);
        Assert.Equal(SlotMessageKind.Request, back.Kind);
        Assert.Equal("yulgar", SlotProtocol.RequireString(back.Args, "map"));

        Assert.Throws<SlotProtocolException>(() => SlotProtocol.Decode([]));
        Assert.Throws<SlotProtocolException>(() => SlotProtocol.Decode("{not json"u8));
        Assert.Throws<SlotProtocolException>(() => SlotProtocol.Decode(Encoding.UTF8.GetBytes("{\"v\":2,\"op\":\"hello\"}")));
        Assert.Throws<SlotProtocolException>(() => SlotProtocol.Decode(Encoding.UTF8.GetBytes("{\"v\":1,\"op\":\"\"}")));
        Assert.Throws<SlotProtocolException>(() => SlotProtocol.Decode(new byte[SlotProtocol.MaxMessageBytes + 1]));
        Assert.Throws<SlotProtocolException>(() => SlotProtocol.Encode(m with { Args = new JsonObject { ["x"] = new string('a', SlotProtocol.MaxMessageBytes) } }));
        Assert.Throws<SlotProtocolException>(() => SlotProtocol.RequireString(new JsonObject { ["map"] = 3 }, "map"));
        Assert.Throws<SlotProtocolException>(() => SlotProtocol.RequireString(new JsonObject(), "map"));
    }

    [Fact]
    public async Task TheEndpointAnswersBadRequestsWithErrorsAndIgnoresGarbage()
    {
        await using SlotRig rig = await SlotRig.StartAsync();
        Assert.Null(await rig.Endpoint.HandleAsync("garbage"u8.ToArray()));
        byte[]? unknown = await rig.Endpoint.HandleAsync(SlotProtocol.Encode(new SlotMessage { Kind = SlotMessageKind.Request, Id = 3, Op = "format-disk" }));
        SlotMessage answer = SlotProtocol.Decode(unknown!);
        Assert.False(answer.Ok);
        Assert.Equal(3, answer.Id);
        Assert.Contains("Unknown operation", answer.Error);

        // Names are checked before anything is sent.
        EngineException e = await Assert.ThrowsAsync<EngineException>(() => rig.Remote.JoinMapAsync("yulgar;drop", "Enter", "Spawn"));
        Assert.Contains("not a map name", e.Message);
        await Assert.ThrowsAsync<EngineException>(() => rig.Remote.GotoPlayerAsync("a%xt%zm"));
        await Assert.ThrowsAsync<EngineException>(() => rig.Remote.StartScriptAsync("../../etc/passwd.cs", ScriptStartOptions.Default));
        await Assert.ThrowsAsync<EngineException>(() => rig.Remote.LoginAsync(new LoginRequest("hero", "x", null)));
        Assert.False(await rig.Remote.SendPacketAsync("%xt%zm%cmd%1%goto%x%"));
    }

    [Fact]
    public async Task TheRemoteEngineLogsInByAccountIdAndMirrorsTheSlot()
    {
        await using SlotRig rig = await SlotRig.StartAsync();
        SlotHello hello = await rig.Remote.HelloAsync();
        Assert.Equal(2, hello.Slot);

        await rig.Remote.LoginAccountAsync(rig.Account.Id, null);
        await Sandbox.Until(() => rig.Remote.Snapshot.IsOnline);
        Assert.Equal(rig.Account.Id, rig.Remote.AccountId);
        Assert.Equal("Sandbox", rig.Remote.Snapshot.ServerName);
        Assert.True(rig.Host.State.Online);

        await rig.Remote.JoinMapAsync("testmap", "r2", "Left");
        await Sandbox.Until(() => rig.Remote.Snapshot.Character?.Cell == "r2");
        await Sandbox.Until(() => rig.Remote.GetRecentLog().Any(l => l.Message.Contains("Online on Sandbox")));
        WorldLists lists = await rig.Remote.FetchWorldListsAsync();
        Assert.Contains(lists.Monsters, m => m.Name.Contains("Pixie"));
        Assert.Empty(lists.Inventory); // inventories stay in the slot

        // Scripts cannot run without a Skua host: the slot's own message reaches the coordinator.
        File.WriteAllText(Path.Combine(rig.Layout.Paths.ScriptsDir, "x.cs"), "class X {}");
        EngineException noScripts = await Assert.ThrowsAsync<EngineException>(() => rig.Remote.StartScriptAsync("x.cs", ScriptStartOptions.Default));
        Assert.Contains("Scripts cannot run", noScripts.Message);

        await rig.Remote.LogoutAsync();
        await Sandbox.Until(() => rig.Remote.Snapshot.Connection == ConnectionPhase.Offline);
        Assert.False(rig.Host.State.Online);
        Assert.Null(rig.Remote.AccountId);
    }

    [Fact]
    public async Task NoPasswordOrTokenEverCrossesTheProcessBoundary()
    {
        await using SlotRig rig = await SlotRig.StartAsync();
        await rig.Remote.LoginAccountAsync(rig.Account.Id, null);
        await Sandbox.Until(() => rig.Remote.Snapshot.IsOnline);
        await rig.Remote.RefreshAsync();
        rig.Endpoint.Flush();
        string everything = string.Join('\n', rig.Wire.Select(b => Encoding.UTF8.GetString(b)));
        Assert.NotEmpty(rig.Wire);
        Assert.DoesNotContain(FakeAqw.Password, everything);
        Assert.DoesNotContain(Skua.Core.Headless.Tests.Fakes.FakeAqwServer.TokenPrefix, everything);
    }

    [Fact]
    public async Task AMissingPasswordOrUnknownAccountIsAClearError()
    {
        await using SlotRig rig = await SlotRig.StartAsync();
        ArmyAccount other = rig.Accounts.Save(new ArmyAccount(AccountStore.NewId(), "nopass"));
        EngineException e = await Assert.ThrowsAsync<EngineException>(() => rig.Remote.LoginAccountAsync(other.Id, null));
        Assert.Contains("No password is saved", e.Message);
        EngineException unknown = await Assert.ThrowsAsync<EngineException>(() => rig.Remote.LoginAccountAsync("0123456789ab", null));
        Assert.Contains("not in the account list", unknown.Message);
    }

    [Fact]
    public async Task ASlotThatDiesFailsPendingRequestsAndShowsOffline()
    {
        await using SlotRig rig = await SlotRig.StartAsync();
        await rig.Remote.HelloAsync();
        rig.Channel.SimulateDeath();
        Assert.Equal(ConnectionPhase.Offline, rig.Remote.Snapshot.Connection);
        Assert.Equal("Slot process stopped", rig.Remote.Snapshot.StatusText);
        EngineException e = await Assert.ThrowsAsync<EngineException>(() => rig.Remote.LogoutAsync());
        Assert.Contains("not running", e.Message);
        // Unreadable bytes from a slot are logged, never thrown.
        rig.Channel.Inject("junk"u8.ToArray());
    }

    [Fact]
    public async Task ARequestTheSlotNeverAnswersTimesOut()
    {
        SilentChannel silent = new();
        await using RemoteBotEngine remote = new(3, silent);
        Task t = remote.LogoutAsync();
        Assert.False(t.IsCompleted);
        using CancellationTokenSource cts = new(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => remote.JoinMapAsync("a", "b", "c", cts.Token));
    }

    [Fact]
    public void SlotLayoutsNeverShareAWritableFolder()
    {
        AppPaths main = AppPaths.UnderRoot(Sandbox.NewDir());
        Assert.Empty(SlotLayout.Collisions(main, Enumerable.Range(1, SlotIds.MaxFullSlots)));
        HashSet<string> homes = Enumerable.Range(1, SlotIds.MaxFullSlots).Select(s => SlotLayout.For(main, s).Paths.SkuaDir).ToHashSet();
        Assert.Equal(SlotIds.MaxFullSlots, homes.Count);
        HashSet<string> caches = Enumerable.Range(1, SlotIds.MaxFullSlots).Select(s => SlotLayout.For(main, s).Paths.CompiledScriptCacheDir).ToHashSet();
        Assert.Equal(SlotIds.MaxFullSlots, caches.Count);
        // The vault is the one shared store.
        Assert.Single(Enumerable.Range(1, SlotIds.MaxFullSlots).Select(s => SlotLayout.For(main, s).Paths.SecretsDir).Distinct());
        Assert.Throws<ArgumentOutOfRangeException>(() => SlotLayout.For(main, 7));

        Assert.Equal(3, SlotIds.FromProcessName("com.vibeskua.droid:acct3"));
        Assert.Null(SlotIds.FromProcessName("com.vibeskua.droid"));
        Assert.Null(SlotIds.FromProcessName("com.vibeskua.droid:acct1"));
        Assert.Null(SlotIds.FromProcessName("com.vibeskua.droid:acct03"));
        Assert.Null(SlotIds.FromProcessName("com.vibeskua.droid:restart"));
        Assert.Equal(":acct6", SlotIds.ProcessSuffix(6));
    }

    [Fact]
    public void ASlotSeesTheSharedScriptsButKeepsItsOwnCompileCache()
    {
        AppPaths main = AppPaths.UnderRoot(Sandbox.NewDir());
        main.EnsureCreated();
        Directory.CreateDirectory(Path.Combine(main.ScriptsDir, "Story"));
        File.WriteAllText(Path.Combine(main.ScriptsDir, "Story", "Quest.cs"), "// quest");
        File.WriteAllText(Path.Combine(main.ScriptsDir, "CoreBots.cs"), "// core");
        Directory.CreateDirectory(Path.Combine(main.ScriptsDir, "Cached-Scripts"));
        File.WriteAllText(Path.Combine(main.ScriptsDir, "Cached-Scripts", "main.dll"), "x");
        File.WriteAllText(Path.Combine(main.SkuaDir, "UserAdvancedSkills.json"), "{}");
        Directory.CreateDirectory(main.SkuaOptionsDir);
        File.WriteAllText(Path.Combine(main.SkuaOptionsDir, "Queue_Quest_1.cfg"), "a=b");

        SlotLayout slot = SlotLayout.For(main, 2);
        Assert.Empty(slot.Prepare());
        Assert.Empty(slot.Prepare()); // idempotent
        Assert.Equal("// quest", File.ReadAllText(slot.ResolveScript("Story/Quest.cs")));
        Assert.Equal("// core", File.ReadAllText(Path.Combine(slot.Paths.ScriptsDir, "CoreBots.cs")));
        Assert.False(File.Exists(Path.Combine(slot.Paths.CompiledScriptCacheDir, "main.dll")));
        Assert.True(File.Exists(Path.Combine(slot.Paths.SkuaDir, "UserAdvancedSkills.json")));
        Assert.True(slot.CopyOptionProfile("Queue_Quest_1"));
        Assert.Equal("a=b", File.ReadAllText(Path.Combine(slot.Paths.SkuaOptionsDir, "Queue_Quest_1.cfg")));
        Assert.False(File.Exists(Path.Combine(main.SkuaOptionsDir, "..", "..", "slots")));

        // A script added later shows up at the next prepare; a removed one goes.
        File.WriteAllText(Path.Combine(main.ScriptsDir, "New.cs"), "// new");
        File.Delete(Path.Combine(main.ScriptsDir, "CoreBots.cs"));
        slot.Prepare();
        Assert.True(File.Exists(slot.ResolveScript("New.cs")));
        Assert.False(File.Exists(Path.Combine(slot.Paths.ScriptsDir, "CoreBots.cs")));

        Assert.Throws<EngineException>(() => slot.ResolveScript("../../droid/settings.json"));
        Assert.Throws<EngineException>(() => slot.ResolveScript("Story/../../x.cs"));
        Assert.Throws<EngineException>(() => slot.ResolveScript("/etc/passwd"));
        Assert.Throws<EngineException>(() => slot.CopyOptionProfile("../settings"));
    }

    [Fact]
    public void EachAccountHasItsOwnVaultEntryBoundToItsName()
    {
        MemorySecretStore store = new();
        AccountVault vault = new(store);
        CredentialVault single = new(store);
        single.Save("main", "main-pass");
        ArmyAccount a = new(AccountStore.NewId(), "alpha");
        ArmyAccount b = new(AccountStore.NewId(), "beta");
        vault.Save(a, "pass-a");
        vault.Save(b, "pass-b");
        Assert.Equal("pass-a", vault.Load(a, out VaultStatus sa));
        Assert.Equal(VaultStatus.Found, sa);
        Assert.Equal("pass-b", vault.Load(b, out _));
        Assert.Equal("main-pass", single.TryLoad("main"));
        // Renamed account: the password saved for the old name is not offered.
        Assert.Null(vault.Load(a with { Username = "gamma" }, out _));
        vault.Forget(a.Id);
        Assert.False(vault.HasPassword(a.Id));
        Assert.True(vault.HasPassword(b.Id));
        Assert.Throws<ArgumentException>(() => AccountVault.EntryFor("../aqw-account"));
    }

    [Fact]
    public void TheAccountListKeepsOneAccountPerFullSlotAndAValidRoster()
    {
        string path = Path.Combine(Sandbox.NewDir(), "accounts.json");
        AccountStore store = new(path);
        ArmyAccount a = store.Save(new ArmyAccount(AccountStore.NewId(), "alpha", Mode: AccountMode.Full, FullSlot: 2));
        ArmyAccount b = store.Save(new ArmyAccount(AccountStore.NewId(), "beta", Tags: ["farm"]));
        Assert.Throws<ArgumentException>(() => store.Save(new ArmyAccount(AccountStore.NewId(), "ALPHA")));
        Assert.Throws<ArgumentException>(() => store.Save(new ArmyAccount(AccountStore.NewId(), "bad%name")));
        store.Save(b with { Mode = AccountMode.Full, FullSlot = 2 });
        ArmyState s = store.Current;
        Assert.Equal(AccountMode.Light, s.Accounts.Single(x => x.Id == a.Id).Mode); // slot 2 went to beta
        Assert.Equal(2, s.Accounts.Single(x => x.Id == b.Id).FullSlot);
        store.Save(b with { Mode = AccountMode.Full, FullSlot = 5 });
        Assert.Equal(AccountMode.Light, store.Current.Accounts.Single(x => x.Id == b.Id).Mode); // only 2 full slots by default

        store.Update(x => x with { MasterId = a.Id, FollowerIds = [b.Id, a.Id, "ffffffffffff"] });
        Assert.Equal([b.Id], store.Current.FollowerIds);
        store.Remove(a.Id);
        Assert.Null(store.Current.MasterId);
        ArmyState reread = new AccountStore(path).Current;
        Assert.Equal(store.Current.Accounts.Select(x => (x.Id, x.Username, x.Mode, x.FullSlot)), reread.Accounts.Select(x => (x.Id, x.Username, x.Mode, x.FullSlot)));
        Assert.Equal(["farm"], reread.Accounts.Single().TagList);
    }

    /// <summary>A channel to a slot that never answers.</summary>
    private sealed class SilentChannel : ISlotChannel
    {
        public bool IsConnected => true;

        public event Action<byte[]>? Received
        {
            add { }
            remove { }
        }

        public event Action<bool>? ConnectionChanged
        {
            add { }
            remove { }
        }

        public bool Send(byte[] message) => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A full slot (slot 2) as its process would compose it, linked to the coordinator's proxy in memory.</summary>
internal sealed class SlotRig : IAsyncDisposable
{
    private SlotRig()
    {
    }

    public required AppPaths Main { get; init; }

    public required SlotLayout Layout { get; init; }

    public required AccountStore Accounts { get; init; }

    public required ArmyAccount Account { get; init; }

    public required EngineHost EngineHost { get; init; }

    public required SlotHost Host { get; init; }

    public required SlotEndpoint Endpoint { get; init; }

    public required RecordingChannel Channel { get; init; }

    public required RemoteBotEngine Remote { get; init; }

    public List<byte[]> Wire => Channel.Wire;

    public static Task<SlotRig> StartAsync()
    {
        AppPaths main = AppPaths.UnderRoot(Sandbox.NewDir());
        main.EnsureCreated();
        SlotLayout layout = SlotLayout.For(main, 2);
        layout.Prepare();
        MemorySecretStore secrets = new();
        AccountStore accounts = new(layout.AccountsFile);
        ArmyAccount account = accounts.Save(new ArmyAccount(AccountStore.NewId(), "hero", Mode: AccountMode.Full, FullSlot: 2));
        new AccountVault(secrets).Save(account, FakeAqw.Password);

        FakeAqw aqw = new();
        HeadlessBotEngine engine = aqw.NewEngine();
        EngineHost host = new(engine, new SettingsStore(layout.Paths.SettingsFile), new CredentialVault(secrets), new ResumeStore(layout.Paths.ResumeFile), new NullPlatformServices());
        SlotHost slot = new(2, host, layout, new AccountStore(layout.AccountsFile), new AccountVault(secrets));
        SlotEndpoint endpoint = new(slot, eventInterval: TimeSpan.FromMilliseconds(20));
        RecordingChannel channel = new(endpoint);
        RemoteBotEngine remote = new(2, channel, main.ScriptsDir);
        return Task.FromResult(new SlotRig
        {
            Main = main, Layout = layout, Accounts = accounts, Account = account, EngineHost = host, Host = slot,
            Endpoint = endpoint, Channel = channel, Remote = remote, AqwOwned = aqw,
        });
    }

    public required FakeAqw AqwOwned { get; init; }

    public async ValueTask DisposeAsync()
    {
        await Remote.DisposeAsync();
        Endpoint.Dispose();
        await EngineHost.DisposeAsync();
        await AqwOwned.DisposeAsync();
    }
}

/// <summary>The loopback channel, recording every byte that crosses it in either direction.</summary>
internal sealed class RecordingChannel : ISlotChannel
{
    private readonly LoopbackSlotChannel _inner;

    public RecordingChannel(SlotEndpoint endpoint)
    {
        _inner = new LoopbackSlotChannel(endpoint);
        _inner.Received += bytes =>
        {
            lock (Wire)
                Wire.Add(bytes);
            Received?.Invoke(bytes);
        };
        _inner.ConnectionChanged += c => ConnectionChanged?.Invoke(c);
    }

    public List<byte[]> Wire { get; } = new();

    public bool IsConnected => _inner.IsConnected;

    public event Action<byte[]>? Received;

    public event Action<bool>? ConnectionChanged;

    public void SimulateDeath() => _inner.SimulateDeath();

    public void Inject(byte[] bytes) => _inner.Inject(bytes);

    public bool Send(byte[] message)
    {
        lock (Wire)
            Wire.Add(message);
        return _inner.Send(message);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
