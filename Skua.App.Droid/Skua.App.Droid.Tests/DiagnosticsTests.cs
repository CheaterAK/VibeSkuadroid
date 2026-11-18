using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Core.Security;

namespace Skua.App.Droid.Tests;

public class DiagnosticsTests
{
    private static readonly KeyValuePair<string, string>[] Context = [new("device", "Test Phone"), new("android", "13 (API 33)")];

    [Fact]
    public void CrashReportRoundTripsAndIsClearedOnce()
    {
        DiagnosticsStore store = new(Sandbox.NewDir(), new ManualTime());
        Assert.False(store.HasPendingReport);
        Assert.Null(store.ReadPendingReport());

        Assert.True(store.WriteCrashReport("AndroidEnvironment.UnhandledExceptionRaiser", Throw("boom").ToString(), Context));
        Assert.True(store.HasPendingReport);
        string report = store.ReadPendingReport()!;
        Assert.Contains("source: AndroidEnvironment.UnhandledExceptionRaiser", report);
        Assert.Contains("device: Test Phone", report);
        Assert.Contains("System.InvalidOperationException: boom", report);
        Assert.Contains(nameof(Throw), report); // the stack trace is kept

        store.ClearPendingReport();
        Assert.False(store.HasPendingReport);
    }

    [Fact]
    public void FirstReportWinsAndLaterOnesGoToTheLog()
    {
        DiagnosticsStore store = new(Sandbox.NewDir(), new ManualTime());
        Assert.True(store.WriteCrashReport("first", "first failure", Context));
        Assert.False(store.WriteCrashReport("second", "second failure", Context));
        Assert.Contains("first failure", store.ReadPendingReport());
        Assert.DoesNotContain("second failure", store.ReadPendingReport());
        Assert.Contains(store.ReadLogTail(10), l => l.Contains("second failure", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisteredSecretsAndCredentialPairsNeverReachTheFiles()
    {
        DiagnosticText.Secrets.Add("Sup3rS3cretPw!");
        DiagnosticText.Secrets.Add("tok-7f3a9b2c4d");
        try
        {
            DiagnosticsStore store = new(Sandbox.NewDir(), new ManualTime());
            string details = "HttpRequestException: login with Sup3rS3cretPw! failed\n"
                + "body: user=hero&pass=hunter22&option=1\n"
                + "json: {\"sToken\":\"tok-7f3a9b2c4d\",\"password\": \"other-pw-1\"}\n"
                + "header Authorization: Bearer abc.def.ghi\n"
                + "cookie=sid123456";
            store.WriteCrashReport("test", details, [new("note", "token=zzzz9999")]);
            store.Log("retrying with password=letmein99");

            string onDisk = File.ReadAllText(store.CrashReportPath) + File.ReadAllText(store.LogPath);
            foreach (string secret in new[] { "Sup3rS3cretPw!", "tok-7f3a9b2c4d", "hunter22", "other-pw-1", "abc.def.ghi", "sid123456", "zzzz9999", "letmein99" })
                Assert.DoesNotContain(secret, onDisk);
            Assert.Contains(SecretScrubber.Marker, onDisk);
            // Ordinary text survives.
            Assert.Contains("user=hero", onDisk);
            Assert.Contains("HttpRequestException", onDisk);
        }
        finally
        {
            DiagnosticText.Secrets.Clear();
        }
    }

    [Fact]
    public void ScrubLeavesNormalStackTracesAlone()
    {
        string trace = Throw("Value cannot be null. (Parameter 'session')").ToString();
        Assert.Equal(trace, DiagnosticText.Scrub(trace));
        Assert.Equal(string.Empty, DiagnosticText.Scrub(null));
    }

    [Fact]
    public void DescribeFlattensAggregateExceptions()
    {
        AggregateException aggregate = new(new AggregateException(Throw("inner one")), Throw("inner two"));
        string text = DiagnosticText.Describe(aggregate);
        Assert.Contains("inner one", text);
        Assert.Contains("inner two", text);
    }

    [Fact]
    public void MarkerRoundTripsAndClassifiesHowAProcessEnded()
    {
        ManualTime time = new();
        DiagnosticsStore store = new(Sandbox.NewDir(), time);
        Assert.Null(store.ReadMarker());

        DateTimeOffset started = time.GetUtcNow();
        store.WriteMarker(4242, started, StartupStep.EngineInitStarted);
        StartupMarker marker = store.ReadMarker()!;
        Assert.Equal(4242, marker.ProcessId);
        Assert.Equal(StartupStep.EngineInitStarted, marker.LastStep);
        Assert.Equal(started, marker.StartedAt);
        Assert.True(marker.EndedUnexpectedly);
        Assert.True(marker.DiedInEngineInit);
        Assert.Contains("step 3 (EngineInitStarted)", DiagnosticsStore.DescribeMarker(marker));

        // A background process (service or receiver only) that was later reclaimed is not a crash.
        Assert.False((marker with { LastStep = StartupStep.ApplicationReady }).EndedUnexpectedly);
        Assert.False((marker with { LastStep = StartupStep.LauncherActivity }).EndedUnexpectedly);
        // Dying while the UI comes up is.
        Assert.True((marker with { LastStep = StartupStep.AvaloniaInitStarted }).EndedUnexpectedly);
        Assert.True((marker with { LastStep = StartupStep.MainActivityResumed }).EndedUnexpectedly);
        // A completed start is not.
        Assert.False((marker with { LastStep = StartupStep.FirstFrame }).EndedUnexpectedly);
        Assert.True((marker with { LastStep = StartupStep.FirstFrame }).Completed);
    }

    [Fact]
    public void CorruptMarkerReadsAsNone()
    {
        DiagnosticsStore store = new(Sandbox.NewDir(), new ManualTime());
        File.WriteAllText(store.MarkerPath, "garbage\nstep=999\n");
        Assert.Null(store.ReadMarker());
    }

    [Fact]
    public void LogRollsOverAndKeepsTheTail()
    {
        DiagnosticsStore store = new(Sandbox.NewDir(), new ManualTime());
        string filler = new('x', 1000);
        for (int i = 0; i < 300; i++)
            store.Log($"line {i} {filler}");
        Assert.True(new FileInfo(store.LogPath).Length <= DiagnosticsStore.MaxLogBytes);
        Assert.True(File.Exists(store.LogPath + ".1"));
        IReadOnlyList<string> tail = store.ReadLogTail(3);
        Assert.Equal(3, tail.Count);
        Assert.Contains("line 299 ", tail[^1]);
    }

    [Fact]
    public void AnUnwritableDirectoryNeverThrows()
    {
        string file = Path.Combine(Sandbox.NewDir(), "not-a-dir");
        File.WriteAllText(file, "x");
        DiagnosticsStore store = new(Path.Combine(file, "diagnostics"), new ManualTime());
        Assert.False(store.WriteCrashReport("test", "details", Context));
        store.Log("nothing");
        store.WriteMarker(1, DateTimeOffset.UtcNow, StartupStep.ProcessStarted);
        Assert.Null(store.ReadMarker());
        Assert.Empty(store.ReadLogTail(5));
        Assert.False(store.HasPendingReport);
    }

    private static Exception Throw(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }
}
