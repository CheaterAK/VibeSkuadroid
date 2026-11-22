using Skua.App.Droid.Core.Army;
using Skua.App.Droid.Core.Engine;
using Skua.Core.Headless.World;
using System.Text.Json.Nodes;
using Skua.Core.Headless.Tests.Fakes;

namespace Skua.App.Droid.Tests;

/// <summary>
/// Light accounts: many Skua.Core.Headless sessions in one process, each against its own fake AQW server, with the
/// native behaviours; the rotation parser, the follow decision, the login stagger, and the per-session memory figure.
/// </summary>
public class ArmyLightTests
{
    private readonly ITestOutputHelper _output;

    public ArmyLightTests(ITestOutputHelper output)
    {
        _output = output;
    }

    internal static LightAccount NewLight(FakeAqw aqw, string name, LoginStagger? stagger = null) =>
        new(new ArmyAccount(AccountStore.NewId(), name), new LightOptions
        {
            SessionFactory = aqw.NewSession,
            Stagger = stagger,
            Tick = TimeSpan.FromMilliseconds(40),
            ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
        });

    [Fact]
    public async Task SeveralLightSessionsFarmConcurrentlyInOneProcess()
    {
        const int n = 4;
        FakeAqw[] servers = Enumerable.Range(0, n).Select(_ => new FakeAqw()).ToArray();
        foreach (FakeAqw s in servers)
        {
            s.Server.AutoAttackCooldownMs = 150;
            s.Server.RespawnDelay = TimeSpan.FromMilliseconds(50);
        }
        LightAccount[] lights = servers.Select((s, i) => NewLight(s, $"light{i}")).ToArray();
        try
        {
            await Task.WhenAll(lights.Select(l => l.LoginAsync(FakeAqw.Password, null)));
            Assert.All(lights, l => Assert.True(l.Engine.Snapshot.IsOnline));
            Assert.All(servers, s => Assert.Single(s.Server.Sessions));

            await Task.WhenAll(lights.Select(l => l.Engine.JoinMapAsync("testmap", "r2", "Left")));
            foreach (LightAccount l in lights)
            {
                l.SetPickup(new LightPickup(["Pixie Wing"], [], RejectOthers: false));
                l.StartQuest(1000, maxTurnIns: 1);
                l.StartFight(new LightFight("Pixie", LightRotation.Parse("1 | 2 | 3")));
            }
            // Every session fights its own Pixies, picks up its own Pixie Wings and turns in its own quest once.
            await Sandbox.Until(() => lights.All(l => l.Quest is { Done: true }), 30000);
            await Sandbox.Until(() => lights.All(l => l.World.GetInventory().Any(i => i.Name == "Pixie Wing")), 15000);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(1, lights[i].Quest!.TurnIns);
                Assert.Contains("tryQuestComplete", servers[i].Server.LastSession!.Commands);
                Assert.Contains("getDrop", servers[i].Server.LastSession!.Commands);
                Assert.Equal("r2", lights[i].World.GetArea().Cell);
            }
            // The quest loop stops at its limit: no second turn-in.
            int turnIns = servers[0].Server.LastSession!.Commands.Count(c => c == "tryQuestComplete");
            await Task.Delay(400);
            Assert.Equal(turnIns, servers[0].Server.LastSession!.Commands.Count(c => c == "tryQuestComplete"));

            // Scripts are refused with the reason, and Stop All ends the fight.
            EngineException noScripts = await Assert.ThrowsAsync<EngineException>(() => lights[0].Engine.StartScriptAsync("x.cs"));
            Assert.Contains("Light accounts run native tasks", noScripts.Message);
            lights[0].StopAll();
            Assert.Null(lights[0].Fight);
        }
        finally
        {
            foreach (LightAccount l in lights)
                await l.DisposeAsync();
            foreach (FakeAqw s in servers)
                await s.DisposeAsync();
        }
    }

    [Fact]
    public async Task ALightSessionRestsResurrectsAndRejectsJunk()
    {
        await using FakeAqw aqw = new();
        await using LightAccount light = NewLight(aqw, "solo");
        await light.LoginAsync(FakeAqw.Password, null);
        await light.Engine.JoinMapAsync("testmap", "Enter", "Spawn");
        await Sandbox.Until(() => light.World.GetArea().MapName == "testmap");

        light.StartFight(new LightFight("*", LightRotation.Default, RestBelowPercent: 101));
        await Sandbox.Until(() => aqw.Server.LastSession!.Commands.Contains("restRequest"));
        Assert.StartsWith("Resting", light.Status);

        // A drop on the junk list is closed client side, never picked up.
        aqw.Server.LastSession!.Json(new JsonObject { ["cmd"] = "dropItem", ["items"] = new JsonObject { ["11"] = new JsonObject { ["ItemID"] = 11, ["sName"] = "Pixie Wing", ["iQty"] = 1 } } });
        await Sandbox.Until(() => light.World.GetDrops().Count == 1);
        light.SetPickup(new LightPickup([], ["Pixie Wing"], RejectOthers: false));
        await Sandbox.Until(() => light.World.GetDrops().Count == 0);
        Assert.DoesNotContain("getDrop", aqw.Server.LastSession!.Commands);

        // Dead: the light client asks to resurrect.
        aqw.Server.LastSession!.Str("uotls", "hero", "intHP:0,intState:0");
        await Sandbox.Until(() => light.Status.StartsWith("Dead", StringComparison.Ordinal));
        await Sandbox.Until(() => aqw.Server.LastSession!.Commands.Contains("resPlayerTimed"));
    }

    [Fact]
    public void RotationsAreReadWithSkuasParser()
    {
        LightRotation r = LightRotation.Parse("1 H>70% | 2 M<30 | 3S | 4WW500");
        Assert.Equal([1, 2, 3, 4], r.Steps.Select(s => s.Index));
        LightSkillStep heal = r.Steps[0];
        Assert.True(heal.Rules[0].Health);
        Assert.True(heal.Rules[0].Greater);
        Assert.True(LightRotation.Allows(heal, hp: 80, hpMax: 100, mp: 0, mpMax: 100));
        Assert.False(LightRotation.Allows(heal, hp: 50, hpMax: 100, mp: 0, mpMax: 100));
        Assert.False(r.Steps[1].Rules[0].Health);
        Assert.True(LightRotation.Allows(r.Steps[1], 100, 100, 20, 100));
        Assert.Equal(500, r.Steps[3].WaitMs);
        Assert.Equal([1, 2, 3, 4], LightRotation.Parse("").Steps.Select(s => s.Index));
        Assert.True(LightRotation.Parse("1 | 2", "WaitForCooldown", 300).WaitForCooldown);
        Assert.Equal([1, 2, 3, 4], LightRotation.Parse("garbage ||| x").Steps.Select(s => s.Index));
    }

    [Fact]
    public void TheFollowDecisionGotosThenMovesToTheCell()
    {
        FollowTargetPosition master = new("Master", "battleon", "r3", "Left");
        TimeSpan long_ = TimeSpan.FromSeconds(10);
        Assert.Equal(FollowAction.Goto, LightFollow.Decide(master, null, "yulgar", "Enter", long_, long_, 0).Action);
        Assert.Equal(FollowAction.None, LightFollow.Decide(master, null, "yulgar", "Enter", TimeSpan.FromSeconds(1), long_, 0).Action);
        // /goto keeps failing (private room): join the map by name.
        Assert.Equal(FollowAction.JoinMap, LightFollow.Decide(master, null, "yulgar", "Enter", long_, long_, LightFollow.GotoAttemptsBeforeJoin).Action);
        // A follow target by name only never joins a guessed map.
        Assert.Equal(FollowAction.Goto, LightFollow.Decide(master with { Map = null }, null, "yulgar", "Enter", long_, long_, 9).Action);

        AreaUserInfo here = new("Master", 5, "r3", "Left", 100, 100, 1, 50, false, new JsonObject(), null);
        (FollowAction action, string? cell, string? pad) = LightFollow.Decide(master, here, "battleon", "Enter", long_, long_, 0);
        Assert.Equal((FollowAction.MoveToCell, "r3", "Left"), (action, cell, pad));
        Assert.Equal(FollowAction.None, LightFollow.Decide(master, here, "battleon", "r3", long_, long_, 0).Action);
        Assert.Equal(FollowAction.None, LightFollow.Decide(master, here, "battleon", "Enter", long_, TimeSpan.FromMilliseconds(100), 0).Action);
    }

    [Fact]
    public async Task TheStaggerSpacesLoginsAndRetriesOnlyWhatIsWorthIt()
    {
        ManualTime time = new();
        List<TimeSpan> waits = new();
        LoginStagger stagger = new(new StaggerOptions(), time, (d, _) =>
        {
            waits.Add(d);
            time.Advance(d);
            return Task.CompletedTask;
        }, new Random(7));
        List<(string Who, DateTimeOffset At)> starts = new();
        int flaky = 0;
        StaggeredLogin Ok(string who) => new(who, who, _ => { starts.Add((who, time.GetUtcNow())); return Task.CompletedTask; });
        IReadOnlyList<StaggerResult> results = await stagger.RunAsync(
        [
            Ok("a"),
            Ok("b"),
            new("c", "c", _ => { starts.Add(("c", time.GetUtcNow())); return ++flaky < 3 ? throw new EngineException("Connection failed: timeout") : Task.CompletedTask; }),
            new("d", "d", _ => throw new EngineException("Login failed: Wrong password")),
            Ok("a"), // the same account twice in one run: once
        ]);
        Assert.Equal(["a", "b", "c", "d"], results.Select(r => r.Key));
        Assert.True(results[2].Ok);
        Assert.Equal(3, results[2].Attempts);
        Assert.False(results[3].Ok);
        Assert.Equal(1, results[3].Attempts);
        // Every login start is at least 2 s + 400 ms after the previous one, and at most 2 s + 1 s when nothing else waited.
        for (int i = 1; i < starts.Count; i++)
            Assert.True(starts[i].At - starts[i - 1].At >= TimeSpan.FromMilliseconds(2400), $"{starts[i - 1].Who}->{starts[i].Who}");
        Assert.True(starts[1].At - starts[0].At <= TimeSpan.FromMilliseconds(3000));
        Assert.False(LoginStagger.ShouldRetry("No password is saved for x"));
        Assert.True(LoginStagger.ShouldRetry("Connection failed: Server is full"));
    }

    [Fact]
    public async Task TheStaggerNeverRunsTwoLoginsAtOnce()
    {
        LoginStagger stagger = new(new StaggerOptions { Spacing = TimeSpan.FromMilliseconds(10), JitterMinMs = 0, JitterMaxMs = 5 });
        int running = 0, peak = 0;
        await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using (await stagger.EnterAsync())
            {
                int now = Interlocked.Increment(ref running);
                peak = Math.Max(peak, now);
                await Task.Delay(15);
                Interlocked.Decrement(ref running);
            }
        }));
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task ALightAccountsSecretsAreInTheDiagnosticsRegistryUntilLogout()
    {
        await using FakeAqw aqw = new();
        LightAccount light = NewLight(aqw, "scrubbed");
        await light.LoginAsync(FakeAqw.Password, null);
        await Sandbox.Until(() => light.Engine.Snapshot.IsOnline);
        string token = light.Engine.Session.Account!.Token!;
        Assert.False(string.IsNullOrEmpty(token));
        // Crash and exit reports, canary samples and the bundle all go through DiagnosticText.
        Assert.DoesNotContain(token, Skua.App.Droid.Core.Diagnostics.DiagnosticText.Scrub("socket login with " + token));
        Assert.DoesNotContain(FakeAqw.Password, Skua.App.Droid.Core.Diagnostics.DiagnosticText.Scrub("pw " + FakeAqw.Password));

        await light.Engine.LogoutAsync();
        Assert.Contains(token, Skua.App.Droid.Core.Diagnostics.DiagnosticText.Scrub("socket login with " + token));
        await light.DisposeAsync();
    }

    [Fact]
    public void ALinkedScrubbersSecretsAreScrubbedUntilClearedOrUnlinked()
    {
        Skua.App.Droid.Core.Security.SecretScrubber registry = new(), a = new(), b = new();
        registry.Add("registry-secret");
        registry.Link(a);
        registry.Link(b);
        a.Add("account-a-pass");
        b.Add("account-b-token");
        b.Add("account-a-pass-and-more"); // longer, containing a's: replaced whole
        Assert.Equal("<redacted> <redacted> <redacted> <redacted>", registry.Scrub("registry-secret account-a-pass account-b-token account-a-pass-and-more"));
        a.Clear(); // a's logout: b and the registry keep theirs
        Assert.Equal("<redacted> account-a-pass <redacted>", registry.Scrub("registry-secret account-a-pass account-b-token"));
        registry.Clear(); // the main engine's logout: linked accounts keep theirs
        Assert.Equal("registry-secret <redacted>", registry.Scrub("registry-secret account-b-token"));
        registry.Unlink(b);
        Assert.Equal("account-b-token", registry.Scrub("account-b-token"));
        Assert.Equal("<redacted>", b.Scrub("account-b-token"));
    }

    [Fact]
    public async Task LightSessionsReloginThroughTheStagger()
    {
        await using FakeAqw a = new();
        await using FakeAqw b = new();
        LoginStagger stagger = new(new StaggerOptions { Spacing = TimeSpan.FromMilliseconds(800), JitterMinMs = 0, JitterMaxMs = 0 });
        await using LightAccount la = NewLight(a, "la", stagger);
        await using LightAccount lb = NewLight(b, "lb", stagger);
        await la.LoginAsync(FakeAqw.Password, null);
        await lb.LoginAsync(FakeAqw.Password, null);
        a.Server.LastSession!.Close();
        b.Server.LastSession!.Close();
        await Sandbox.Until(() => a.Server.Sessions.Count == 2 && b.Server.Sessions.Count == 2 && la.Engine.Snapshot.IsOnline && lb.Engine.Snapshot.IsOnline, 15000);
        // The two relogins did not start together.
        long first = a.Server.LastSession!.Received.First().Timestamp;
        long second = b.Server.LastSession!.Received.First().Timestamp;
        string logs = string.Join("\n", la.Engine.GetRecentLog().Concat(lb.Engine.GetRecentLog()).OrderBy(l => l.At).Select(l => $"{l.At:HH:mm:ss.fff} {l.Message}"));
        Assert.True(Math.Abs(first - second) >= System.Diagnostics.Stopwatch.Frequency / 4, $"relogins were not spaced: {(first - second) * 1000.0 / System.Diagnostics.Stopwatch.Frequency} ms\n{logs}");
    }

    /// <summary>
    /// The light-session memory figure: managed heap per online light session (character, inventory, a map with
    /// monsters, a stream of combat, movement and chat packets), measured after a full GC.
    /// </summary>
    [Fact]
    public async Task LightSessionMemoryPerSession()
    {
        const int n = 8;
        FakeAqw[] servers = Enumerable.Range(0, n).Select(_ => new FakeAqw()).ToArray();
        long before = Heap();
        LightAccount[] lights = servers.Select((s, i) => NewLight(s, $"m{i}")).ToArray();
        try
        {
            foreach (LightAccount l in lights)
                await l.LoginAsync(FakeAqw.Password, null);
            await Task.WhenAll(lights.Select(l => l.Engine.JoinMapAsync("testmap", "r2", "Left")));
            foreach (LightAccount l in lights)
                l.StartFight(new LightFight("*", LightRotation.Default));
            // Realistic traffic: 2000 messages per session (other players moving and chatting, monster updates).
            for (int k = 0; k < 2000; k++)
            {
                foreach (FakeAqw s in servers)
                {
                    FakeAqwServer.Session session = s.Server.LastSession!;
                    if (k % 3 == 0)
                        session.Str("uotls", $"other{k % 40}", $"strFrame:r{k % 3 + 1},strPad:Left,intHP:{900 + k % 100}");
                    else if (k % 3 == 1)
                        session.Str("chatm", $"zone~hello {k}", $"other{k % 40}", "1", "1");
                    else
                        session.Str("mtls", 3, $"intHP:{400 - k % 50}");
                }
            }
            await Task.Delay(1500);
            long after = Heap();
            double perSession = (after - before) / (double)n / 1048576.0;
            _output.WriteLine($"Light session managed heap: {perSession:0.00} MB per online session ({n} sessions, {(after - before) / 1048576.0:0.0} MB in all).");
            Console.WriteLine($"LIGHT-SESSION-MEMORY {perSession:0.00} MB/session");
            Assert.True(perSession < 8, $"{perSession:0.00} MB per light session");
            Assert.All(lights, l => Assert.True(l.Engine.Snapshot.IsOnline));
        }
        finally
        {
            foreach (LightAccount l in lights)
                await l.DisposeAsync();
            foreach (FakeAqw s in servers)
                await s.DisposeAsync();
        }
    }

    private static long Heap()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
