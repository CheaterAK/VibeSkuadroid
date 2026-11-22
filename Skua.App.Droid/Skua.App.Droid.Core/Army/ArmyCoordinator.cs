using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Security;
using Skua.Core.Headless.World;

namespace Skua.App.Droid.Core.Army;

/// <summary>What the coordinator needs from the OS.</summary>
public interface IArmyPlatform
{
    /// <summary>Starts full slot <paramref name="slot"/> (2..6: its foreground service in its own process) and links to it; null when refused.</summary>
    ISlotChannel? StartSlot(int slot);

    /// <summary>Logs the slot out, stops its service and ends its process.</summary>
    void StopSlot(int slot);

    DeviceMemory ReadDeviceMemory();

    /// <summary>Light sessions are online (they live in this process: its foreground service must run).</summary>
    void KeepAlive(bool active);

    /// <summary>The army's lines for the app's one persistent notification (null: none).</summary>
    void PublishSummary(string? summary);
}

/// <summary>An army member as the Army page shows it.</summary>
public sealed class ArmyMember
{
    internal ArmyMember(ArmyAccount account)
    {
        Account = account;
    }

    public ArmyAccount Account { get; internal set; }

    public bool IsLight => Account.Mode == AccountMode.Light;

    public int? Slot => Account.Mode == AccountMode.Full ? Account.FullSlot : null;

    /// <summary>The light session (light accounts, once created).</summary>
    public LightAccount? Light { get; internal set; }

    /// <summary>The proxy of a full slot process (slots 2..6, once started).</summary>
    public RemoteBotEngine? Remote { get; internal set; }

    /// <summary>The engine driving this member, if any: the light engine, the remote proxy or the main engine (slot 1).</summary>
    public IBotEngine? Engine { get; internal set; }

    public EngineSnapshot Snapshot => Engine?.Snapshot ?? EngineSnapshot.Initial;

    public bool IsOnline => Snapshot.IsOnline;

    /// <summary>The last message of a command for this member (refusals, failures).</summary>
    public string? Note { get; internal set; }

    /// <summary>The character's name when online, else the account's label.</summary>
    public string Name => Snapshot.Character?.Name ?? Account.Label;

    public string KindText => IsLight ? "light" : Slot == SlotIds.MainSlot ? "full · main" : $"full · slot {Slot}";

    /// <summary>What it does: the light behaviours, or the script / queue of a full slot.</summary>
    public string Activity => Light is not null
        ? Light.Status
        : Snapshot.Script.IsBusy ? $"{(Snapshot.Script.IsPaused ? "Paused" : "Running")} {Snapshot.Script.ScriptName}" : Remote?.QueueText ?? "No script";
}

/// <summary>One line of an army command's outcome.</summary>
public sealed record ArmyResult(string Member, bool Ok, string Message);

/// <summary>
/// The army in the main process: the account list made live. Light accounts are <see cref="LightAccount"/>s in this
/// process; full accounts run in slot 1 (this process's own engine, the single-account engine) or in a slot process
/// (2..6) reached through <see cref="RemoteBotEngine"/>. Every login goes through the <see cref="LoginStagger"/>; every
/// slot start through <see cref="MemoryAdmission"/>. The commands follow the desktop's army controls (Start All, Stop
/// All, Load Script to All, Jump All to Map/Cell/Pad, Jump All to Player) and ButlerV5's army parts (master and
/// followers, summon all, release, quest sync), coordinator-mediated instead of the desktop's sync files.
/// </summary>
public sealed class ArmyCoordinator : IAsyncDisposable
{
    private readonly IArmyPlatform _platform;
    private readonly AccountVault _vault;
    private readonly IBotEngine _main;
    private readonly Func<ArmyAccount, LightAccount> _lightFactory;
    private readonly object _gate = new();
    private readonly List<ArmyMember> _members = new();
    private readonly Dictionary<int, RemoteBotEngine> _remotes = new();
    private readonly CancellationTokenSource _lifetime = new();
    private string? _mainAccountId;
    private long _pendingBytes;
    private DateTimeOffset _pendingUntil;

    /// <param name="platform">OS services.</param>
    /// <param name="accounts">The account list.</param>
    /// <param name="vault">The army vault (this process reads the passwords of light accounts and of slot 1 only).</param>
    /// <param name="mainEngine">This process's engine (slot 1).</param>
    /// <param name="stagger">The army's login turn.</param>
    /// <param name="lightFactory">Creates a light session (tests point it at fake servers).</param>
    /// <param name="scriptsRoot">The script folder (script paths are sent relative to it).</param>
    public ArmyCoordinator(IArmyPlatform platform, AccountStore accounts, AccountVault vault, IBotEngine mainEngine, LoginStagger stagger,
        Func<ArmyAccount, LightAccount> lightFactory, string? scriptsRoot = null)
    {
        _platform = platform;
        Accounts = accounts;
        _vault = vault;
        _main = mainEngine;
        Stagger = stagger;
        _lightFactory = lightFactory;
        ScriptsRoot = scriptsRoot;
        Accounts.Changed += (_, _) => Sync();
        Sync();
        _ = Task.Run(QuestSyncLoopAsync);
    }

    public AccountStore Accounts { get; }

    public LoginStagger Stagger { get; }

    public string? ScriptsRoot { get; }

    public MemoryEstimates Estimates { get; set; } = MemoryEstimates.Default;

    /// <summary>Quest sync on: the followers accept what the master has accepted, every few seconds.</summary>
    public bool AutoQuestSync { get; set; }

    public TimeSpan QuestSyncInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Raised when a member, its state or a note changed (background thread).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ArmyMember> Members
    {
        get
        {
            lock (_gate)
                return _members.ToList();
        }
    }

    public ArmyMember? Find(string accountId) => Members.FirstOrDefault(m => m.Account.Id == accountId);

    /// <summary>Makes the members follow the account list (modes, slots, removals).</summary>
    public void Sync()
    {
        ArmyState state = Accounts.Current;
        List<(ArmyMember Member, LightAccount? Light)> drop = new();
        lock (_gate)
        {
            foreach (ArmyMember m in _members.ToList())
            {
                ArmyAccount? now = state.Accounts.FirstOrDefault(a => a.Id == m.Account.Id);
                if (now is null || now.Mode == AccountMode.Off || now.Mode != m.Account.Mode || now.FullSlot != m.Account.FullSlot)
                {
                    _members.Remove(m);
                    drop.Add((m, m.Light));
                }
                else
                {
                    m.Account = now;
                    m.Light?.Rename(now);
                }
            }
            foreach (ArmyAccount a in state.Accounts.Where(a => a.Mode != AccountMode.Off))
            {
                if (_members.Any(m => m.Account.Id == a.Id))
                    continue;
                ArmyMember member = new(a);
                if (a.Mode == AccountMode.Full && a.FullSlot == SlotIds.MainSlot)
                    member.Engine = _main;
                else if (a.Mode == AccountMode.Full && a.FullSlot is int slot && _remotes.TryGetValue(slot, out RemoteBotEngine? remote))
                {
                    member.Remote = remote;
                    member.Engine = remote;
                }
                _members.Add(member);
            }
            _members.Sort((x, y) => state.Accounts.ToList().FindIndex(a => a.Id == x.Account.Id).CompareTo(state.Accounts.ToList().FindIndex(a => a.Id == y.Account.Id)));
        }
        // A member that changed mode or slot (or was removed) is logged out where it ran.
        foreach ((ArmyMember m, LightAccount? light) in drop)
            _ = RetireAsync(m, light);
        RaiseChanged();
    }

    private async Task RetireAsync(ArmyMember m, LightAccount? light)
    {
        try
        {
            if (light is not null)
            {
                await light.LogoutAsync().ConfigureAwait(false);
                await light.DisposeAsync().ConfigureAwait(false);
            }
            else if (m.Slot is int slot && SlotIds.IsRemote(slot))
            {
                StopSlot(slot);
            }
            else if (m.Slot == SlotIds.MainSlot && _mainAccountId == m.Account.Id)
            {
                await _main.LogoutAsync().ConfigureAwait(false);
            }
        }
        catch (EngineException)
        {
        }
        UpdateKeepAlive();
    }

    /// <summary>Whether one more member of this kind may start now.</summary>
    public Admission CheckAdmission(ArmyMember member)
    {
        if (member.IsOnline)
            return Admission.Ok;
        bool full = !member.IsLight;
        // Slot 1 is this process: already paid for.
        if (member.Slot == SlotIds.MainSlot)
            return Admission.Ok;
        int fullRunning = Members.Count(m => !m.IsLight && m != member && (m.IsOnline || m.Remote?.IsConnected == true || (m.Slot == SlotIds.MainSlot && m.Snapshot.Connection != ConnectionPhase.Offline)));
        long pending = DateTimeOffset.UtcNow < _pendingUntil ? Interlocked.Read(ref _pendingBytes) : 0;
        return MemoryAdmission.Check(full, _platform.ReadDeviceMemory(), Estimates, fullRunning, Accounts.Current.FullSlots, pending);
    }

    /// <summary>Logs one member in (memory admission, then its turn in the stagger, with its retries).</summary>
    public async Task<ArmyResult> LoginAsync(ArmyMember member, CancellationToken cancellationToken = default)
    {
        Admission admission = CheckAdmission(member);
        if (!admission.Allowed)
            return Note(member, false, admission.Reason);
        StaggerResult r = await Stagger.RunOneAsync(new StaggeredLogin(member.Account.Id, member.Account.Label, ct => LoginNowAsync(member, ct)), cancellationToken).ConfigureAwait(false);
        return Note(member, r.Ok, r.Ok ? "Online" : r.Error ?? "Login failed");
    }

    /// <summary>Start All: every member not online, admitted one by one, logged in through the stagger.</summary>
    public async Task<IReadOnlyList<ArmyResult>> StartAllAsync(IProgress<ArmyResult>? progress = null, CancellationToken cancellationToken = default)
    {
        List<ArmyResult> results = new();
        foreach (ArmyMember member in Members.Where(m => !m.IsOnline && !m.Snapshot.IsBusy))
        {
            ArmyResult r = await LoginAsync(member, cancellationToken).ConfigureAwait(false);
            results.Add(r);
            progress?.Report(r);
        }
        return results;
    }

    private async Task LoginNowAsync(ArmyMember member, CancellationToken ct)
    {
        ArmyAccount account = member.Account;
        if (member.IsLight)
        {
            string password = Password(account);
            LightAccount light;
            lock (_gate)
            {
                light = member.Light ??= _lightFactory(account);
                member.Engine = light.Engine;
            }
            HookEngine(light.Engine);
            Reserve(Estimates.LightBytes);
            await light.LoginAsync(password, null, ct).ConfigureAwait(false);
            UpdateKeepAlive();
            return;
        }
        int slot = account.FullSlot ?? throw new EngineException("The account has no slot.");
        if (slot == SlotIds.MainSlot)
        {
            if (_main.Snapshot.Script.IsBusy)
                throw new EngineException("Slot 1 runs a script: stop it first.");
            string password = Password(account);
            await _main.LoginAsync(new LoginRequest(account.Username, password, account.Server), ct).ConfigureAwait(false);
            _mainAccountId = account.Id;
            return;
        }
        RemoteBotEngine remote = await EnsureSlotAsync(slot, ct).ConfigureAwait(false);
        lock (_gate)
        {
            member.Remote = remote;
            member.Engine = remote;
        }
        if (!_vault.HasPassword(account.Id))
            throw new EngineException($"No password is saved for {account.Label}: edit the account and enter it.");
        await remote.LoginAccountAsync(account.Id, account.Server, ct).ConfigureAwait(false);
    }

    private string Password(ArmyAccount account) =>
        _vault.Load(account, out VaultStatus status) ?? throw new EngineException(status == VaultStatus.Unavailable
            ? $"The saved password of {account.Label} cannot be read right now."
            : $"No password is saved for {account.Label}: edit the account and enter it.");

    private async Task<RemoteBotEngine> EnsureSlotAsync(int slot, CancellationToken ct)
    {
        RemoteBotEngine? remote;
        lock (_gate)
            _remotes.TryGetValue(slot, out remote);
        if (remote is null)
        {
            ISlotChannel channel = _platform.StartSlot(slot) ?? throw new EngineException($"Android refused to start slot {slot} (open the app, or allow background use).");
            remote = new RemoteBotEngine(slot, channel, ScriptsRoot);
            lock (_gate)
                _remotes[slot] = remote;
            HookEngine(remote);
            Reserve(Estimates.FullIdleBytes);
        }
        // The slot process starts in a second or two; it says hello once its engine is composed.
        DateTimeOffset until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (remote.IsConnected)
            {
                try
                {
                    await remote.HelloAsync(ct).ConfigureAwait(false);
                    return remote;
                }
                catch (EngineException) when (DateTimeOffset.UtcNow < until)
                {
                }
            }
            if (DateTimeOffset.UtcNow > until)
                throw new EngineException($"Slot {slot} did not start within a minute.");
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    private void StopSlot(int slot)
    {
        RemoteBotEngine? remote;
        lock (_gate)
        {
            _remotes.Remove(slot, out remote);
            foreach (ArmyMember m in _members.Where(m => m.Remote == remote))
            {
                m.Remote = null;
                m.Engine = null;
            }
        }
        _platform.StopSlot(slot);
        if (remote is not null)
            _ = remote.DisposeAsync().AsTask();
        RaiseChanged();
    }

    private void Reserve(long bytes)
    {
        Interlocked.Add(ref _pendingBytes, bytes);
        _pendingUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => Interlocked.Add(ref _pendingBytes, -bytes), TaskScheduler.Default);
    }

    /// <summary>Logs one member out (a full slot process is ended, which returns its memory).</summary>
    public async Task<ArmyResult> LogoutAsync(ArmyMember member, CancellationToken cancellationToken = default)
    {
        try
        {
            if (member.Light is LightAccount light)
            {
                await light.LogoutAsync(cancellationToken).ConfigureAwait(false);
                UpdateKeepAlive();
            }
            else if (member.Slot is int slot && SlotIds.IsRemote(slot))
                StopSlot(slot);
            else if (member.Engine is IBotEngine engine)
                await engine.LogoutAsync(cancellationToken).ConfigureAwait(false);
            return Note(member, true, "Logged out");
        }
        catch (EngineException e)
        {
            return Note(member, false, e.Message);
        }
    }

    public Task<IReadOnlyList<ArmyResult>> LogoutAllAsync(CancellationToken cancellationToken = default) =>
        ForEachAsync(Members.Where(m => m.Engine is not null), m => LogoutAsync(m, cancellationToken));

    /// <summary>Stop All (the desktop's): full slots stop their script; light accounts stop follow, fight and quests. Sessions stay online.</summary>
    public Task<IReadOnlyList<ArmyResult>> StopAllAsync(CancellationToken cancellationToken = default) =>
        ForEachAsync(Members.Where(m => m.IsOnline), async m =>
        {
            if (m.Light is LightAccount light)
            {
                light.StopAll();
                return Note(m, true, "Stopped");
            }
            if (!m.Snapshot.Script.IsBusy)
                return Note(m, true, "No script running");
            await m.Engine!.StopScriptAsync(cancellationToken).ConfigureAwait(false);
            return Note(m, true, "Script stopped");
        });

    /// <summary>
    /// Load Script to All: every online full member starts the script, with its own option profile when
    /// <paramref name="profileFor"/> names one (the queue's per-entry storages). Light members have no script engine:
    /// they are skipped with a message (start a light task instead).
    /// </summary>
    public Task<IReadOnlyList<ArmyResult>> LoadScriptToAllAsync(string scriptPath, Func<ArmyMember, ScriptStartOptions>? profileFor = null, CancellationToken cancellationToken = default) =>
        ForEachAsync(Members, async m =>
        {
            if (m.IsLight)
                return Note(m, false, "Skipped: light accounts cannot run scripts (start Fight or Quest on them, or make the account full).");
            if (!m.IsOnline || m.Engine is null)
                return Note(m, false, "Skipped: not online");
            ScriptStartOptions options = profileFor?.Invoke(m) ?? ScriptStartOptions.Default;
            await m.Engine.StartScriptAsync(scriptPath, options, cancellationToken).ConfigureAwait(false);
            return Note(m, true, $"Started {Path.GetFileNameWithoutExtension(scriptPath)}");
        });

    /// <summary>Jump All to Map/Cell/Pad (map may carry a room number, "yulgar-1234").</summary>
    public async Task<IReadOnlyList<ArmyResult>> JumpAllAsync(string map, string cell = "Enter", string pad = "Spawn", CancellationToken cancellationToken = default)
    {
        ArmyNames.RequireMap(map);
        ArmyNames.RequireCell(cell);
        ArmyNames.RequireCell(pad);
        IReadOnlyList<ArmyResult> results = await ForEachAsync(Members.Where(m => m.IsOnline), async m =>
        {
            string here = m.Snapshot.Character?.Map ?? string.Empty;
            if (string.Equals(here, map, StringComparison.OrdinalIgnoreCase) && m.Light is LightAccount light)
                light.Jump(cell, pad); // same map: just the cell
            else
                await m.Engine!.JoinMapAsync(map, cell, pad, cancellationToken).ConfigureAwait(false);
            return Note(m, true, $"Joined {map} {cell}");
        }, spacing: TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
        RememberJump($"{map}|{cell}|{pad}");
        return results;
    }

    /// <summary>Jump All to Player: /goto the named player.</summary>
    public Task<IReadOnlyList<ArmyResult>> JumpAllToPlayerAsync(string player, CancellationToken cancellationToken = default)
    {
        string name = ArmyNames.RequirePlayer(player);
        return ForEachAsync(Members.Where(m => m.IsOnline && !string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)), m => GotoAsync(m, name, cancellationToken),
            spacing: TimeSpan.FromMilliseconds(150));
    }

    private async Task<ArmyResult> GotoAsync(ArmyMember m, string name, CancellationToken ct)
    {
        if (m.Light is LightAccount light)
            light.GotoPlayer(name);
        else if (m.Remote is RemoteBotEngine remote)
            await remote.GotoPlayerAsync(name, ct).ConfigureAwait(false);
        else if (!await m.Engine!.SendPacketAsync($"%xt%zm%cmd%1%goto%{name.ToLowerInvariant()}%", log: false, ct).ConfigureAwait(false))
            throw new EngineException("Not sent.");
        return Note(m, true, $"Going to {name}");
    }

    // ------------------------------------------------------------------ light tasks

    /// <summary>Starts fighting on every online light member (monsters '|'-separated, "*" = any).</summary>
    public IReadOnlyList<ArmyResult> FightAll(string monsters, Func<ArmyMember, LightRotation>? rotationFor = null) =>
        Members.Where(m => m.Light is not null && m.IsOnline).Select(m =>
        {
            m.Light!.StartFight(new LightFight(monsters, rotationFor?.Invoke(m) ?? LightRotation.Default));
            return Note(m, true, $"Fighting {monsters}");
        }).ToList();

    /// <summary>Starts the quest loop on every online light member.</summary>
    public IReadOnlyList<ArmyResult> QuestAll(int questId, int maxTurnIns) =>
        Members.Where(m => m.Light is not null && m.IsOnline).Select(m =>
        {
            m.Light!.StartQuest(questId, maxTurnIns);
            return Note(m, true, $"Quest {questId}");
        }).ToList();

    /// <summary>The drop rules of every light member.</summary>
    public void SetPickupAll(LightPickup pickup)
    {
        foreach (ArmyMember m in Members.Where(m => m.Light is not null))
            m.Light!.SetPickup(pickup);
    }

    // ------------------------------------------------------------------ ButlerV5 army

    /// <summary>The Butler roster: <paramref name="masterId"/> leads, <paramref name="followerIds"/> follow (light accounts by default).</summary>
    public void SetRoster(string? masterId, IEnumerable<string> followerIds) =>
        Accounts.Update(s => s with { MasterId = masterId, FollowerIds = followerIds.Where(f => f != masterId).Distinct().ToList() });

    public ArmyMember? Master => Accounts.Current.MasterId is string id ? Find(id) : null;

    public IReadOnlyList<ArmyMember> Followers
    {
        get
        {
            HashSet<string> ids = (Accounts.Current.FollowerIds ?? []).ToHashSet();
            return Members.Where(m => ids.Contains(m.Account.Id)).ToList();
        }
    }

    /// <summary>
    /// Summon All: every follower comes to the master. Light followers keep following it (its map, cell and pad are
    /// known exactly: it is a local account); full followers /goto it once.
    /// </summary>
    public async Task<IReadOnlyList<ArmyResult>> SummonAllAsync(CancellationToken cancellationToken = default)
    {
        ArmyMember master = Master ?? throw new EngineException("Choose the master first.");
        if (!master.IsOnline)
            throw new EngineException($"{master.Account.Label} (the master) is not online.");
        return await ForEachAsync(Followers.Where(f => f.IsOnline), async f =>
        {
            if (f.Light is LightAccount light)
            {
                light.FollowLocal(() => MasterPosition(master));
                return Note(f, true, $"Following {master.Name}");
            }
            return await GotoAsync(f, master.Name, cancellationToken).ConfigureAwait(false);
        }, spacing: TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
    }

    /// <summary>Summon one follower.</summary>
    public async Task<ArmyResult> SummonAsync(ArmyMember follower, CancellationToken cancellationToken = default)
    {
        ArmyMember master = Master ?? throw new EngineException("Choose the master first.");
        if (follower.Light is LightAccount light)
        {
            light.FollowLocal(() => MasterPosition(master));
            return Note(follower, true, $"Following {master.Name}");
        }
        return await GotoAsync(follower, master.Name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Release: the followers stop following (they stay where they are).</summary>
    public IReadOnlyList<ArmyResult> ReleaseAll() =>
        Followers.Where(f => f.Light is not null).Select(f =>
        {
            f.Light!.StopFollowing();
            return Note(f, true, "Released");
        }).ToList();

    private static FollowTargetPosition? MasterPosition(ArmyMember master)
    {
        if (master.Snapshot.Character is not CharacterState c || !master.IsOnline)
            return null;
        if (master.Light is LightAccount light)
        {
            AreaInfo area = light.World.GetArea();
            return new FollowTargetPosition(c.Name, area.MapName, area.Cell, area.Pad);
        }
        return new FollowTargetPosition(c.Name, c.Map, c.Cell, c.Pad);
    }

    /// <summary>Quest sync: the followers accept the quests the master has accepted (coordinator-mediated, no sync files).</summary>
    public async Task<IReadOnlyList<ArmyResult>> SyncQuestsAsync(CancellationToken cancellationToken = default)
    {
        ArmyMember master = Master ?? throw new EngineException("Choose the master first.");
        IReadOnlyCollection<int> ids = await AcceptedQuestsAsync(master, cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
            return [];
        return await ForEachAsync(Followers.Where(f => f.IsOnline), async f =>
        {
            int n = f.Light is LightAccount light ? light.AcceptQuests(ids)
                : f.Remote is RemoteBotEngine remote ? await remote.AcceptQuestsAsync(ids, cancellationToken).ConfigureAwait(false)
                : await AcceptOnMainAsync(f, ids, cancellationToken).ConfigureAwait(false);
            return Note(f, true, n == 0 ? "Quests in sync" : $"Accepted {n} quest(s)");
        }).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyCollection<int>> AcceptedQuestsAsync(ArmyMember m, CancellationToken ct)
    {
        if (!m.IsOnline)
            return [];
        if (m.Light is LightAccount light)
            return light.World.GetAcceptedQuestIds();
        WorldLists lists = m.Remote is RemoteBotEngine remote ? await remote.FetchWorldListsAsync(ct).ConfigureAwait(false) : m.Engine!.GetWorldLists();
        return lists.Quests.Where(q => q.Accepted).Select(q => q.QuestId).ToList();
    }

    private static async Task<int> AcceptOnMainAsync(ArmyMember m, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        HashSet<int> have = m.Engine!.GetWorldLists().Quests.Where(q => q.Accepted).Select(q => q.QuestId).ToHashSet();
        int n = 0;
        foreach (int id in ids.Where(i => !have.Contains(i)).Take(30))
        {
            if (await m.Engine.SendPacketAsync($"%xt%zm%acceptQuest%1%{id}%", log: false, ct).ConfigureAwait(false))
                n++;
        }
        return n;
    }

    private async Task QuestSyncLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(QuestSyncInterval, _lifetime.Token).ConfigureAwait(false);
                if (AutoQuestSync && Master is { IsOnline: true })
                    await SyncQuestsAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (EngineException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ memory and status

    /// <summary>The memory line: this process (slot 1 and the light sessions), every slot process, the total.</summary>
    public (long MainBytes, IReadOnlyList<(int Slot, long Bytes)> Slots, long TotalBytes, int Lights) MemoryUse()
    {
        long main = _main.Snapshot.Memory.RssBytes;
        List<(int, long)> slots = new();
        lock (_gate)
        {
            foreach ((int slot, RemoteBotEngine r) in _remotes)
                slots.Add((slot, r.Snapshot.Memory.RssBytes));
        }
        int lights = Members.Count(m => m.Light is not null && m.Snapshot.Connection != ConnectionPhase.Offline);
        long total = Math.Max(0, main) + slots.Sum(s => Math.Max(0, s.Item2));
        return (main, slots, total, lights);
    }

    /// <summary>The army's lines for the persistent notification.</summary>
    public string? Summary()
    {
        List<ArmyMember> active = Members.Where(m => m.Snapshot.Connection != ConnectionPhase.Offline).ToList();
        if (active.Count == 0)
            return null;
        int online = active.Count(m => m.IsOnline);
        int scripts = active.Count(m => m.Snapshot.Script.IsBusy);
        return $"Army: {online}/{Members.Count} online · {scripts} script(s) · {MemoryAdmission.Mb(MemoryUse().TotalBytes)}";
    }

    private void HookEngine(IBotEngine engine)
    {
        engine.SnapshotChanged -= OnMemberSnapshot;
        engine.SnapshotChanged += OnMemberSnapshot;
    }

    private void OnMemberSnapshot(object? sender, EngineSnapshot e) => RaiseChanged();

    private void UpdateKeepAlive() =>
        _platform.KeepAlive(Members.Any(m => m.Light is not null && m.Snapshot.Connection != ConnectionPhase.Offline));

    private int _changedQueued;

    private void RaiseChanged()
    {
        // Coalesced: many members publish several snapshots a second.
        if (Interlocked.Exchange(ref _changedQueued, 1) != 0)
            return;
        _ = Task.Delay(200).ContinueWith(_ =>
        {
            Volatile.Write(ref _changedQueued, 0);
            Changed?.Invoke(this, EventArgs.Empty);
            try
            {
                _platform.PublishSummary(Summary());
            }
            catch (Exception)
            {
            }
        }, TaskScheduler.Default);
    }

    private ArmyResult Note(ArmyMember m, bool ok, string message)
    {
        m.Note = message;
        RaiseChanged();
        return new ArmyResult(m.Account.Label, ok, message);
    }

    private void RememberJump(string entry) =>
        Accounts.Update(s => s with { RecentJumps = new[] { entry }.Concat((s.RecentJumps ?? []).Where(j => j != entry)).Take(8).ToList() });

    private async Task<IReadOnlyList<ArmyResult>> ForEachAsync(IEnumerable<ArmyMember> members, Func<ArmyMember, Task<ArmyResult>> action, TimeSpan? spacing = null)
    {
        List<ArmyResult> results = new();
        bool first = true;
        foreach (ArmyMember m in members.ToList())
        {
            if (!first && spacing is TimeSpan gap)
                await Task.Delay(gap).ConfigureAwait(false);
            first = false;
            try
            {
                results.Add(await action(m).ConfigureAwait(false));
            }
            catch (EngineException e)
            {
                results.Add(Note(m, false, e.Message));
            }
        }
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        foreach (ArmyMember m in Members)
        {
            if (m.Light is LightAccount light)
                await light.DisposeAsync().ConfigureAwait(false);
        }
        List<RemoteBotEngine> remotes;
        lock (_gate)
            remotes = _remotes.Values.ToList();
        foreach (RemoteBotEngine r in remotes)
            await r.DisposeAsync().ConfigureAwait(false);
    }
}
