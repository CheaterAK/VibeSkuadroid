using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Tools;

namespace Skua.App.Droid.Tests;

/// <summary>The Tools pages' backends as fakes that record every call.</summary>
internal sealed class FakeGameTools : IGameTools
{
    public FakeFastTravel FakeFastTravel { get; } = new();

    public IFastTravelBackend FastTravel => FakeFastTravel;
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
