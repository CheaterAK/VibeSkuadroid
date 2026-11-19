using Skua.App.Droid.Core.Control;
using Skua.App.Droid.Core.Tools;
using Skua.Core.Interfaces;

namespace Skua.App.Droid.Plugins.Tools;

/// <summary>
/// The Tools pages' game-side halves on one Skua session. Created once by the script host when its session exists;
/// they always reach the current session's services.
/// </summary>
public sealed class DroidGameTools : IGameTools
{
    /// <param name="bot">The session's script interface (null while it does not exist).</param>
    /// <param name="services">The session's Skua services (null while it does not exist).</param>
    /// <param name="control">The Control page's backend: the shop, quest and bank actions the tools share with it.</param>
    public DroidGameTools(Func<IScriptInterface?> bot, Func<IServiceProvider?> services, IControlBackend control)
    {
        FastTravel = new FastTravelBackend(bot, services);
    }

    public IFastTravelBackend FastTravel { get; }
}
