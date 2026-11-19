using Skua.App.Droid.Core.Plugins;
using Skua.App.Droid.Plugins.Butler;
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
    private readonly ButlerBackend _butler;

    /// <param name="bot">The session's script interface (null while it does not exist).</param>
    /// <param name="services">The session's Skua services (null while it does not exist).</param>
    public DroidPluginBackends(Func<IScriptInterface?> bot, Func<IServiceProvider?> services)
    {
        _dailyTracker = new DailyTrackerBackend(bot);
        _butler = new ButlerBackend(bot, services);
    }

    public IDailyTrackerBackend DailyTracker => _dailyTracker;

    public IButlerBackend Butler => _butler;

    public void Dispose()
    {
        _butler.Dispose();
        _dailyTracker.Dispose();
    }
}
