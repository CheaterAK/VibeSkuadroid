using Skua.App.Droid.Core.GameView;

namespace Skua.App.Droid.Tests.GameView;

/// <summary>The game view's memory guard: warns once near the limit, closes over it or when the renderer dies, and stays closed.</summary>
public class MemoryGuardTests
{
    [Fact]
    public void ClosesOnTheFirstSampleOverTheLimitAndStaysClosed()
    {
        GameViewMemoryGuard guard = new(1536);
        Assert.Equal(MemoryGuardVerdict.Ok, guard.Sample(600));
        Assert.Equal(MemoryGuardVerdict.Ok, guard.Sample(820));
        Assert.Equal(MemoryGuardVerdict.Warn, guard.Sample(1400)); // 85 % of 1536 = 1305.6
        Assert.Equal(MemoryGuardVerdict.Ok, guard.Sample(1450));   // warned once only
        Assert.False(guard.Tripped);
        Assert.Equal(MemoryGuardVerdict.Close, guard.Sample(2700)); // the measured runaway: one sample is enough
        Assert.True(guard.Tripped);
        Assert.Contains("2700 MB", guard.Reason);
        Assert.Contains("1536 MB", guard.Reason);
        Assert.Equal(MemoryGuardVerdict.Close, guard.Sample(500));
        Assert.Equal(2700, guard.PeakMb);
    }

    [Fact]
    public void TheMeasuredGoodConfigurationStaysOpen()
    {
        GameViewMemoryGuard guard = new(new GameViewOptions().MemoryLimitMb);
        Assert.Equal(1536, guard.LimitMb);
        foreach (double pss in new[] { 580.0, 700, 790, 820, 815, 822, 819 })
            Assert.Equal(MemoryGuardVerdict.Ok, guard.Sample(pss));
        Assert.Equal(MemoryGuardVerdict.Ok, guard.Sample(double.NaN));
    }

    [Theory]
    [InlineData(true, "crashed")]
    [InlineData(false, "killed by the system")]
    public void ARendererDeathCloses(bool crashed, string text)
    {
        GameViewMemoryGuard guard = new(1536);
        Assert.Equal(MemoryGuardVerdict.Close, guard.RendererGone(crashed));
        Assert.Contains(text, guard.Reason);
        Assert.Equal(MemoryGuardVerdict.Close, guard.Sample(100));
    }

    [Theory]
    [InlineData(100, 512)]
    [InlineData(99999, 4096)]
    [InlineData(2048, 2048)]
    public void TheLimitIsClampedAndConfigurable(int asked, int used)
    {
        Assert.Equal(used, new GameViewMemoryGuard(asked).LimitMb);
        Assert.Equal(used, (new GameViewOptions { MemoryLimitMb = asked }).Normalized().MemoryLimitMb);
    }

    [Fact]
    public void ReadsPssFromSmapsRollup()
    {
        const string rollup = "00400000-7fff [rollup]\nRss:             1048576 kB\nPss:              838860 kB\nPss_Anon:   1 kB\n";
        Assert.Equal(819.2, GameViewMemoryGuard.ParseSmapsRollupPss(rollup), 1);
        Assert.True(double.IsNaN(GameViewMemoryGuard.ParseSmapsRollupPss("Rss: 1 kB")));
    }

    [Fact]
    public async Task AViewClosedByTheGuardReturnsToHeadlessWithANotice()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        FakeLauncher launcher = new();
        using GameViewSwitch sw = new(app.Host, launcher, GameViewSwitchTests.Fast);
        await sw.OpenAsync();
        string session = launcher.Launched[0];
        sw.Connect(session);
        GameViewMemoryGuard guard = new(sw.Options.MemoryLimitMb);
        Assert.Equal(MemoryGuardVerdict.Close, guard.Sample(2000));
        sw.ViewClosing(session, guard.Reason!, socketsClosed: false, null);
        await sw.Returning;
        Assert.Equal(GameViewState.Headless, sw.State);
        Assert.Contains("too much memory", sw.Status.Notice);
        Assert.Equal(Core.Engine.ConnectionPhase.Online, app.Engine.Snapshot.Connection);
    }
}
