using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using Skua.App.Droid.Core.Memory;
using Skua.App.Droid.Core.Platform;
using Skua.App.Droid.Core.Scripts;
using Skua.App.Droid.Core.Security;
using Skua.App.Droid.Core.Settings;
using System.Text;

namespace Skua.App.Droid.Tests;

public class RingBufferAndScrubberTests
{
    [Fact]
    public void RingBufferKeepsTheNewestInOrder()
    {
        RingBuffer<int> buffer = new(3);
        for (int i = 1; i <= 5; i++)
            buffer.Add(i);
        Assert.Equal([3, 4, 5], buffer.Snapshot());
        buffer.Clear();
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void ScrubberRemovesRegisteredSecretsOnly()
    {
        SecretScrubber s = new();
        s.Add("hunter2");
        s.Add("abc"); // too short to be registered
        Assert.Equal($"pw={SecretScrubber.Marker} abc", s.Scrub("pw=hunter2 abc"));
        s.Clear();
        Assert.Equal("pw=hunter2", s.Scrub("pw=hunter2"));
    }

    [Fact]
    public void ProcStatusLinesParseToBytes()
    {
        Assert.Equal(1234L * 1024, ProcMemoryProbe.ParseKb("VmRSS:\t    1234 kB"));
        Assert.Equal(-1, ProcMemoryProbe.ParseKb("VmRSS:"));
        MemorySample live = new ProcMemoryProbe().Read();
        Assert.True(live.RssBytes > 0);
    }
}

public class MemoryWatchdogTests
{
    private const long Mb = 1024 * 1024;

    private static (MemoryWatchdog Dog, ManualTime Time) New(long thresholdMb = 1000, bool auto = true)
    {
        ManualTime time = new();
        return (new MemoryWatchdog(new MemoryWatchdogOptions { ThresholdBytes = thresholdMb * Mb, AutoRestart = auto }, null, time), time);
    }

    [Fact]
    public void RestartsOnlyAfterUptimeAndIdleGrace()
    {
        (MemoryWatchdog dog, ManualTime time) = New();
        Assert.Equal(MemoryVerdict.Normal, dog.Evaluate(300 * Mb, scriptBusy: false));
        Assert.Equal(MemoryVerdict.RestartPending, dog.Evaluate(1200 * Mb, scriptBusy: false)); // uptime too short
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(MemoryVerdict.RestartNow, dog.Evaluate(1200 * Mb, scriptBusy: false));
        Assert.Equal(MemoryVerdict.RestartPending, dog.Evaluate(1200 * Mb, scriptBusy: false)); // fires once
    }

    [Fact]
    public void NeverRestartsWhileAScriptRuns()
    {
        (MemoryWatchdog dog, ManualTime time) = New();
        dog.Evaluate(300 * Mb, scriptBusy: true);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(MemoryVerdict.RestartPending, dog.Evaluate(1500 * Mb, scriptBusy: true));
        Assert.Equal(MemoryVerdict.RestartPending, dog.Evaluate(1500 * Mb, scriptBusy: false)); // idle grace starts now
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(MemoryVerdict.RestartNow, dog.Evaluate(1500 * Mb, scriptBusy: false));
    }

    [Fact]
    public void ThresholdBelowStartupMemoryIsReportedNotLooped()
    {
        (MemoryWatchdog dog, ManualTime time) = New(thresholdMb: 400);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(MemoryVerdict.ThresholdTooLow, dog.Evaluate(500 * Mb, scriptBusy: false));
    }

    [Fact]
    public void RecentAutomaticRestartBlocksAnother()
    {
        ManualTime time = new();
        MemoryWatchdog dog = new(new MemoryWatchdogOptions { ThresholdBytes = 1000 * Mb }, time.GetUtcNow(), time);
        dog.Evaluate(300 * Mb, false);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(MemoryVerdict.RestartPending, dog.Evaluate(1200 * Mb, false));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(MemoryVerdict.RestartNow, dog.Evaluate(1200 * Mb, false));
    }

    [Fact]
    public void AutoRestartOffOnlyOffers()
    {
        (MemoryWatchdog dog, ManualTime time) = New(auto: false);
        dog.Evaluate(300 * Mb, false);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(MemoryVerdict.RestartPending, dog.Evaluate(1200 * Mb, false));
    }
}

public class CredentialVaultTests
{
    [Fact]
    public void PasswordIsBoundToItsAccount()
    {
        MemorySecretStore store = new();
        CredentialVault vault = new(store);
        vault.Save("Hero", "pässwörd");
        Assert.Equal("pässwörd", vault.TryLoad("hero"));
        Assert.Null(vault.TryLoad("Villain"));
        Assert.Equal("Hero", vault.StoredAccount());
        vault.Forget();
        Assert.Null(vault.TryLoad("Hero"));
    }

    [Fact]
    public void CorruptEntryIsDeleted()
    {
        MemorySecretStore store = new();
        store.Entries[CredentialVault.EntryName] = [9, 9, 9, 9, 9];
        CredentialVault vault = new(store);
        Assert.Null(vault.TryLoad("Hero"));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void EncodingRoundTripsAndRejectsGarbage()
    {
        byte[] blob = CredentialVault.Encode("Hero", "pw:with\0odd");
        Assert.True(CredentialVault.TryDecode(blob, out string? user, out string? pass));
        Assert.Equal(("Hero", "pw:with\0odd"), (user, pass));
        Assert.False(CredentialVault.TryDecode(blob.AsSpan(0, 5), out _, out _));
        blob[0] = 2;
        Assert.False(CredentialVault.TryDecode(blob, out _, out _));
    }

    [Fact]
    public void UnavailableStoreRemembersNothing()
    {
        CredentialVault vault = new(new UnavailableSecretStore());
        vault.Save("Hero", "pw");
        Assert.False(vault.IsAvailable);
        Assert.Null(vault.TryLoad("Hero"));
    }
}

public class SettingsAndResumeTests
{
    [Fact]
    public void SettingsRoundTripAndHoldNoSecret()
    {
        string path = Path.Combine(Sandbox.NewDir(), "droid", "settings.json");
        SettingsStore store = new(path);
        store.Update(s => s with { AccountName = " Hero ", RememberPassword = true, MemoryThresholdMb = 50 });
        SettingsStore again = new(path);
        Assert.Equal("Hero", again.Current.AccountName);
        Assert.Equal(AppSettings.MinMemoryThresholdMb, again.Current.MemoryThresholdMb);
        Assert.DoesNotContain("pass", File.ReadAllText(path).Replace("RememberPassword", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptSettingsFallBackToDefaults()
    {
        string path = Path.Combine(Sandbox.NewDir(), "settings.json");
        File.WriteAllText(path, "{not json");
        Assert.Equal(new AppSettings(), new SettingsStore(path).Current);
    }

    [Fact]
    public void ResumeIsTakenOnceAndExpires()
    {
        ManualTime time = new();
        string path = Path.Combine(Sandbox.NewDir(), "resume.json");
        ResumeStore store = new(path, time);
        store.Write(new ResumeRequest(true, "Hero", "Sandbox", "memory", time.GetUtcNow()));
        Assert.Equal("Hero", store.Take()!.Account);
        Assert.Null(store.Take());

        store.Write(new ResumeRequest(true, "Hero", "Sandbox", "memory", time.GetUtcNow()));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(store.Take());
        Assert.False(File.Exists(path));
    }
}

public class ScriptLibraryTests
{
    private static PickedFile File(string path, string content) =>
        new(path, content.Length, () => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content))));

    [Theory]
    [InlineData("../evil.cs")]
    [InlineData("a/../../evil.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b.cs")]
    [InlineData("a/./b.cs")]
    [InlineData("C:evil.cs")]
    [InlineData("bad\u0001name.cs")]
    [InlineData(" lead.cs")]
    public void UnsafePathsAreRejected(string path)
    {
        Assert.Null(ScriptLibrary.SanitizeRelativePath(path));
    }

    [Fact]
    public async Task ImportKeepsFoldersAndRefusesWhatItShould()
    {
        ScriptLibrary lib = new(Sandbox.NewDir());
        ImportResult result = await lib.ImportAsync(
        [
            File("Scripts/CoreBots.cs", "class CoreBots {}"),
            File("Scripts/Story/Quest.cs", "class Quest {}"),
            File("Scripts/data.json", "{}"),
            File("Scripts/../escape.cs", "x"),
            File("Scripts/tool.exe", "MZ"),
            File("Cached-Scripts/123-CoreBots.dll", "x"),
        ]);
        Assert.Equal(3, result.Imported);
        Assert.Equal(3, result.Skipped.Count);
        Assert.True(System.IO.File.Exists(Path.Combine(lib.Root, "Scripts", "Story", "Quest.cs")));
        Assert.False(System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(lib.Root)!, "escape.cs")));

        IReadOnlyList<ScriptEntry> top = lib.List("Scripts");
        Assert.Equal(["Story", "CoreBots.cs", "data.json"], top.Select(e => e.Name));
        Assert.True(top[1].IsScript);
        Assert.Single(lib.Search("quest"));
    }

    [Fact]
    public async Task OversizedFilesAreSkipped()
    {
        ScriptLibrary lib = new(Sandbox.NewDir());
        PickedFile huge = new("big.cs", null, () => Task.FromResult<Stream>(new MemoryStream(new byte[ScriptLibrary.MaxFileBytes + 1])));
        ImportResult result = await lib.ImportAsync([huge]);
        Assert.Equal(0, result.Imported);
        Assert.Empty(Directory.GetFiles(lib.Root));
    }

    [Fact]
    public void CompilerCacheIsHiddenAndRootCannotBeDeleted()
    {
        ScriptLibrary lib = new(Sandbox.NewDir());
        Directory.CreateDirectory(Path.Combine(lib.Root, "Cached-Scripts"));
        Assert.Empty(lib.List());
        Assert.Throws<ArgumentException>(() => lib.Delete(""));
        Assert.Throws<ArgumentException>(() => lib.Delete("../x"));
    }

    [Fact]
    public void AppPathsMatchSkuaCoreLayout()
    {
        AppPaths paths = new("/data/user/0/com.vibeskua.droid/files", "/data/user/0/com.vibeskua.droid/no_backup", "/data/user/0/com.vibeskua.droid/cache");
        Assert.Equal("/data/user/0/com.vibeskua.droid/files/.config/VibeSkua/Scripts", paths.ScriptsDir);
        Assert.Equal("/data/user/0/com.vibeskua.droid/no_backup/secrets", paths.SecretsDir);
        Assert.StartsWith(paths.ScriptsDir, paths.CompiledScriptCacheDir);
    }
}
