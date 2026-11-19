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
}
