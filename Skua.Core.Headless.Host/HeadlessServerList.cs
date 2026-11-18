using Skua.Core.Headless.Flash;
using Skua.Core.Headless.Net;
using Skua.Core.Interfaces;
using Skua.Core.Models.Servers;

namespace Skua.Core.Headless.Host;

/// <summary>
/// Skua's server list on the headless client (<see cref="IServerListProvider"/>): <c>api/data/servers</c> over HTTPS
/// through the session's <see cref="AqwWebApi"/> (game.aq.com, with the browser-like headers the site requires), and
/// when that fails or is empty, the list the last HTTP login returned. Skua.Core's own fetch (plain http to
/// content.aq.com, which Android refuses) is never used.
/// </summary>
public sealed class HeadlessServerListProvider : IServerListProvider
{
    private readonly HeadlessGame _game;
    private readonly Action<HeadlessLogEntry> _log;

    public HeadlessServerListProvider(HeadlessGame game, Action<HeadlessLogEntry>? log = null)
    {
        _game = game;
        _log = log ?? (_ => { });
    }

    public async Task<List<Server>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GameServer> servers;
        try
        {
            servers = await _game.Session.Api.GetServersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AqwLoginException or HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            servers = Array.Empty<GameServer>();
            _log(new HeadlessLogEntry("game", $"Server list request failed ({e.Message}); using the login reply's list.", DateTimeOffset.UtcNow));
        }
        if (servers.Count == 0)
            servers = _game.Servers;
        return servers.Select(ToSkua).ToList();
    }

    /// <summary>A server list entry as Skua's model (the fields of <c>api/data/servers</c>).</summary>
    public static Server ToSkua(GameServer s)
    {
        return new Server
        {
            Name = s.Name,
            IP = s.Host,
            Port = s.Port,
            PlayerCount = s.Players,
            MaxPlayers = s.MaxPlayers,
            Online = s.Online,
            Upgrade = s.MembersOnly,
            Level = s.MinLevel,
            Lang = s.Language ?? string.Empty,
            ChatLevel = JsonUtil.AsInt(s.Raw?["iChat"]),
        };
    }
}
