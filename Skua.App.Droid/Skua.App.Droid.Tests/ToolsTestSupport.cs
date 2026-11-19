using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Tools;
using Skua.Core.Models.Quests;

namespace Skua.App.Droid.Tests;

/// <summary>The Tools pages' backends as fakes that record every call.</summary>
internal sealed class FakeGameTools : IGameTools
{
    public FakeFastTravel FakeFastTravel { get; } = new();

    public IFastTravelBackend FastTravel => FakeFastTravel;

    public FakeLoader FakeLoader { get; } = new();

    public ILoaderBackend Loader => FakeLoader;
}

internal sealed class FakeFastTravel : IFastTravelBackend
{
    public List<TravelDestination> Saved { get; } =
    [
        new("Nulgath", "tercessuinotlim", "Boss2", "Right"),
        new("Shop", "shopmap", "Enter", "Spawn"),
    ];

    public List<string> Calls { get; } = new();

    public TravelLocation? Here { get; set; } = new("testmap", "r2", "Left");

    public IReadOnlyList<TravelDestination> Load() => Saved.ToList();

    public void Save(IReadOnlyList<TravelDestination> destinations)
    {
        Calls.Add($"save {destinations.Count}");
        Saved.Clear();
        Saved.AddRange(destinations);
    }

    public IReadOnlyList<TravelDestination> Presets { get; } =
    [
        new("Tercessuinotlim", "tercessuinotlim", "Enter", "Spawn"),
        new("Nulgath", "tercessuinotlim", "Boss2", "Right"),
    ];

    public TravelLocation? CurrentLocation() => Here;

    public void Travel(TravelDestination destination, bool usePrivateRoom, int roomNumber)
    {
        Calls.Add($"travel {destination.Map} {destination.Cell} {destination.Pad} {(usePrivateRoom ? roomNumber.ToString() : "public")}");
        if (destination.Map == "nowhere")
            throw new EngineException("Log in first.");
    }
}

internal sealed class FakeLoader : ILoaderBackend
{
    public List<string> Calls { get; } = new();

    public List<QuestListEntry> List { get; } =
    [
        new(1000, "Pixie Problem", 1, false, false),
        new(1001, "Map item", 1, false, false),
        new(2000, "Member Quest", 50, true, true),
    ];

    public bool IsIdList(string input) => LoaderText.IsIdList(input);

    public string Load(LoaderKind kind, string input)
    {
        Calls.Add($"load {kind} {input}");
        if (input.Trim() == "404")
            throw new EngineException("Shop 404 did not load.");
        return $"loaded {kind} {input}";
    }

    public string LoadQuests(IReadOnlyList<int> questIds)
    {
        Calls.Add($"quests {string.Join(",", questIds)}");
        return "loaded quests";
    }

    public Task<IReadOnlyList<QuestListEntry>> GetQuestListAsync() => Task.FromResult<IReadOnlyList<QuestListEntry>>(List.ToList());

    public bool Matches(QuestListEntry quest, string query) => LoaderText.QuestMatches(quest.Id, quest.Name, query);

    public string CopyText(IReadOnlyList<QuestListEntry> quests, QuestCopyFormat format) => format switch
    {
        QuestCopyFormat.Names => LoaderText.Names(quests.Select(q => q.Name)),
        QuestCopyFormat.Ids => LoaderText.Ids(quests.Select(q => q.Id)),
        _ => LoaderText.NamesAndIds(quests.Select(q => (q.Name, q.Id))),
    };

    public string FakeComplete(int questId)
    {
        Calls.Add($"fake {questId}");
        return "faked";
    }

    public async Task<IReadOnlyList<QuestListEntry>> UpdateAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        Calls.Add("update");
        progress.Report("Loading Quests 2001-2029...");
        await Task.Yield();
        List.Add(new(2001, "New Quest", 1, false, false));
        return List.ToList();
    }

    public string? ParseRange(string input, out int startId, out int endId) => LoaderText.ParseRange(input, out startId, out endId);

    public ManualResetEventSlim? Gate { get; set; }

    public async Task<IReadOnlyList<QuestListEntry>> UpdateRangeAsync(int startId, int endId, IProgress<string> progress, CancellationToken cancellationToken)
    {
        Calls.Add($"range {startId}-{endId}");
        progress.Report($"Loading Quests {startId}-{endId}...");
        while (Gate is { IsSet: false } && !cancellationToken.IsCancellationRequested)
            await Task.Delay(10);
        return List.ToList();
    }
}
