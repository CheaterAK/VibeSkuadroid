using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Security;
using Skua.Core.Headless;
using Skua.Core.Headless.Client;
using Skua.Core.Headless.Net;
using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Rules;
using Skua.Core.Headless.World;

namespace Skua.App.Droid.Core.Army;

/// <summary>Where a follow target is, as the follower knows it.</summary>
/// <param name="Name">The character name.</param>
/// <param name="Map">Its map name when known (a local account), else null.</param>
/// <param name="Cell">Its cell when known.</param>
/// <param name="Pad">Its pad when known.</param>
public sealed record FollowTargetPosition(string Name, string? Map, string? Cell, string? Pad);

/// <summary>What the follower should do this tick.</summary>
public enum FollowAction
{
    None,

    /// <summary>/goto the target (another room or map).</summary>
    Goto,

    /// <summary>Join the target's map by name (goto failed repeatedly, e.g. the target is in a private room).</summary>
    JoinMap,

    /// <summary>Same room, other cell: move to the target's cell and pad.</summary>
    MoveToCell,
}

/// <summary>The follow decision, pure (tested without a server).</summary>
public static class LightFollow
{
    /// <summary>Between two /goto: the Control page's spacing (3 s).</summary>
    public static readonly TimeSpan GotoInterval = TimeSpan.FromSeconds(3);

    /// <summary>Between two cell moves (0.5 s).</summary>
    public static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>After this many /goto that did not bring the follower to the target, join the target's map instead (when known).</summary>
    public const int GotoAttemptsBeforeJoin = 3;

    /// <param name="target">The target.</param>
    /// <param name="inRoom">The target as seen in the follower's room (null: not here).</param>
    /// <param name="myMap">The follower's map.</param>
    /// <param name="myCell">The follower's cell.</param>
    /// <param name="sinceGoto">Time since the last /goto.</param>
    /// <param name="sinceMove">Time since the last cell move.</param>
    /// <param name="failedGotos">/goto sent since the follower last saw the target.</param>
    public static (FollowAction Action, string? Cell, string? Pad) Decide(FollowTargetPosition target, AreaUserInfo? inRoom, string myMap, string myCell,
        TimeSpan sinceGoto, TimeSpan sinceMove, int failedGotos)
    {
        if (inRoom is null)
        {
            if (sinceGoto < GotoInterval)
                return (FollowAction.None, null, null);
            if (failedGotos >= GotoAttemptsBeforeJoin && target.Map is { Length: > 0 } map && !string.Equals(map, myMap, StringComparison.OrdinalIgnoreCase))
                return (FollowAction.JoinMap, target.Cell, target.Pad);
            return (FollowAction.Goto, null, null);
        }
        string cell = string.IsNullOrEmpty(inRoom.Cell) ? target.Cell ?? "Enter" : inRoom.Cell;
        string pad = string.IsNullOrEmpty(inRoom.Pad) ? target.Pad ?? "Spawn" : inRoom.Pad;
        if (string.Equals(cell, myCell, StringComparison.OrdinalIgnoreCase) || sinceMove < MoveInterval)
            return (FollowAction.None, null, null);
        return (FollowAction.MoveToCell, cell, pad);
    }
}

/// <summary>Fight settings of a light account.</summary>
/// <param name="Monsters">Monster names to attack ("*" or empty: any in the cell), '|'-separated like Skua's.</param>
/// <param name="Rotation">The skill rotation.</param>
/// <param name="RestBelowPercent">Rest when out of combat below this HP (0: never).</param>
public sealed record LightFight(string Monsters, LightRotation Rotation, int RestBelowPercent = 50)
{
    public bool Accepts(string name)
    {
        if (string.IsNullOrWhiteSpace(Monsters) || Monsters.Trim() == "*")
            return true;
        return Monsters.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A quest loop: accept, turn in when completable, up to a number of turn-ins.</summary>
public sealed record LightQuest(int QuestId, int MaxTurnIns)
{
    public int TurnIns { get; init; }

    public bool Done => MaxTurnIns > 0 && TurnIns >= MaxTurnIns;
}

/// <summary>Drop rules: pick up the whitelist ("*" = everything not junk), reject the rest when asked, always reject junk.</summary>
public sealed record LightPickup(IReadOnlyList<string> Whitelist, IReadOnlyList<string> Junk, bool RejectOthers)
{
    public static LightPickup None { get; } = new([], [], false);

    public bool Wants(string name) =>
        !Junk.Any(j => string.Equals(j, name, StringComparison.OrdinalIgnoreCase))
        && Whitelist.Any(w => w == "*" || string.Equals(w, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Settings of a light session.</summary>
public sealed record LightOptions
{
    /// <summary>Creates the protocol session (tests: a fake web API; the app: the defaults).</summary>
    public Func<AqwSession>? SessionFactory { get; init; }

    /// <summary>The army's login turn, taken before every relogin.</summary>
    public LoginStagger? Stagger { get; init; }

    public TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Console lines kept per light account (the main engine keeps 2000).</summary>
    public int LogCapacity { get; init; } = 300;

    public IReadOnlyList<TimeSpan>? ReconnectDelays { get; init; }

    public TimeProvider? Time { get; init; }
}

/// <summary>
/// A light army account: one <see cref="AqwSession"/> (socket, world model) and the game's client rules
/// (<see cref="GameClient"/>) inside the main process, with no Skua.Core runtime, no Roslyn and no scripts. Many can
/// live in one process (Skua.Core.Headless keeps no per-process state but the socket buffer canaries' locked
/// diagnostics). Login, reconnect (through the army's login stagger), snapshots and the log come from a
/// <see cref="HeadlessBotEngine"/> without a script host; behaviours run natively on the client on one tick loop:
/// follow a master, fight with a rotation, rest and resurrect, pick up drops, a quest loop.
/// </summary>
public sealed class LightAccount : IAsyncDisposable
{
    private readonly LightClientHost _client;
    private readonly LightOptions _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SecretScrubber _secrets = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private FollowTargetPosition? _followName;
    private Func<FollowTargetPosition?>? _followSource;
    private LightFight? _fight;
    private LightQuest? _quest;
    private LightPickup _pickup = LightPickup.None;
    private string _status = "Idle";
    private DateTimeOffset _lastGoto = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMove = DateTimeOffset.MinValue;
    private DateTimeOffset _lastResurrect = DateTimeOffset.MinValue;
    private DateTimeOffset _lastQuestAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPickup = DateTimeOffset.MinValue;
    private int _failedGotos;
    private int _rotationPos;
    private DateTimeOffset _waitingSince = DateTimeOffset.MinValue;
    private bool _questTurnInPending;
    private readonly HashSet<string> _unsupportedReported = new(StringComparer.Ordinal);

    public LightAccount(ArmyAccount account, LightOptions? options = null)
    {
        Account = account;
        _options = options ?? new LightOptions();
        _time = _options.Time ?? TimeProvider.System;
        _client = new LightClientHost(_time);
        HeadlessEngineOptions engineOptions = new()
        {
            SessionFactory = _options.SessionFactory ?? (() => new AqwSession()),
            // Its own scrubber: a light logout must not clear the other accounts' secrets. Linked into the process-wide
            // diagnostics registry (crash and exit reports, canary samples, the bundle) until this account is disposed.
            Scrubber = _secrets,
            LogCapacity = _options.LogCapacity,
            PacketCapacity = 1,
            PublishInterval = TimeSpan.FromMilliseconds(500),
            MemorySampleInterval = TimeSpan.FromSeconds(15),
            LoginGate = _options.Stagger is { } stagger ? ct => stagger.EnterAsync(ct) : null,
            ReconnectDelays = _options.ReconnectDelays ?? new HeadlessEngineOptions().ReconnectDelays,
        };
        Engine = new HeadlessBotEngine(engineOptions, _client, time: _options.Time);
        Diagnostics.DiagnosticText.Secrets.Link(_secrets);
        _loop = Task.Run(LoopAsync);
    }

    public ArmyAccount Account { get; private set; }

    /// <summary>The account's engine (login, reconnect, snapshots, log). Scripts are refused: light accounts run native tasks.</summary>
    public HeadlessBotEngine Engine { get; }

    /// <summary>The client rules of the current session.</summary>
    public GameClient? Client => _client.Client;

    public WorldModel World => Engine.Session.World;

    /// <summary>What the behaviours are doing ("Following Master", "Fighting in r2", "Quest 1000: 3/10").</summary>
    public string Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public LightFight? Fight
    {
        get { lock (_gate) return _fight; }
    }

    public LightQuest? Quest
    {
        get { lock (_gate) return _quest; }
    }

    public string? FollowingName
    {
        get { lock (_gate) return (_followSource?.Invoke() ?? _followName)?.Name; }
    }

    public void Rename(ArmyAccount account) => Account = account;

    public Task LoginAsync(string password, string? server, CancellationToken cancellationToken = default) =>
        Engine.LoginAsync(new LoginRequest(Account.Username, password, server ?? Account.Server), cancellationToken);

    /// <summary>Stops every behaviour and logs out.</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        StopAll();
        await Engine.LogoutAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Follows a player by name (anyone; /goto then their cell).</summary>
    public void FollowPlayer(string name)
    {
        string checkedName = ArmyNames.RequirePlayer(name);
        lock (_gate)
        {
            _followName = new FollowTargetPosition(checkedName, null, null, null);
            _followSource = null;
            _failedGotos = 0;
        }
    }

    /// <summary>Follows a local account (its map, cell and pad are known exactly).</summary>
    public void FollowLocal(Func<FollowTargetPosition?> master)
    {
        lock (_gate)
        {
            _followSource = master;
            _followName = null;
            _failedGotos = 0;
        }
    }

    public void StopFollowing()
    {
        lock (_gate)
        {
            _followName = null;
            _followSource = null;
        }
    }

    public void StartFight(LightFight fight)
    {
        lock (_gate)
        {
            _fight = fight;
            _rotationPos = 0;
        }
    }

    public void StopFight()
    {
        lock (_gate)
            _fight = null;
        Client?.CancelAutoAttack();
    }

    public void StartQuest(int questId, int maxTurnIns)
    {
        if (questId <= 0)
            throw new EngineException("Enter a quest ID.");
        lock (_gate)
        {
            _quest = new LightQuest(questId, Math.Max(0, maxTurnIns));
            _questTurnInPending = false;
        }
    }

    public void StopQuest()
    {
        lock (_gate)
            _quest = null;
    }

    public void SetPickup(LightPickup pickup)
    {
        lock (_gate)
            _pickup = pickup;
    }

    /// <summary>Stops follow, fight and quest (Stop All).</summary>
    public void StopAll()
    {
        StopFollowing();
        StopFight();
        StopQuest();
        SetStatus("Idle");
    }

    /// <summary>Jumps to a cell and pad of the current map.</summary>
    public void Jump(string cell, string pad)
    {
        GameClient client = Client ?? throw new EngineException($"{Account.Label} is not online.");
        client.JumpCorrectRoom(ArmyNames.RequireCell(cell), ArmyNames.RequireCell(pad));
    }

    /// <summary>/goto a player once.</summary>
    public void GotoPlayer(string name)
    {
        GameClient client = Client ?? throw new EngineException($"{Account.Label} is not online.");
        client.GoTo(ArmyNames.RequirePlayer(name));
    }

    /// <summary>Accepts quests (quest sync from the master).</summary>
    public int AcceptQuests(IEnumerable<int> questIds)
    {
        GameClient? client = Client;
        if (client is null || !Engine.Snapshot.IsOnline)
            return 0;
        int n = 0;
        foreach (int id in questIds.Where(i => i > 0).Distinct().Take(30))
        {
            if (!client.IsQuestInProgress(id))
            {
                client.AcceptQuest(id);
                n++;
            }
        }
        return n;
    }

    private void SetStatus(string text)
    {
        lock (_gate)
            _status = text;
    }

    private async Task LoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.Tick, _time, _lifetime.Token).ConfigureAwait(false);
                Tick();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is InvalidOperationException or EngineException or ArgumentException or KeyNotFoundException or NullReferenceException)
            {
                // One bad tick (a world read racing a map change) never ends the loop.
                Engine.WriteLog(EngineLogLevel.Debug, "light", $"tick: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>One step of every behaviour (public for tests).</summary>
    public void Tick()
    {
        GameClient? client = Client;
        if (client is null || !Engine.Snapshot.IsOnline || client.MapLoadInProgress)
            return;
        WorldModel world = World;
        if (world.GetPlayer() is not PlayerInfo me || !me.CharacterLoaded)
            return;
        DateTimeOffset now = _time.GetUtcNow();
        FollowTargetPosition? follow;
        LightFight? fight;
        LightQuest? quest;
        LightPickup pickup;
        lock (_gate)
        {
            follow = _followSource?.Invoke() ?? _followName;
            fight = _fight;
            quest = _quest;
            pickup = _pickup;
        }

        if (!me.IsAlive)
        {
            if (now - _lastResurrect > TimeSpan.FromSeconds(2))
            {
                _lastResurrect = now;
                client.Resurrect();
            }
            SetStatus("Dead, resurrecting");
            return;
        }

        List<string> parts = new();
        if (follow is not null && !string.Equals(follow.Name, me.Username, StringComparison.OrdinalIgnoreCase))
            parts.Add(FollowStep(client, world, follow, now));
        if (pickup.Whitelist.Count > 0 || pickup.Junk.Count > 0 || pickup.RejectOthers)
            PickupStep(client, world, pickup, now);
        if (quest is not null)
            parts.Add(QuestStep(client, world, quest, now));
        if (fight is not null)
            parts.Add(FightStep(client, world, me, fight, now));
        SetStatus(parts.Count == 0 ? "Idle" : string.Join(" · ", parts.Where(p => p.Length > 0)));
    }

    private string FollowStep(GameClient client, WorldModel world, FollowTargetPosition target, DateTimeOffset now)
    {
        AreaInfo area = world.GetArea();
        AreaUserInfo? there = world.GetAreaUsers().FirstOrDefault(u => !u.IsMe && string.Equals(u.Name, target.Name, StringComparison.OrdinalIgnoreCase));
        if (there is not null)
            _failedGotos = 0;
        (FollowAction action, string? cell, string? pad) = LightFollow.Decide(target, there, area.MapName, area.Cell, now - _lastGoto, now - _lastMove, _failedGotos);
        switch (action)
        {
            case FollowAction.Goto:
                _lastGoto = now;
                _failedGotos++;
                client.GoTo(target.Name);
                return $"Following {target.Name} (goto)";
            case FollowAction.JoinMap:
                _lastGoto = now;
                _failedGotos = 0;
                _ = JoinQuietlyAsync(target.Map!, cell ?? "Enter", pad ?? "Spawn");
                return $"Following {target.Name} (joining {target.Map})";
            case FollowAction.MoveToCell:
                _lastMove = now;
                client.JumpCorrectRoom(cell!, pad!);
                return $"Following {target.Name} to {cell}";
            default:
                return $"Following {target.Name}";
        }
    }

    private async Task JoinQuietlyAsync(string map, string cell, string pad)
    {
        try
        {
            await Engine.JoinMapAsync(map, cell, pad).ConfigureAwait(false);
        }
        catch (EngineException e)
        {
            Engine.WriteLog(EngineLogLevel.Warning, "light", $"Join {map}: {e.Message}");
        }
    }

    private void PickupStep(GameClient client, WorldModel world, LightPickup pickup, DateTimeOffset now)
    {
        IReadOnlyList<DropInfo> drops = world.GetDrops();
        if (drops.Count == 0)
            return;
        DropInfo? wanted = drops.FirstOrDefault(d => pickup.Wants(d.Name));
        if (wanted is not null && now - _lastPickup > TimeSpan.FromMilliseconds(600))
        {
            _lastPickup = now;
            _client.Send(Skua.Core.Headless.Protocol.Packets.GetDrop(world.RoomId, wanted.ItemId));
        }
        bool hasUnwanted = drops.Any(d => !pickup.Wants(d.Name));
        if (hasUnwanted && (pickup.RejectOthers || pickup.Junk.Count > 0))
        {
            IEnumerable<string> keep = drops.Where(d => pickup.Wants(d.Name) || (!pickup.RejectOthers && !pickup.Junk.Contains(d.Name, StringComparer.OrdinalIgnoreCase))).Select(d => d.Name);
            world.RejectDropsExcept(keep.ToList());
        }
    }

    private string QuestStep(GameClient client, WorldModel world, LightQuest quest, DateTimeOffset now)
    {
        if (quest.Done)
            return $"Quest {quest.QuestId}: done {quest.TurnIns}/{quest.MaxTurnIns}";
        QuestInfo? info = world.GetQuest(quest.QuestId);
        bool accepted = client.IsQuestInProgress(quest.QuestId);
        string count = quest.MaxTurnIns > 0 ? $"{quest.TurnIns}/{quest.MaxTurnIns}" : $"{quest.TurnIns}";
        if (_questTurnInPending && !accepted)
        {
            // The turn-in went through (the server dropped the quest from the accepted list).
            _questTurnInPending = false;
            lock (_gate)
            {
                if (_quest?.QuestId == quest.QuestId)
                    _quest = quest = _quest with { TurnIns = _quest.TurnIns + 1 };
            }
            count = quest.MaxTurnIns > 0 ? $"{quest.TurnIns}/{quest.MaxTurnIns}" : $"{quest.TurnIns}";
            if (quest.Done)
                return $"Quest {quest.QuestId}: done {count}";
        }
        if (now - _lastQuestAction < TimeSpan.FromSeconds(1.5))
            return $"Quest {quest.QuestId}: {count}";
        if (info is not { Loaded: true })
        {
            _lastQuestAction = now;
            client.ShowQuests(quest.QuestId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return $"Quest {quest.QuestId}: loading";
        }
        if (!accepted)
        {
            _lastQuestAction = now;
            client.AcceptQuest(quest.QuestId);
            return $"Quest {quest.QuestId}: accepting ({count})";
        }
        if (info.Raw is { } raw && QuestRules.IsCompletable(raw, client.Character(), now))
        {
            _lastQuestAction = now;
            _questTurnInPending = true;
            client.TryQuestComplete(quest.QuestId);
            return $"Quest {quest.QuestId}: turning in ({count})";
        }
        return $"Quest {quest.QuestId}: {count}";
    }

    private string FightStep(GameClient client, WorldModel world, PlayerInfo me, LightFight fight, DateTimeOffset now)
    {
        string cell = world.GetArea().Cell;
        List<MonsterInfo> here = world.GetMonsters().Where(m => m.IsAlive && fight.Accepts(m.Name)
            && (m.Live is null ? m.Cell == cell : string.Equals(Skua.Core.Headless.JsonUtil.AsString(m.Live["strFrame"]), cell, StringComparison.Ordinal))).ToList();
        if (here.Count == 0)
        {
            if (!me.InCombat && fight.RestBelowPercent > 0 && me.HpMax > 0 && me.Hp * 100 < me.HpMax * fight.RestBelowPercent)
            {
                client.Rest();
                return "Resting";
            }
            return $"No monsters in {cell}";
        }
        TargetRef? target = client.Target;
        bool targetAlive = target is { Kind: 'm' } && here.Any(m => m.MonMapId == target.Id);
        if (!targetAlive)
        {
            client.AttackMonster(here[0].MonMapId);
            _rotationPos = 0;
        }
        UseRotation(client, me, fight.Rotation, now);
        return $"Fighting in {cell}";
    }

    private void UseRotation(GameClient client, PlayerInfo me, LightRotation rotation, DateTimeOffset now)
    {
        if (rotation.Steps.Count == 0)
            return;
        for (int tries = 0; tries < rotation.Steps.Count; tries++)
        {
            LightSkillStep step = rotation.Steps[_rotationPos % rotation.Steps.Count];
            foreach (string rule in step.Unsupported)
            {
                if (_unsupportedReported.Add(rule))
                    Engine.WriteLog(EngineLogLevel.Info, "light", $"{rule} rules are not judged by light accounts (treated as met).");
            }
            if (!LightRotation.Allows(step, me.Hp, me.HpMax, me.Mp, me.MpMax))
            {
                _rotationPos++;
                continue;
            }
            if (client.CanUseSkill(step.Index))
            {
                client.UseSkill(step.Index);
                _rotationPos++;
                _waitingSince = DateTimeOffset.MinValue;
                return;
            }
            bool wait = rotation.WaitForCooldown || step.WaitMs > 0;
            if (wait)
            {
                if (_waitingSince == DateTimeOffset.MinValue)
                    _waitingSince = now;
                int limit = step.WaitMs > 0 ? step.WaitMs : Math.Max(rotation.TimeoutMs, 100);
                if ((now - _waitingSince).TotalMilliseconds < limit)
                    return;
                _waitingSince = DateTimeOffset.MinValue;
            }
            _rotationPos++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await Engine.DisposeAsync().ConfigureAwait(false);
        _secrets.Clear();
        Diagnostics.DiagnosticText.Secrets.Unlink(_secrets);
        _lifetime.Dispose();
    }
}

/// <summary>
/// The light account's "script host": not one. It only gives every session of the engine its <see cref="GameClient"/>
/// (the client rules the behaviours drive) and refuses scripts.
/// </summary>
internal sealed class LightClientHost : IScriptHost, IPacketSender
{
    public const string NoScripts = "Light accounts run native tasks (follow, fight, drops, quests), not scripts. Switch the account to a full slot to run scripts.";

    private readonly TimeProvider _time;
    private AqwSession? _session;
    private GameClient? _client;

    public LightClientHost(TimeProvider time)
    {
        _time = time;
    }

    public GameClient? Client => Volatile.Read(ref _client);

    public ScriptSupport Support { get; } = new(false, NoScripts);

    public ScriptStatus Status => ScriptStatus.Idle;

    public int LoadedAssemblyCount => 0;

    public event EventHandler<ScriptStatus>? StatusChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<LogEntry>? Log
    {
        add { }
        remove { }
    }

    public void Attach(AqwSession session)
    {
        if (_session is not null)
        {
            _session.PacketReceived -= OnInbound;
            _session.PacketSent -= OnOutbound;
            _session.Disconnected -= OnDisconnected;
        }
        GameClient? old = _client;
        _session = session;
        // No map file downloads: cell jumps use the pads the commands name (the light account's memory stays small).
        Volatile.Write(ref _client, new GameClient(session.World, this, _time));
        session.PacketReceived += OnInbound;
        session.PacketSent += OnOutbound;
        session.Disconnected += OnDisconnected;
        old?.Dispose();
    }

    private void OnInbound(object? sender, InboundPacket packet) => _client?.OnInbound(packet);

    private void OnOutbound(object? sender, PacketSentEventArgs e) => _client?.OnOutbound(e.Text);

    private void OnDisconnected(object? sender, DisconnectInfo info) => _client?.CancelAutoAttack();

    public bool Send(string packet)
    {
        AqwConnection? c = _session?.Connection;
        if (c is null || c.State == ConnectionState.Closed)
            return false;
        if (packet.StartsWith("%xt%zm%loadBank%", StringComparison.Ordinal))
            return false; // light accounts never load the bank
        _ = c.SendAsync(packet);
        return true;
    }

    public void Load(string scriptPath)
    {
    }

    public Task StartAsync(CancellationToken cancellationToken) => throw new EngineException(NoScripts);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        return ValueTask.CompletedTask;
    }
}
