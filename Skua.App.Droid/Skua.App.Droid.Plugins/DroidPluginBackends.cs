using Skua.App.Droid.Core.Plugins;
using Skua.App.Droid.Plugins.DailyTracker;
using Skua.Core.Interfaces;

namespace Skua.App.Droid.Plugins;

/// <summary>
/// The built-in plugins' game-side halves on one Skua session. Created once by the script host when its session
/// exists; disposed with it.
/// </summary>
public sealed class DroidPluginBackends : IPluginBackends, IDisposable
{
    private readonly DailyTrackerBackend _dailyTracker;

    /// <param name="bot">The session's script interface (null while it does not exist).</param>
    public DroidPluginBackends(Func<IScriptInterface?> bot)
    {
        _dailyTracker = new DailyTrackerBackend(bot);
    }

    public IDailyTrackerBackend DailyTracker => _dailyTracker;

    public void Dispose()
    {
        _dailyTracker.Dispose();
    }
}
