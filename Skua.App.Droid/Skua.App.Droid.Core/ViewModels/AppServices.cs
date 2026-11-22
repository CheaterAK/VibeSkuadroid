using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Notifications;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>What the ViewModels are built from. One set per process.</summary>
public sealed record AppServices(
    EngineHost Host,
    IUiDispatcher Dispatcher,
    IFilePicker FilePicker,
    ScriptLibrary Library,
    IScriptRepositorySync RepositorySync,
    AppPaths Paths)
{
    /// <summary>
    /// Drop notifications, Notification Center, junk and Discord webhook (process-wide, created with the engine). Null
    /// where the host did not create them: their pages are then not offered.
    /// </summary>
    public NotificationHub? Notifications { get; init; }

    /// <summary>The Calendar page's source; null for artix.com over HTTPS (<see cref="Calendar.ArtixCalendarSource"/>).</summary>
    public Calendar.ICalendarSource? Calendar { get; init; }

    /// <summary>The army (accounts, light sessions, full slot processes); null where the host has none: no Army page.</summary>
    public Army.ArmyCoordinator? Army { get; init; }

    /// <summary>Reads the device's memory for the Army page (Android's ActivityManager.MemoryInfo).</summary>
    public Func<Army.DeviceMemory>? DeviceMemory { get; init; }
}
