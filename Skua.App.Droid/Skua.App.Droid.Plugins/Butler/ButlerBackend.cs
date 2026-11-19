using Microsoft.Extensions.DependencyInjection;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Plugins;
using Skua.Core.Interfaces;
using Skua.Core.Models.Players;

namespace Skua.App.Droid.Plugins.Butler;

/// <summary>
/// ButlerV5 for one account on the engine's Skua session: follow a player by name, send this account to a player
/// (once), jump to a player in this room, and the plugin's log.
/// </summary>
/// <remarks>
/// Army work (several local accounts) would add, next to this: one Skua session per account in the process, the
/// desktop's sync file format (Skua.Plugin.ButlerV5/SyncFile.cs, UI-free) in app-private storage, its Broadcaster
/// and order watcher per session, and the desktop Follower (which needs the master's sync file) for followers whose
/// master is local. <see cref="PlayerFollower"/> stays the follower for players outside the army.
/// </remarks>
public sealed class ButlerBackend : IButlerBackend, IDisposable
{
    private readonly Func<IScriptInterface?> _bot;
    private readonly ButlerLog _log = new();
    private readonly PlayerFollower _follower;
    private readonly object _gate = new();
    // Start/stop are serialised on their own lock: the follow loop reports its state under _gate, and Stop waits for
    // that loop, so holding _gate there would stall both.
    private readonly object _followGate = new();
    private ButlerOptions _options = new();
    private ButlerStatus _status = ButlerStatus.Idle;

    /// <param name="bot">The session's script interface (null while it does not exist).</param>
    /// <param name="services">The session's Skua services (skill sets), or null.</param>
    public ButlerBackend(Func<IScriptInterface?> bot, Func<IServiceProvider?> services)
    {
        _bot = bot;
        _log.DebugEnabled = () => Options.DebugLog;
        _log.EventSink = text =>
        {
            // Skua's script log (the app's Console), as the desktop plugin's "[ButlerV5] ..." lines.
            if (_bot() is { } b)
                b.Log($"[ButlerV5] {text}");
        };
        _log.Changed += (_, e) => LogChanged?.Invoke(this, e);
        _follower = new PlayerFollower(() => Options, _log, SetStatus,
            () => services()?.GetService<IAdvancedSkillContainer>()?.LoadedSkills);
    }

    public ButlerOptions Options
    {
        get => Volatile.Read(ref _options);
        set => Volatile.Write(ref _options, (value ?? new ButlerOptions()).Normalized());
    }

    public ButlerStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public event EventHandler<ButlerStatus>? StatusChanged;

    public event EventHandler? LogChanged;

    public IReadOnlyList<ButlerLogLine> GetLog() => _log.Snapshot();

    public void ClearLog() => _log.Clear();

    public void StartFollow(string player)
    {
        string name = player?.Trim() ?? string.Empty;
        IScriptInterface bot = Ready(name);
        if (bot.Manager.ScriptRunning)
            throw new EngineException("Can't start following: a script is running on this account. Stop it first.");
        if (name.Equals(bot.Player.Username, StringComparison.OrdinalIgnoreCase))
            throw new EngineException("That is this account. Enter another player's name.");
        lock (_followGate)
            _follower.Start(bot, name);
    }

    public void StopFollow(bool park = false)
    {
        lock (_followGate)
            _follower.Stop(park);
    }

    public void GotoPlayer(string player)
    {
        string name = player?.Trim() ?? string.Empty;
        IScriptInterface bot = Ready(name);
        if (bot.Manager.ScriptRunning)
            throw new EngineException("A script is running on this account; it decides where the character goes.");
        if (bot.Player.InCombat)
            _log.Event("In combat: the game only sends you once out of combat.", ButlerLogLevel.Warning);
        bot.Player.Goto(name);
        _log.Event($"Sent this account to {name}.");
    }

    public void JumpToPlayer(string player)
    {
        string name = player?.Trim() ?? string.Empty;
        IScriptInterface bot = Ready(name);
        if (!bot.Map.TryGetPlayer(name, out PlayerInfo? info) || string.IsNullOrEmpty(info?.Cell))
            throw new EngineException($"{name} is not in this room. Use Go to first.");
        bot.Map.Jump(info.Cell, string.IsNullOrEmpty(info.Pad) ? "Spawn" : info.Pad, autoCorrect: false);
        _log.Event($"Jumped to {name} ({info.Cell}).");
    }

    public IReadOnlyList<string> PlayersInRoom()
    {
        IScriptInterface? bot = _bot();
        if (bot?.Player?.LoggedIn != true)
            return [];
        try
        {
            string me = bot.Player.Username ?? string.Empty;
            return (bot.Map.PlayerNames ?? [])
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals(me, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private IScriptInterface Ready(string name)
    {
        if (name.Length == 0)
            throw new EngineException("Enter a player name first.");
        IScriptInterface bot = _bot() ?? throw new EngineException("The game is not available (no Skua session).");
        if (bot.Player?.LoggedIn != true)
            throw new EngineException("Log in first.");
        return bot;
    }

    private void SetStatus(ButlerStatus status)
    {
        bool changed;
        lock (_gate)
        {
            changed = status != _status;
            _status = status;
        }
        if (changed)
            StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        StopFollow();
    }
}
