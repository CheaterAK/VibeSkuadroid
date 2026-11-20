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

    public FakeGrabber FakeGrabber { get; } = new();

    public IGrabberBackend Grabber => FakeGrabber;
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

internal sealed class FakeGrabber : IGrabberBackend
{
    public List<string> Calls { get; } = new();

    /// <summary>When set, RunAsync waits for it (or for cancellation).</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public IReadOnlyList<GrabbedItem> Grab(GrabberCategory category)
    {
        Calls.Add($"grab {category}");
        return category switch
        {
            GrabberCategory.ShopItems =>
            [
                new(40, "Health Potion", "[40] Health Potion | Cost: 10 Gold", false, "potion"),
                new(41, "Fancy Hat", "[41] Fancy Hat | Cost: 1,000 Gold", false, "hat"),
            ],
            GrabberCategory.Inventory =>
            [
                new(1, "Default Sword", "[1] Default Sword x1 | Category: Sword", false, "sword"),
                new(30, "Old Boots", "[30] Old Boots x3 | Category: Misc", true, "boots"),
            ],
            GrabberCategory.MapMonsters => [new(5, "Pixie", "[5] Pixie | Cell: r2 | HP: 100/100", false, "pixie")],
            _ => [],
        };
    }

    public GrabberPlan Plan(GrabberAction action, IReadOnlyList<GrabbedItem> items)
    {
        if (items.Count == 0)
            return GrabberPlan.Refuse("Select at least one item.");
        return action switch
        {
            GrabberAction.Buy when items.Count == 1 => new(null, $"Buy {items[0].Name}?", new QuantityAsk("Buy quantity", 1, 99, 1)),
            GrabberAction.Buy => new(null, $"Buy {items.Count} items?", null),
            GrabberAction.Sell when items.Count > 1 => GrabberPlan.Refuse("ATTENTION - 2 items selected!"),
            GrabberAction.Sell => new(null, $"Sell {items[0].Name}?", new QuantityAsk("Sell quantity", 1, 3, 1)),
            _ => new(null, null, null),
        };
    }

    public async Task<string> RunAsync(GrabberAction action, IReadOnlyList<GrabbedItem> items, int quantity, IProgress<string> progress, CancellationToken cancellationToken)
    {
        Calls.Add($"{action} {string.Join(",", items.Select(i => i.Id))} x{quantity}");
        progress.Report("working");
        while (Gate is { IsSet: false })
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, CancellationToken.None);
        }
        return $"{action} done";
    }

    public string CopyText(string title, IReadOnlyList<GrabbedItem> items) => $"=== {title} ({items.Count} items) ===\n" + string.Join("\n", items.Select(i => i.Text));

    public string DetailedText(string title, IReadOnlyList<GrabbedItem> items) => "details of " + string.Join(",", items.Select(i => i.Id));
}
