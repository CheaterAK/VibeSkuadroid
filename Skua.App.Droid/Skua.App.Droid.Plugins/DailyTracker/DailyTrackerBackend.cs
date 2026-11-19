using CommunityToolkit.Mvvm.Messaging;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Plugins;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Plugins.Shared.DailyTracker;

namespace Skua.App.Droid.Plugins.DailyTracker;

/// <summary>
/// The Daily &amp; Weekly Tracker on the engine's Skua session: the desktop plugin's catalog and status checks
/// (<see cref="DailyQuestCatalog"/>, <see cref="DailyQuestStatusReader"/>, shared source), without its window.
/// </summary>
public sealed class DailyTrackerBackend : IDailyTrackerBackend, IDisposable
{
    private readonly Func<IScriptInterface?> _bot;
    private readonly object _recipient = new();
    private readonly object _readGate = new();
    private EventHandler<int>? _questTurnedIn;
    private bool _registered;

    /// <param name="bot">The session's script interface (null while it does not exist).</param>
    public DailyTrackerBackend(Func<IScriptInterface?> bot)
    {
        _bot = bot;
        Catalog = BuildCatalog();
    }

    public IReadOnlyList<DailyQuestGroupInfo> Catalog { get; }

    /// <summary>Raised from Skua's QuestTurninMessage (the server's successful "ccqr"), like the desktop window's handler.</summary>
    public event EventHandler<int>? QuestTurnedIn
    {
        add
        {
            lock (_recipient)
            {
                _questTurnedIn += value;
                if (!_registered)
                {
                    StrongReferenceMessenger.Default.Register<object, QuestTurninMessage, int>(_recipient, (int)MessageChannels.GameEvents,
                        (_, m) => _questTurnedIn?.Invoke(this, m.QuestID));
                    _registered = true;
                }
            }
        }
        remove
        {
            lock (_recipient)
            {
                _questTurnedIn -= value;
                if (_questTurnedIn is null && _registered)
                {
                    StrongReferenceMessenger.Default.UnregisterAll(_recipient);
                    _registered = false;
                }
            }
        }
    }

    public IReadOnlyDictionary<int, bool?> ReadStates(CancellationToken cancellationToken = default)
    {
        IScriptInterface bot = _bot() ?? throw new EngineException("The game is not available (no Skua session).");
        if (bot.Player?.LoggedIn != true)
            throw new EngineException("Log in first.");

        // One read at a time (a refresh while another runs would only send the same batch again).
        lock (_readGate)
        {
            DailyQuestStatusReader.EnsureAllQuestsLoaded(bot);
            Dictionary<int, bool?> states = new();
            foreach (int id in DailyQuestCatalog.TrackedQuests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                states[id] = DailyQuestStatusReader.TryReadState(bot, id, out bool done, out _) ? done : null;
            }
            return states;
        }
    }

    private static IReadOnlyList<DailyQuestGroupInfo> BuildCatalog()
    {
        static string? Portable(string? script) => script?.Replace('\\', '/');

        List<DailyQuestGroupInfo> groups = new();
        foreach ((string name, int start, int count) in DailyQuestCatalog.Categories)
        {
            List<DailyQuestInfo> quests = new();
            for (int i = start; i < start + count; i++)
            {
                int id = DailyQuestCatalog.TrackedQuests[i];
                quests.Add(new DailyQuestInfo(id, DailyQuestCatalog.QuestNames[i], Portable(DailyQuestCatalog.ScriptFor(id)), DailyQuestCatalog.IsLordOfOrder(id)));
            }
            groups.Add(new DailyQuestGroupInfo(name, Portable(DailyQuestCatalog.CategoryScript(name)), quests));
        }
        return groups;
    }

    public void Dispose()
    {
        lock (_recipient)
        {
            _questTurnedIn = null;
            if (_registered)
                StrongReferenceMessenger.Default.UnregisterAll(_recipient);
            _registered = false;
        }
    }
}
