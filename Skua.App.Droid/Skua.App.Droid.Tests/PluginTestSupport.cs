using System.Reflection;
using Skua.App.Droid.Core.Control;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Plugins;
using Skua.Core.Headless;

namespace Skua.App.Droid.Tests;

/// <summary>A script host without scripts that hands the engine plugin backends (fakes or real ones).</summary>
internal sealed class PluginOnlyScriptHost : IScriptHost, IPluginBackendSource
{
    private readonly UnavailableScriptHost _inner = new();

    public PluginOnlyScriptHost(IPluginBackends backends)
    {
        PluginBackends = backends;
    }

    public IPluginBackends? PluginBackends { get; }

    public ScriptSupport Support => _inner.Support;

    public ScriptStatus Status => _inner.Status;

    public int LoadedAssemblyCount => 0;

    public event EventHandler<ScriptStatus>? StatusChanged
    {
        add => _inner.StatusChanged += value;
        remove => _inner.StatusChanged -= value;
    }

    public event EventHandler<LogEntry>? Log
    {
        add { }
        remove { }
    }

    public void Attach(AqwSession session)
    {
    }

    public void Load(string scriptPath) => _inner.Load(scriptPath);

    public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakePluginBackends : IPluginBackends
{
    public FakeDailyTracker FakeDailyTracker { get; } = new();

    public IDailyTrackerBackend? DailyTracker => FakeDailyTracker;

    public FakeButler FakeButler { get; } = new();

    public IButlerBackend? Butler => FakeButler;

    public FakeControlBackend FakeControl { get; } = new();

    public IControlBackend? Control => FakeControl;

    public FakeGameTools FakeTools { get; } = new();

    public Skua.App.Droid.Core.Tools.IGameTools? GameTools => FakeTools;
}

internal sealed class FakeButler : IButlerBackend
{
    private readonly List<ButlerLogLine> _log = new();

    public ButlerOptions Options { get; set; } = new();

    public ButlerStatus Status { get; private set; } = ButlerStatus.Idle;

    public List<string> Calls { get; } = new();

    public event EventHandler<ButlerStatus>? StatusChanged;

    public event EventHandler? LogChanged;

    public IReadOnlyList<ButlerLogLine> GetLog()
    {
        lock (_log)
            return _log.ToList();
    }

    public void AddLog(string text)
    {
        lock (_log)
            _log.Add(new ButlerLogLine(_log.Count + 1, DateTimeOffset.UtcNow, ButlerLogLevel.Info, "ButlerV5", text, 1));
        LogChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearLog()
    {
        lock (_log)
            _log.Clear();
        LogChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StartFollow(string player)
    {
        Calls.Add($"follow {player}");
        if (player.Trim() == "me")
            throw new EngineException("That is this account. Enter another player's name.");
        SetStatus(new ButlerStatus(player.Trim(), ButlerFollowState.Traveling, $"Going to {player.Trim()}.", 0));
    }

    public void StopFollow(bool park = false)
    {
        Calls.Add(park ? "stop park" : "stop");
        SetStatus(ButlerStatus.Idle);
    }

    public void GotoPlayer(string player) => Calls.Add($"goto {player}");

    public void JumpToPlayer(string player)
    {
        Calls.Add($"jump {player}");
        throw new EngineException($"{player} is not in this room. Use Go to first.");
    }

    public IReadOnlyList<string> PlayersInRoom() => ["Alpha", "Beta"];

    public void SetStatus(ButlerStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}

internal sealed class FakeDailyTracker : IDailyTrackerBackend
{
    public IReadOnlyList<DailyQuestGroupInfo> Catalog { get; } =
    [
        new("Resources", null, [new(1, "First", "Dailies/First.cs", false), new(2, "Second", null, false)]),
        new("Lord of Order", "Dailies/LordOfOrder.cs", [new(7156, "Heart of Servitude", "Dailies/LordOfOrder.cs", true)]),
    ];

    public Dictionary<int, bool?> States { get; } = new() { [1] = false, [2] = true, [7156] = false };

    public int Reads;

    public Func<Exception?> Failure { get; set; } = () => null;

    public event EventHandler<int>? QuestTurnedIn;

    public IReadOnlyDictionary<int, bool?> ReadStates(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Reads);
        if (Failure() is Exception e)
            throw e;
        lock (States)
            return new Dictionary<int, bool?>(States);
    }

    public void TurnIn(int questId) => QuestTurnedIn?.Invoke(this, questId);
}

/// <summary>
/// A stand-in for any Skua script interface (IScriptInterface and the interfaces it hands out): every call goes to
/// <see cref="Handler"/> with the method name; unhandled calls return the default value (or another fake for
/// interface-typed results, so <c>bot.Quests.Tree</c> style chains work).
/// </summary>
public class ScriptFake : DispatchProxy
{
    public Func<string, object?[], object?>? Handler { get; set; }

    public List<string> Calls { get; } = new();

    public static T Create<T>(Func<string, object?[], object?> handler) where T : class
    {
        T proxy = Create<T, ScriptFake>();
        ((ScriptFake)(object)proxy).Handler = handler;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        string name = targetMethod!.Name;
        lock (Calls)
            Calls.Add(name);
        const string NoAnswer = "\u0000none";
        object? result = Handler?.Invoke(name, args ?? []) ?? NoAnswer;
        Type type = targetMethod.ReturnType;
        if (!ReferenceEquals(result, NoAnswer))
        {
            // Flash answers are text; a typed call (CallGameFunction<int>) gets the value converted.
            Type plain = Nullable.GetUnderlyingType(type) ?? type;
            if (result is string text && plain != typeof(string) && plain != typeof(object) && plain.IsPrimitive)
                return Convert.ChangeType(text, plain, System.Globalization.CultureInfo.InvariantCulture);
            return result;
        }
        if (type == typeof(void))
            return null;
        if (type.IsInterface)
        {
            MethodInfo create = typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(Create) && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(type, typeof(ScriptFake));
            object nested = create.Invoke(null, null)!;
            ((ScriptFake)nested).Handler = Handler;
            return nested;
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>A Control page backend with a fixed little world; records every action and can fail or block on demand.</summary>
internal sealed class FakeControlBackend : IControlBackend
{
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public List<string> Calls { get; } = new();

    /// <summary>Thrown by the next action (then cleared).</summary>
    public Exception? Fail { get; set; }

    /// <summary>When set, actions wait for it.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public ControlState State { get; set; } = new(
        true, "testmap", "testmap-901", "Enter", "Spawn",
        [new("Enter", 0, 0, 1, true), new("r2", 2, 2, 0, false), new("r3", 0, 1, 0, false)],
        CellSource.RoomData,
        ["Spawn", "Left", "Right"],
        [new(1, "Pixie", 3, 50, 100, 2, true), new(2, "Pixie", 3, 0, 100, 0, false)],
        [new("OtherPlayer", 12, "Enter", "Spawn", 800, 800, 1, true)],
        [new("OtherPlayer", 12, "Enter", "Spawn", 800, 800, 1, true)],
        [new(11, "Pixie Wing", 1)],
        [new(1, "Heal"), new(2, "Smite"), new(5, "Potion")],
        "Pixie (#1)",
        true, true, 900, 1000, true, true, 1);

    public ControlShop? Shop { get; set; } = new(77, "Pixie Shop",
    [
        new(40, 9001, "Health Potion", 10, false, false, 1, 99, 50, [], null),
        new(41, 9002, "Fancy Hat", 1000, false, false, 1, 1, 0, [], "Not enough gold."),
        new(43, 9004, "Wing Charm", 0, false, false, 1, 10, 0, ["Missing 2 x Pixie Wing in inventory."], null),
    ]);

    public ControlQuest? Quest { get; set; } = new(1000, "Pixie Problem", "Bring dust.", true, false, false,
        ["Level 1"], null, [new(10, "Pixie Dust", 1, 3)], ["100 gold", "50 XP", "1 x Pixie Crown"], 0);

    public ControlBank Bank { get; set; } = new(true,
        [new(11, "Pixie Wing", 2, 99, "Item", "None", false, false, false)],
        [new(1, "Default Sword", 1, 1, "Sword", "Weapon", true, false, false), new(30, "Old Boots", 3, 5, "Misc", "None", false, false, false)],
        20, 40);

    public ControlState Read()
    {
        Interlocked.Increment(ref _reads);
        return State;
    }

    public ControlShop? ReadShop() => Shop;

    public ControlQuest? ReadQuest(int questId) => Quest?.QuestId == questId ? Quest : null;

    public ControlBank ReadBank() => Bank;

    private string Do(string call)
    {
        lock (Calls)
            Calls.Add(call);
        Gate?.Wait(TimeSpan.FromSeconds(10));
        if (Fail is Exception e)
        {
            Fail = null;
            throw e;
        }
        return $"done: {call}";
    }

    public string JoinMap(string map) => Do($"join {map}");

    public string Jump(string cell, string? pad) => Do($"jump {cell} {pad ?? "-"}");

    public string GotoPlayer(string name) => Do($"goto {name}");

    public string Attack(int monMapId) => Do($"attack {monMapId}");

    public string UseSkill(int index) => Do($"skill {index}");

    public string CancelTarget() => Do("cancel");

    public string Rest() => Do("rest");

    public string Resurrect() => Do("resurrect");

    public string PickupDrop(int itemId) => Do($"pickup {itemId}");

    public string RejectDrop(int itemId) => Do($"reject {itemId}");

    public string LoadShop(int shopId) => Do($"shop {shopId}");

    public string Buy(int itemId, int shopItemId, int quantity) => Do($"buy {itemId} {shopItemId} {quantity}");

    public string Sell(int itemId, int quantity) => Do($"sell {itemId} {quantity}");

    public string LoadQuest(int questId) => Do($"quest {questId}");

    public string AcceptQuest(int questId) => Do($"accept {questId}");

    public string TurnInQuest(int questId) => Do($"turnin {questId}");

    public string LoadBank() => Do("bank");

    public string BankToInventory(int itemId) => Do($"toinv {itemId}");

    public string InventoryToBank(int itemId) => Do($"tobank {itemId}");
}
