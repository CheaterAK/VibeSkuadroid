namespace Skua.App.Droid.Core.Plugins;

/// <summary>
/// The game-side halves of the built-in plugins: what they do through Skua's script API on the engine's one session.
/// Implemented in Skua.App.Droid.Plugins (on <c>IScriptInterface</c>); the ViewModels only see these interfaces and
/// plain records, as they only see <see cref="Engine.IBotEngine"/> for the engine.
/// </summary>
/// <remarks>Null members: that plugin cannot run in this engine (no Skua host).</remarks>
public interface IPluginBackends
{
    IDailyTrackerBackend? DailyTracker { get; }

    IButlerBackend? Butler { get; }
}

/// <summary>Implemented by a script host that can provide <see cref="IPluginBackends"/> (the Skua.Core host adapter).</summary>
public interface IPluginBackendSource
{
    /// <summary>Null until the host's session exists, or when it could not start.</summary>
    IPluginBackends? PluginBackends { get; }
}

/// <summary>One tracked quest of the Daily &amp; Weekly Tracker.</summary>
/// <param name="Id">Quest ID.</param>
/// <param name="Name">Display name.</param>
/// <param name="Script">The community script that does it (relative to the script folder, '/' separators), or null.</param>
/// <param name="IsLordOfOrder">A step of the Lord of Order chain (started from its category, not on its own).</param>
public sealed record DailyQuestInfo(int Id, string Name, string? Script, bool IsLordOfOrder);

/// <summary>A category of tracked quests.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Script">The script the category's own Start button runs, or null.</param>
/// <param name="Quests">Its quests, in display order.</param>
public sealed record DailyQuestGroupInfo(string Name, string? Script, IReadOnlyList<DailyQuestInfo> Quests);

/// <summary>Daily &amp; Weekly Tracker: daily and weekly quest status and ultra boss lockouts of the logged-in character.</summary>
public interface IDailyTrackerBackend
{
    /// <summary>What is tracked (static).</summary>
    IReadOnlyList<DailyQuestGroupInfo> Catalog { get; }

    /// <summary>
    /// Batch-loads every tracked quest the client does not know yet, then reads whether each is done today. Blocking
    /// (seconds); call it off the UI thread. A null value: that quest's check failed (keep what was shown).
    /// </summary>
    /// <exception cref="Engine.EngineException">The game is not available.</exception>
    IReadOnlyDictionary<int, bool?> ReadStates(CancellationToken cancellationToken = default);

    /// <summary>The server confirmed a quest turn-in (any quest; background thread).</summary>
    event EventHandler<int>? QuestTurnedIn;
}
