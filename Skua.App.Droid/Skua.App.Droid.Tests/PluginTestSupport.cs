using System.Reflection;
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
