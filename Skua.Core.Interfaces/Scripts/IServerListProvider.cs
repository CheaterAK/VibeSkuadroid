using Skua.Core.Models.Servers;

namespace Skua.Core.Interfaces;

/// <summary>
/// Where <see cref="IScriptServers.GetServers(bool)"/> gets the game's server list. Optional: when none is registered,
/// Skua.Core fetches the list itself as it always has (the desktop). A host registers one when it has a better source,
/// such as the headless client, which reads it over HTTPS with the headers the game's site requires.
/// </summary>
public interface IServerListProvider
{
    /// <summary>The current server list; empty when it cannot be read (it should not throw for network failures).</summary>
    Task<List<Server>> GetServersAsync(CancellationToken cancellationToken = default);
}
