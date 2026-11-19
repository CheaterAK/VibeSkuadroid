using Skua.App.Droid.Core.Plugins;
using Skua.App.Droid.Core.Control;
using Skua.App.Droid.Plugins.Butler;
using Skua.App.Droid.Plugins.Control;
using Skua.App.Droid.Plugins.DailyTracker;
using Skua.Core.Interfaces;

namespace Skua.App.Droid.Plugins;

/// <summary>
/// The built-in plugins' game-side halves (and the Control page's) on one Skua session. Created once by the script host when its session
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
        Control = new ControlBackend(bot, services);
    }

    public IDailyTrackerBackend DailyTracker => _dailyTracker;

    public IButlerBackend Butler => _butler;

    /// <summary>The Control page's game side (same session, same API).</summary>
    public IControlBackend Control { get; }

    public void Dispose()
    {
        _butler.Dispose();
        _dailyTracker.Dispose();
    }
}
