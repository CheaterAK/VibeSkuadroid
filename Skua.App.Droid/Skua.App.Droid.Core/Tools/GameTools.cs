namespace Skua.App.Droid.Core.Tools;

/// <summary>
/// The game-side halves of the desktop tools ported to the Tools pages (Fast Travel, Loader, Grabber): what they do
/// through Skua's services on the engine's one session. Implemented in Skua.App.Droid.Plugins (on Skua.Core); the
/// ViewModels only see these interfaces and plain records, as they only see <see cref="Engine.IBotEngine"/>.
/// </summary>
/// <remarks>
/// Confirmation is the pages' job (the app's own confirmation UI): nothing here asks anything, and nothing here goes
/// through Skua's dialog service (which declines every unattended confirmation on this client).
/// </remarks>
public interface IGameTools
{
    IFastTravelBackend FastTravel { get; }
}

/// <summary>A saved fast travel destination (Skua's "FastTravels" setting, "name,map,cell,pad").</summary>
public sealed record TravelDestination(string Name, string Map, string Cell, string Pad);

/// <summary>Where the character is.</summary>
public sealed record TravelLocation(string Map, string Cell, string Pad);

/// <summary>Fast Travel (desktop FastTravelView): saved destinations and Skua's map service travel.</summary>
public interface IFastTravelBackend
{
    /// <summary>The saved destinations (the desktop's list: Skua's "FastTravels" setting).</summary>
    IReadOnlyList<TravelDestination> Load();

    /// <summary>Replaces the saved destinations.</summary>
    void Save(IReadOnlyList<TravelDestination> destinations);

    /// <summary>Skua's preset destinations ("DefaultFastTravels").</summary>
    IReadOnlyList<TravelDestination> Presets { get; }

    /// <summary>Map, cell and pad the character is in; null when not in game.</summary>
    TravelLocation? CurrentLocation();

    /// <summary>
    /// Travels the way the desktop's destination cards do (Skua's map service): joins the map (in the private room
    /// <paramref name="roomNumber"/> when <paramref name="usePrivateRoom"/>), then the cell and pad. Returns once the
    /// travel started; the join runs in the background.
    /// </summary>
    /// <exception cref="Engine.EngineException">Not in game.</exception>
    void Travel(TravelDestination destination, bool usePrivateRoom, int roomNumber);
}
