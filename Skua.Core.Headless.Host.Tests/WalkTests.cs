using Skua.Core.Headless.Flash;
using Skua.Core.Interfaces;

namespace Skua.Core.Headless.Host.Tests;

/// <summary>Skua's Player.WalkTo and pMC.x/y headless: the avatar starts on its pad, walks in the client's time, the server hears mv.</summary>
public sealed class WalkTests : IAsyncLifetime
{
    private static readonly string s_data = Path.Combine(Path.GetTempPath(), "skua-headless-walk-" + Environment.ProcessId);
    private readonly List<string> _log = new();
    private FakeWorld _world = null!;
    private HeadlessSkuaSession _session = null!;

    public Task InitializeAsync()
    {
        _world = new FakeWorld();
        _session = HeadlessSkuaSession.Create(new HeadlessSkuaOptions
        {
            DataDirectory = s_data,
            WebApi = _world.Api,
            Game = new HeadlessGameOptions { Session = _world.SessionOptions, Client = new() { MapLoadTime = TimeSpan.FromMilliseconds(100) } },
            Log = e =>
            {
                lock (_log)
                    _log.Add($"[{e.Source}] {e.Message}");
            },
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _world.DisposeAsync();
    }

    [Fact]
    public async Task SkuasWalkToMovesTheAvatarAndTellsTheServer()
    {
        Assert.True(await _session.LoginAsync(FakeWorld.User, FakeWorld.Pass, "Sandbox"), string.Join("\n", _log));
        IScriptPlayer player = _session.Bot.Player;
        // FakeWorld's synthetic map puts every pad at (0, 0): the avatar stands there (give or take 5 px).
        Assert.True(await FakeWorld.Until(() => _session.Game.Client.Cells is not null));
        Assert.InRange(player.X, -5, 4);
        Assert.InRange(player.Y, -5, 4);

        // Skua waits up to its wait timeout for pMC.x/y to reach the target, as on Windows; this walk takes about 1.4 s.
        await Task.Run(() => player.WalkTo(200, 150));
        Assert.True(await FakeWorld.Until(() => (player.X, player.Y) == (200, 150), 5000), $"{player.X},{player.Y}");
        Assert.Contains(_world.Server.LastSession!.Received, r => r.Text == $"%xt%zm%mv%{_session.Bot.Map.RoomID}%200%150%8%");
        Assert.DoesNotContain(_session.Gaps.Snapshot(), g => g.Name.Contains("pMC", StringComparison.Ordinal) || g.Name.Contains("world.players", StringComparison.Ordinal));
    }
}
