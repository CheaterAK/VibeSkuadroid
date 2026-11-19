using Microsoft.Extensions.DependencyInjection;
using Skua.App.Droid.Core.Control;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Tools;
using Skua.Core.Interfaces;
using Skua.Core.Models.Quests;

namespace Skua.App.Droid.Plugins.Tools;

/// <summary>
/// The Loader on Skua's own pieces: the desktop's input rules and copy formats (<see cref="LoaderText"/>), the shop load
/// shared with the Control page (<see cref="IControlBackend.LoadShop"/>), Skua's quest load and "fake complete"
/// (<see cref="IScriptQuest"/>), and Skua's quest list and its updater (<see cref="IQuestDataLoaderService"/>,
/// QuestData.json in Skua's folder: the file the community script sync keeps current).
/// </summary>
public sealed class LoaderBackend : ILoaderBackend
{
    public const string QuestFile = "QuestData.json";

    /// <summary>How long a quest load may take before it counts as failed.</summary>
    internal static TimeSpan QuestWait { get; set; } = TimeSpan.FromSeconds(6);

    private readonly Func<IScriptInterface?> _bot;
    private readonly Func<IServiceProvider?> _services;
    private readonly IControlBackend _control;

    public LoaderBackend(Func<IScriptInterface?> bot, Func<IServiceProvider?> services, IControlBackend control)
    {
        _bot = bot;
        _services = services;
        _control = control;
    }

    public bool IsIdList(string input) => LoaderText.IsIdList(input);

    public string Load(LoaderKind kind, string input)
    {
        if (!LoaderText.IsIdList(input))
            throw new EngineException("Enter IDs: digits, separated by commas or spaces.");
        if (kind == LoaderKind.Shop)
        {
            // The desktop loads a shop only for a single ID.
            if (!int.TryParse(input.Trim(), out int shopId))
                throw new EngineException("Enter one shop ID.");
            return _control.LoadShop(shopId);
        }
        return LoadQuests(LoaderText.ParseIds(input));
    }

    public string LoadQuests(IReadOnlyList<int> questIds)
    {
        int[] ids = questIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            throw new EngineException("Enter a quest ID.");
        IScriptInterface bot = Ready();
        bot.Quests.Load(ids);
        DateTime until = DateTime.UtcNow + QuestWait;
        List<int> missing = ids.ToList();
        while (true)
        {
            missing.RemoveAll(id => bot.Quests.TryGetQuest(id, out Quest? q) && q is not null);
            if (missing.Count == 0 || DateTime.UtcNow > until)
                break;
            Thread.Sleep(100);
        }
        if (missing.Count == ids.Length)
            throw new EngineException(ids.Length == 1 ? $"Quest {ids[0]} did not load (no such quest?)." : "None of the quests loaded (no such quests?).");
        if (missing.Count > 0)
            return $"Loaded {ids.Length - missing.Count} of {ids.Length} quests; not loaded: {string.Join(", ", missing)}.";
        return ids.Length == 1 && bot.Quests.TryGetQuest(ids[0], out Quest? one) && one is not null
            ? $"Loaded quest {one.ID}: {one.Name}."
            : $"Loaded {ids.Length} quests.";
    }

    public async Task<IReadOnlyList<QuestListEntry>> GetQuestListAsync()
    {
        List<QuestData> data = await Quests().GetFromFileAsync(QuestFile).ConfigureAwait(false);
        return Map(data);
    }

    public bool Matches(QuestListEntry quest, string query) => LoaderText.QuestMatches(quest.Id, quest.Name, query);

    public string CopyText(IReadOnlyList<QuestListEntry> quests, QuestCopyFormat format) => format switch
    {
        QuestCopyFormat.Names => LoaderText.Names(quests.Select(q => q.Name)),
        QuestCopyFormat.Ids => LoaderText.Ids(quests.Select(q => q.Id)),
        _ => LoaderText.NamesAndIds(quests.Select(q => (q.Name, q.Id))),
    };

    public string FakeComplete(int questId)
    {
        IScriptInterface bot = Ready();
        if (!bot.Quests.UpdateQuest(questId))
            throw new EngineException($"Quest {questId} did not load; nothing was changed.");
        return $"Fake completed quest {questId} (this client only).";
    }

    public async Task<IReadOnlyList<QuestListEntry>> UpdateAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        Ready();
        List<QuestData> data = await Quests().UpdateAsync(QuestFile, false, progress, cancellationToken).ConfigureAwait(false);
        return Map(data);
    }

    public string? ParseRange(string input, out int startId, out int endId) => LoaderText.ParseRange(input, out startId, out endId);

    public async Task<IReadOnlyList<QuestListEntry>> UpdateRangeAsync(int startId, int endId, IProgress<string> progress, CancellationToken cancellationToken)
    {
        Ready();
        List<QuestData> data = await Quests().UpdateRangeAsync(QuestFile, startId, endId, progress, cancellationToken).ConfigureAwait(false);
        return Map(data);
    }

    private static IReadOnlyList<QuestListEntry> Map(IEnumerable<QuestData> data) =>
        data.Select(q => new QuestListEntry(q.ID, q.Name ?? string.Empty, q.Level, q.Upgrade, q.Once)).ToList();

    private IScriptInterface Ready()
    {
        IScriptInterface bot = _bot() ?? throw new EngineException("The game is not available (no Skua session).");
        if (bot.Player?.LoggedIn != true)
            throw new EngineException("Log in first.");
        return bot;
    }

    private IQuestDataLoaderService Quests() =>
        _services()?.GetService<IQuestDataLoaderService>() ?? throw new EngineException("The game is not available (no Skua session).");
}
