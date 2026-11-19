using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Tools;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Models;

namespace Skua.App.Droid.Plugins.Tools;

/// <summary>
/// Fast Travel on Skua's own pieces, as the desktop window uses them: the destinations are Skua's "FastTravels" setting
/// (same file and line format, <see cref="FastTravelEntry"/>), the presets "DefaultFastTravels", the current location
/// and the travel itself Skua's <see cref="IMapService"/> (private room, the tercessuinotlim detour through m22).
/// </summary>
public sealed class FastTravelBackend : IFastTravelBackend
{
    public const string SettingKey = "FastTravels";
    public const string PresetsKey = "DefaultFastTravels";

    private readonly Func<IScriptInterface?> _bot;
    private readonly Func<IServiceProvider?> _services;

    public FastTravelBackend(Func<IScriptInterface?> bot, Func<IServiceProvider?> services)
    {
        _bot = bot;
        _services = services;
    }

    public IReadOnlyList<TravelDestination> Load() => Read(SettingKey);

    public IReadOnlyList<TravelDestination> Presets => Read(PresetsKey);

    public void Save(IReadOnlyList<TravelDestination> destinations)
    {
        StringCollection values = new();
        foreach (TravelDestination d in destinations)
            values.Add(FastTravelEntry.Format(d.Name, d.Map, d.Cell, d.Pad));
        try
        {
            Settings().Set(SettingKey, values);
        }
        catch (InvalidOperationException e)
        {
            throw new EngineException("Could not save the destinations: " + e.Message, e);
        }
    }

    public TravelLocation? CurrentLocation()
    {
        IScriptInterface? bot = _bot();
        if (bot?.Player?.Playing != true)
            return null;
        (string map, string cell, string pad) = Map().GetCurrentLocation();
        return new TravelLocation(map, cell, pad);
    }

    public void Travel(TravelDestination destination, bool usePrivateRoom, int roomNumber)
    {
        IScriptInterface bot = _bot() ?? throw new EngineException("The game is not available (no Skua session).");
        if (bot.Player?.LoggedIn != true)
            throw new EngineException("Log in first.");
        if (!bot.Player.Playing)
            throw new EngineException("Can't travel while dead.");
        IMapService map = Map();
        map.UsePrivateRoom = usePrivateRoom;
        map.PrivateRoomNumber = roomNumber;
        map.Travel(new Target(destination));
    }

    private IReadOnlyList<TravelDestination> Read(string key)
    {
        StringCollection? saved = Settings().Get<StringCollection>(key);
        List<TravelDestination> list = new();
        if (saved is null)
            return list;
        foreach (string? line in saved)
        {
            if (FastTravelEntry.TryParse(line, out FastTravelEntry? e))
                list.Add(new TravelDestination(e!.Name, e.Map, e.Cell, e.Pad));
        }
        return list;
    }

    private ISettingsService Settings() =>
        _services()?.GetService<ISettingsService>() ?? throw new EngineException("The game is not available (no Skua session).");

    private IMapService Map() =>
        _services()?.GetService<IMapService>() ?? throw new EngineException("The game is not available (no Skua session).");

    /// <summary>What Skua's map service travels to (the desktop passes its destination card's ViewModel).</summary>
    private sealed class Target : IFastTravelItemViewModel
    {
        public Target(TravelDestination d)
        {
            DescriptionName = d.Name;
            MapName = d.Map;
            Cell = d.Cell;
            Pad = d.Pad;
        }

        public string DescriptionName { get; }

        public string MapName { get; }

        public string Cell { get; }

        public string Pad { get; }

        public IRelayCommand<object> TravelCommand { get; } = new RelayCommand<object>(_ => { });

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }
    }
}
