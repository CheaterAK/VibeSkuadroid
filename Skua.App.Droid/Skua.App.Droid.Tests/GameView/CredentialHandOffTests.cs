using System.Text.RegularExpressions;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.GameView;

namespace Skua.App.Droid.Tests.GameView;

/// <summary>
/// Auto-login in the game view: the stored login leaves the vault only for the open view of the current session, at
/// most a few times, and never reaches an Intent, a file or a log. The Android pieces are checked at the source level
/// (the only Intent extra of the view is the session id; the password key only travels in the link's reply Bundle;
/// the page never stores or reports it).
/// </summary>
public sealed partial class CredentialHandOffTests
{
    [Fact]
    public async Task OnlyTheOpenViewOfTheSessionGetsTheStoredLogin()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: true);
        FakeLauncher launcher = new();
        using GameViewSwitch sw = new(app.Host, launcher, GameViewSwitchTests.Fast with { MaxCredentialRequests = 2 });

        await sw.OpenAsync();
        string session = launcher.Launched[0];
        Assert.Null(sw.TakeCredentials(session));                 // not connected yet
        GameViewHello hello = sw.Connect(session)!;
        Assert.Equal("hero", hello.AutoLoginAccount);
        Assert.DoesNotContain(FakeAqw.Password, hello.OptionsJson);
        Assert.Null(sw.TakeCredentials("00000000000000000000000000000000"));

        GameViewCredentials login = sw.TakeCredentials(session)!;
        Assert.Equal(("hero", FakeAqw.Password), (login.Account, login.Password));
        Assert.DoesNotContain(FakeAqw.Password, login.ToString());
        Assert.NotNull(sw.TakeCredentials(session));
        Assert.Null(sw.TakeCredentials(session));                  // at most MaxCredentialRequests per view

        sw.ViewClosing(session, "closed by the user", true, "in game after 3 s");
        await sw.Returning;
        Assert.Null(sw.TakeCredentials(session));                  // closed: nothing more

        // Nowhere: not in the launch (only the session id), the log, the settings or any file the app wrote.
        Assert.All(launcher.Launched, s => Assert.Matches("^[0-9A-F]{32}$", s));
        Assert.DoesNotContain(app.Engine.GetRecentLog(), l => l.Message.Contains(FakeAqw.Password, StringComparison.Ordinal));
        Assert.Contains(app.Engine.GetRecentLog(), l => l.Source == "gameview" && l.Message.Contains("Auto-login", StringComparison.Ordinal));
        foreach (string file in Directory.EnumerateFiles(app.Paths.FilesDir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(FakeAqw.Password, File.ReadAllText(file));
    }

    [Fact]
    public async Task WithoutARememberedPasswordNothingIsOffered()
    {
        await using TestApp app = new();
        await app.Host.LoginAsync("hero", FakeAqw.Password, null, remember: false);
        FakeLauncher launcher = new();
        using GameViewSwitch sw = new(app.Host, launcher, GameViewSwitchTests.Fast);
        await sw.OpenAsync();
        GameViewHello hello = sw.Connect(launcher.Launched[0])!;
        Assert.Null(hello.AutoLoginAccount);
        Assert.Null(sw.TakeCredentials(launcher.Launched[0]));
        Assert.Null(sw.Plan().AutoLoginAccount);
    }

    [Fact]
    public void TheRecorderRedactsTheLoginOnceRegistered()
    {
        GameViewRecorder rec = new(() => 1);
        rec.AddSecret(FakeAqw.Password);
        rec.Console("Error: login failed for " + FakeAqw.Password, "ERROR");
        rec.Event("x", "pass=" + FakeAqw.Password);
        string report = rec.BuildReport();
        Assert.DoesNotContain(FakeAqw.Password, report);
        Assert.Contains("(secret)", report);
    }

    private static string AndroidHead()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Skua.App.Droid", "Skua.App.Droid.csproj")))
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("Skua.App.Droid.csproj not found above the test output.");
        return Path.Combine(dir, "Skua.App.Droid");
    }

    [GeneratedRegex(@"PutExtra\(([^,]+),")]
    private static partial Regex PutExtraKey();

    [Fact]
    public void TheAndroidViewPassesNoCredentialThroughIntentsFilesOrLogs()
    {
        string head = AndroidHead();
        string[] sources = Directory.GetFiles(Path.Combine(head, "GameView"), "*.cs");
        Assert.Contains(sources, s => s.EndsWith("GameViewActivity.cs", StringComparison.Ordinal));
        foreach (string path in sources)
        {
            string code = File.ReadAllText(path);
            // Intent extras: the session id, and the report's subject and text in the share sheet; nothing else.
            foreach (Match m in PutExtraKey().Matches(code))
                Assert.Contains(m.Groups[1].Value.Trim(), new[] { "GameViewLinkProtocol.ExtraSession", "Intent.ExtraSubject", "Intent.ExtraText" });
            // No logcat, no files written, no shared preferences in the view or the link.
            Assert.DoesNotContain("Android.Util.Log.", code);
            Assert.DoesNotContain("Log.Info(", code);
            Assert.DoesNotContain("Log.Warn(", code);
            Assert.DoesNotContain("File.Write", code);
            Assert.DoesNotContain("GetSharedPreferences", code);
            Assert.DoesNotContain("CookieManager.Instance?.SetCookie", code);
            // The password is never reported: not to the recorder (except as a secret to redact), not to the crash log.
            foreach (string line in code.Split('\n').Where(l => l.Contains("password", StringComparison.OrdinalIgnoreCase)))
            {
                Assert.DoesNotContain("_rec.Event(", line);
                Assert.DoesNotContain("CrashCapture", line);
                Assert.DoesNotContain("Toast", line);
            }
        }
        // The password key is written in exactly one place: the link's reply Bundle.
        string link = File.ReadAllText(Path.Combine(head, "GameView", "GameViewLink.cs"));
        Assert.Single(Regex.Matches(link, @"PutString\(GameViewLinkProtocol\.KeyPassword"));
        Assert.DoesNotContain("KeyPassword", File.ReadAllText(Path.Combine(head, "EngineRuntime.cs")));

        // The page keeps it only in a closure until the game's login call, never in storage, never in a record.
        string js = File.ReadAllText(Path.Combine(head, "GameView", "web", "gameview.js"));
        Assert.DoesNotContain("localStorage", js);
        Assert.DoesNotContain("sessionStorage", js);
        Assert.DoesNotContain("document.cookie", js);
        foreach (string line in js.Split('\n').Where(l => l.Contains("emit(", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("pendingLogin.p", line);
            Assert.DoesNotContain("l.p", line);
            Assert.DoesNotContain("l.u", line);
        }
    }
}
