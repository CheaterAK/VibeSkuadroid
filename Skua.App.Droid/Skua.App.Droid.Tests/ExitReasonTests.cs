using System.Text;
using Skua.App.Droid.Core.Diagnostics;
using Skua.App.Droid.Core.Security;

namespace Skua.App.Droid.Tests;

public class ExitReasonTests
{
    private const string Main = "com.vibeskua.droid";
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly KeyValuePair<string, string>[] Context = [new("device", "SM-G998B")];

    private static ProcessExitRecord Exit(ExitReason reason, int pid = 100, int importance = 125, int status = 0, string process = Main,
        DateTimeOffset? at = null, ProcessActivity? summary = null, string? description = null) =>
        new(at ?? T0, pid, process, (int)reason, status, importance, 512 * 1024, 700 * 1024, description, summary);

    private static ProcessActivity Busy(UiState ui = UiState.Background) => new(ui, "Running", "Online", "heartbeat", T0);

    private static ProcessActivity Idle(UiState ui = UiState.Background) => new(ui, "Idle", "Offline", "heartbeat", T0);

    [Theory]
    [InlineData(ExitReason.CrashNative)]
    [InlineData(ExitReason.Crash)]
    [InlineData(ExitReason.Anr)]
    [InlineData(ExitReason.InitializationFailure)]
    [InlineData(ExitReason.ExcessiveResourceUsage)]
    [InlineData(ExitReason.DependencyDied)]
    public void CrashesAndAnrsAreAlwaysReported(ExitReason reason)
    {
        Assert.Equal(ExitVerdict.Report, ProcessExitClassifier.Classify(Exit(reason, importance: 400), Main, null));
        Assert.Equal(ExitVerdict.Report, ProcessExitClassifier.Classify(Exit(reason), Main, Idle()));
    }

    [Fact]
    public void KillsAreReportedOnlyWhenTheAppWasDoingSomething()
    {
        // Low memory kill of the foreground-service process (a session or script): reported.
        Assert.Equal(ExitVerdict.Report, ProcessExitClassifier.Classify(Exit(ExitReason.LowMemory, importance: 125), Main, null));
        // Of an idle cached process: normal Android housekeeping.
        Assert.Equal(ExitVerdict.LogOnly, ProcessExitClassifier.Classify(Exit(ExitReason.LowMemory, importance: 400), Main, Idle()));
        // Cached by importance but a script was running by our own record: reported.
        Assert.Equal(ExitVerdict.Report, ProcessExitClassifier.Classify(Exit(ExitReason.LowMemory, importance: 400), Main, Busy()));
        Assert.Equal(ExitVerdict.Report, ProcessExitClassifier.Classify(Exit(ExitReason.Signaled, status: 9, importance: 200), Main, null));
        Assert.Equal(ExitVerdict.LogOnly, ProcessExitClassifier.Classify(Exit(ExitReason.Signaled, status: 9, importance: 400), Main, Idle()));
        // OTHER ("remove task", updates) needs our own record of a running script or a visible UI.
        Assert.Equal(ExitVerdict.LogOnly, ProcessExitClassifier.Classify(Exit(ExitReason.Other, importance: 125), Main, null));
        Assert.Equal(ExitVerdict.Report, ProcessExitClassifier.Classify(Exit(ExitReason.Other, importance: 125), Main, Busy()));
        Assert.Equal(ExitVerdict.Report, ProcessExitClassifier.Classify(Exit(ExitReason.Unknown), Main, Idle(UiState.Foreground)));
        // Swiped away in Recents while a script ran: the user's choice.
        Assert.Equal(ExitVerdict.LogOnly, ProcessExitClassifier.Classify(Exit(ExitReason.Other, description: "remove task"), Main, Busy()));
    }

    [Fact]
    public void UserActionsPlannedRestartsAndOtherProcessesAreNotReported()
    {
        Assert.Equal(ExitVerdict.LogOnly, ProcessExitClassifier.Classify(Exit(ExitReason.UserRequested), Main, Busy()));
        Assert.Equal(ExitVerdict.LogOnly, ProcessExitClassifier.Classify(Exit(ExitReason.UserStopped), Main, Busy()));
        Assert.Equal(ExitVerdict.LogOnly, ProcessExitClassifier.Classify(Exit(ExitReason.PackageUpdated), Main, Busy()));
        Assert.Equal(ExitVerdict.LogOnly, ProcessExitClassifier.Classify(Exit(ExitReason.ExitSelf), Main, Busy()));
        Assert.Equal(ExitVerdict.Report, ProcessExitClassifier.Classify(Exit(ExitReason.ExitSelf, status: 134), Main, null));
        // The restart kills the process on purpose.
        ProcessActivity planned = Busy() with { Planned = "restart" };
        Assert.Equal(ExitVerdict.LogOnly, ProcessExitClassifier.Classify(Exit(ExitReason.Signaled, status: 9, importance: 100), Main, planned));
        // The restart trampoline process ends itself.
        Assert.Equal(ExitVerdict.LogOnly, ProcessExitClassifier.Classify(Exit(ExitReason.Signaled, process: Main + ":restart"), Main, null));
    }

    [Fact]
    public void OnlyNewRecordsAreSelectedNewestFirst()
    {
        ProcessExitRecord old = Exit(ExitReason.LowMemory, pid: 1, at: T0 - TimeSpan.FromDays(10));
        ProcessExitRecord a = Exit(ExitReason.CrashNative, pid: 2, at: T0 - TimeSpan.FromHours(2));
        ProcessExitRecord b = Exit(ExitReason.Anr, pid: 3, at: T0 - TimeSpan.FromHours(1));
        ProcessExitRecord self = Exit(ExitReason.Crash, pid: 99, at: T0);

        // First run: only the recent window, never the current process.
        IReadOnlyList<ProcessExitRecord> fresh = ProcessExitClassifier.SelectNew([old, a, b, self], null, 99, T0, TimeSpan.FromDays(3));
        Assert.Equal(new[] { b, a }, fresh);

        ExitHistoryMark? mark = ExitHistoryMark.Advance(null, fresh);
        Assert.Equal(b.At, mark!.At);
        Assert.Empty(ProcessExitClassifier.SelectNew([old, a, b], mark, 99, T0, TimeSpan.FromDays(3)));

        // Same timestamp, different pid: still new; the mark survives a round trip through its file.
        ProcessExitRecord twin = Exit(ExitReason.CrashNative, pid: 4, at: b.At);
        ExitHistoryMark reread = ExitHistoryMark.Parse(mark.Serialize())!;
        Assert.Equal(new[] { twin }, ProcessExitClassifier.SelectNew([a, b, twin], reread, 99, T0, TimeSpan.FromDays(3)));
        ExitHistoryMark after = ExitHistoryMark.Advance(reread, [twin])!;
        Assert.Equal(new[] { 3, 4 }, after.ProcessIds.Order());
        Assert.Null(ExitHistoryMark.Parse("garbage"));
    }

    [Fact]
    public void ActivitySummaryFitsAndroidsLimitAndRoundTrips()
    {
        ProcessActivity activity = new(UiState.Background, "Running", "Reconnecting", "surfaceDestroyed", T0, null, 812);
        byte[] summary = activity.ToSummary();
        Assert.True(summary.Length <= ProcessActivity.MaxSummaryBytes);
        Assert.Equal(activity, ProcessActivity.FromSummary(summary));

        ProcessActivity noisy = new(UiState.Foreground, new string('x', 200), "a|b=c\nd", new string('e', 300), T0, new string('p', 100), int.MaxValue);
        byte[] bounded = noisy.ToSummary();
        Assert.True(bounded.Length <= ProcessActivity.MaxSummaryBytes);
        Assert.Equal(UiState.Foreground, ProcessActivity.FromSummary(bounded)!.Ui);

        Assert.Null(ProcessActivity.FromSummary(null));
        Assert.Null(ProcessActivity.FromSummary(Encoding.ASCII.GetBytes("v2|something")));
    }

    [Fact]
    public void MarkerCarriesTheLastHeartbeatAndClassifiesABusyDeath()
    {
        DiagnosticsStore store = new(Sandbox.NewDir(), new ManualTime());
        ProcessActivity busy = new(UiState.Background, "Running", "Online", "heartbeat", T0, null, 640);
        store.WriteMarker(77, T0, StartupStep.FirstFrame, EngineInitPhase.Finished, busy);
        StartupMarker marker = store.ReadMarker()!;
        Assert.Equal(busy, marker.Activity);
        Assert.False(marker.EndedUnexpectedly); // startup had completed...
        Assert.True(marker.DiedWhileBusy);      // ...but a script was running
        Assert.Contains("script Running", DiagnosticsStore.DescribeBusyDeath(marker));

        Assert.False((marker with { Activity = busy with { Planned = "restart" } }).DiedWhileBusy);
        Assert.False((marker with { Activity = busy with { Script = "Idle" } }).DiedWhileBusy);
        // Markers written by older builds have no activity lines.
        store.WriteMarker(78, T0, StartupStep.FirstFrame, EngineInitPhase.Finished);
        Assert.Null(store.ReadMarker()!.Activity);
        Assert.False(store.ReadMarker()!.DiedWhileBusy);
    }

    [Fact]
    public void ReportSaysWhatAndroidRecordedAndIsScrubbed()
    {
        DiagnosticText.Secrets.Add("Sup3rS3cretPw!");
        try
        {
            DiagnosticsStore store = new(Sandbox.NewDir(), new ManualTime());
            ProcessExitRecord crash = Exit(ExitReason.CrashNative, pid: 4321, status: 11, importance: 100, summary: Busy(UiState.Foreground),
                description: "crash");
            ProcessExitRecord earlier = Exit(ExitReason.LowMemory, pid: 4000, at: T0 - TimeSpan.FromHours(1));
            string details = ProcessExitReport.Format(crash, crash.Summary, "Abort message: 'login password=hunter22 with Sup3rS3cretPw!'", null, [crash, earlier]);
            Assert.True(store.WriteCrashReport(ProcessExitReport.Source(crash), details, Context));

            string report = store.ReadPendingReport()!;
            Assert.Contains("CRASH_NATIVE", report);
            Assert.Contains("signal 11 SIGSEGV", report);
            Assert.Contains("importance at death: 100 (foreground)", report);
            Assert.Contains("PSS 512 MB, RSS 700 MB", report);
            Assert.Contains("UI foreground, script Running", report);
            Assert.Contains("pid 4000", report); // the other exit is listed
            Assert.DoesNotContain("hunter22", report);
            Assert.DoesNotContain("Sup3rS3cretPw!", report);
            Assert.Contains(SecretScrubber.Marker, report);

            // A later record of the same death is added to the pending report instead of being dropped.
            Assert.True(store.AppendToPendingReport("how Android says the run ended", "token=abc123def"));
            report = store.ReadPendingReport()!;
            Assert.Contains("how Android says the run ended", report);
            Assert.DoesNotContain("abc123def", report);
            store.ClearPendingReport();
            Assert.False(store.AppendToPendingReport("x", "y"));
        }
        finally
        {
            DiagnosticText.Secrets.Clear();
        }
    }

    [Fact]
    public void ReportNotesAMissingTraceAndBoundsALongOne()
    {
        string none = ProcessExitReport.Format(Exit(ExitReason.CrashNative), null, null, "Android 11 keeps no native crash tombstone", []);
        Assert.Contains("Android 11 keeps no native crash tombstone", none);
        Assert.Contains("last app state: (not recorded)", none);

        string longTrace = ProcessExitReport.Format(Exit(ExitReason.Anr), null, new string('t', ProcessExitReport.MaxTraceChars * 2), null, []);
        Assert.True(longTrace.Length < ProcessExitReport.MaxTraceChars + 4096);
        Assert.Contains("(trace truncated)", longTrace);
    }

    [Fact]
    public void AnrTextTracesPassThrough()
    {
        byte[] anr = Encoding.UTF8.GetBytes("----- pid 4321 at 2026-11-19 10:00:00 -----\nCmd line: com.vibeskua.droid\n\n\"main\" prio=5 tid=1 Native\n  native: #00 pc 000a1b2c  /system/lib64/libc.so (__epoll_pwait+8)\n");
        string text = TraceText.Format(anr, truncated: false);
        Assert.Contains("\"main\" prio=5 tid=1 Native", text);
        Assert.Contains("only the first", TraceText.Format(anr, truncated: true));
    }

    [Fact]
    public void NativeTombstoneProtobufIsDecoded()
    {
        byte[] tombstone = BuildTombstone();
        Assert.False(TraceText.LooksLikeText(tombstone));
        string text = TraceText.Format(tombstone, truncated: false);
        Assert.Contains("Native crash tombstone", text);
        Assert.Contains("Build fingerprint: 'samsung/p3sxeea/p3s:13/TP1A/G998BXXU7EWCH:user/release-keys'", text);
        Assert.Contains("pid: 4321, tid: 4400", text);
        Assert.Contains("signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x18", text);
        Assert.Contains("Abort message: 'eglSwapBuffers failed'", text);
        Assert.Contains("crashing thread 4400 \"Render Thread\"", text);
        Assert.Contains("#00 pc 000000000004e2b0  /vendor/lib64/egl/libGLES_mali.so (eglp_swap+52)", text);
        Assert.Contains("tid 4321 \"main\"", text);
        Assert.Contains("BufferQueue has been abandoned", text);
    }

    /// <summary>
    /// The S21 Ultra crash of 2026-11-19/20 (.NET 10.0.15, libmonosgen BuildId a81cb065...): the runtime's frames are
    /// named from the embedded sgen table and the report says it is the Mono bridge bug, not the app.
    /// </summary>
    [Fact]
    public void TheMonoBridgeCrashIsSymbolizedAndExplained()
    {
        const string BuildId = "a81cb0659781b5613899a7899e8d4d24da649df7";
        const string Lib = "/data/app/~~x/com.vibeskua.droid-y/base.apk!libmonosgen-2.0.so";
        ulong[] pcs = [0x29d438, 0x29c734, 0x297d24, 0x2b5ed8, 0x2b5004, 0x2b14ec, 0x2b12d4, 0x2adadc, 0x2a02c8];
        Proto worker = new Proto().Varint(1, 18155).Str(2, ".NET TP Worker");
        foreach (ulong pc in pcs)
            worker.Msg(4, new Proto().Varint(1, pc).Str(6, Lib).Str(8, BuildId));
        worker.Msg(4, new Proto().Varint(1, 0xf928).Str(6, "<anonymous:712dfdf000>"));
        byte[] tombstone = new Proto()
            .Varint(5, 18000).Varint(6, 18155)
            .Msg(10, new Proto().Varint(1, 11).Str(2, "SIGSEGV").Varint(3, 1).Str(4, "SEGV_MAPERR").Varint(8, 1).Varint(9, 0x7b3a2262222c22))
            .Msg(16, new Proto().Varint(1, 18155).Msg(2, worker))
            .ToArray();

        string text = TraceText.Format(tombstone, truncated: false);

        Assert.Contains("fault addr 0x7b3a2262222c22", text);
        Assert.Contains("#00 pc 000000000029d438  " + Lib + " [push_object+0x10 (sgen-tarjan-bridge.c)] (BuildId: " + BuildId + ")", text);
        string[] expected =
        [
            "push_object+0x10 (sgen-tarjan-bridge.c)", "processing_stw_step+0xcf8 (sgen-tarjan-bridge.c)",
            "sgen_bridge_processing_stw_step+0x1c (sgen-bridge.c)", "finish_gray_stack+0x114 (sgen-gc.c)",
            "collect_nursery+0x5a0 (sgen-gc.c)", "sgen_perform_collection+0x1f4 (sgen-gc.c)",
            "sgen_ensure_free_space+0xb8 (sgen-gc.c)", "sgen_alloc_obj_nolock+0x3b8 (sgen-alloc.c)",
            "mono_gc_alloc_vector+0x84 (sgen-mono.c)",
        ];
        foreach (string symbol in expected)
            Assert.Contains("[" + symbol + "]", text);
        Assert.Contains("Diagnosis: Crash inside the Mono GC's Java bridge (Tarjan) during a nursery collection", text);
        Assert.Contains("dotnet/runtime#123537", text);
    }

    [Theory]
    [InlineData("major=marksweep-conc", "concurrent major GC")]
    [InlineData("major=marksweep", "serial major GC")]
    [InlineData("nursery-size=64m,major=marksweep", "serial major GC")]
    [InlineData(null, "not set")]
    public void TheGcModeIsDescribed(string? gcParams, string expected)
    {
        Assert.Contains(expected, MonoGcDiagnostics.DescribeGcMode(gcParams));
    }

    [Fact]
    public void UnknownRuntimeBuildsAndOtherLibrariesAreLeftAlone()
    {
        Assert.Null(MonoGcDiagnostics.Symbolize("/x/libmonosgen-2.0.so", "0000", 0x29d438));
        Assert.Null(MonoGcDiagnostics.Symbolize("/x/libc.so", "a81cb0659781b5613899a7899e8d4d24da649df7", 0x29d438));
        Assert.Null(MonoGcDiagnostics.Diagnose([("/apex/libc.so", "1", 0x10)], "main", null));
        Assert.Contains("no symbol table", MonoGcDiagnostics.Diagnose([("/x/libmonosgen-2.0.so", "0000", 0x10)], ".NET TP Worker", null));
        Assert.True(MonoGcDiagnostics.HasTable("A81CB0659781B5613899A7899E8D4D24DA649DF7"));
    }

    /// <summary>The APK must keep the serial major collector until the runtime has the bridge fix (see the csproj).</summary>
    [Fact]
    public void TheAndroidHeadUsesTheSerialMajorCollector()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Skua.App.Droid", "Skua.App.Droid.csproj")))
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("Skua.App.Droid.csproj not found above the test output.");
        System.Xml.Linq.XDocument project = System.Xml.Linq.XDocument.Load(Path.Combine(dir, "Skua.App.Droid", "Skua.App.Droid.csproj"));
        Assert.Contains(project.Descendants("AndroidEnableSGenConcurrent"), e => e.Value.Trim() == "false" && e.Parent?.Attribute("Condition") is null);
    }

    [Fact]
    public void GarbageTracesFallBackToPrintableStrings()
    {
        byte[] junk = [0x00, 0xFF, 0x01, .. Encoding.ASCII.GetBytes("libmonosgen-2.0.so"), 0x02, 0x03, .. Encoding.ASCII.GetBytes("ab"), 0x00, 0xFE, 0x07, 0x06];
        string text = TraceText.Format(junk, truncated: false);
        Assert.Contains("libmonosgen-2.0.so", text);
        Assert.DoesNotContain("ab\n", text);
        Assert.False(TraceText.TryFormatTombstone(junk, out _));
        Assert.Equal("(empty trace)", TraceText.Format([], false));
    }

    // tombstone.proto (system/core/debuggerd/proto) subset: Tombstone, Signal, Thread, BacktraceFrame, LogBuffer.
    private static byte[] BuildTombstone()
    {
        Proto frame = new Proto().Varint(1, 0x4e2b0).Str(4, "eglp_swap").Varint(5, 52).Str(6, "/vendor/lib64/egl/libGLES_mali.so");
        Proto frame2 = new Proto().Varint(1, 0x1234).Str(6, "/data/app/libmonosgen-2.0.so");
        Proto render = new Proto().Varint(1, 4400).Str(2, "Render Thread").Msg(4, frame).Msg(4, frame2);
        Proto main = new Proto().Varint(1, 4321).Str(2, "main").Msg(4, new Proto().Varint(1, 0x99).Str(6, "/apex/libc.so").Str(4, "__epoll_pwait").Varint(5, 8));
        Proto signal = new Proto().Varint(1, 11).Str(2, "SIGSEGV").Varint(3, 1).Str(4, "SEGV_MAPERR").Varint(8, 1).Varint(9, 0x18);
        Proto log = new Proto().Str(1, "main").Msg(2, new Proto().Str(1, "11-19 10:00:00.000").Varint(2, 4321).Varint(3, 4400).Varint(4, 6)
            .Str(5, "BufferQueueProducer").Str(6, "dequeueBuffer: BufferQueue has been abandoned"));
        return new Proto()
            .Varint(1, 3) // arch arm64
            .Str(2, "samsung/p3sxeea/p3s:13/TP1A/G998BXXU7EWCH:user/release-keys")
            .Str(4, "2026-11-19 10:00:00.123+0100")
            .Varint(5, 4321).Varint(6, 4400)
            .Str(9, "com.vibeskua.droid")
            .Msg(10, signal)
            .Str(14, "eglSwapBuffers failed")
            .Msg(16, new Proto().Varint(1, 4400).Msg(2, render))
            .Msg(16, new Proto().Varint(1, 4321).Msg(2, main))
            .Fixed64(17, 42) // a field the decoder skips
            .Msg(18, log)
            .Varint(20, 3600)
            .ToArray();
    }

    private sealed class Proto
    {
        private readonly MemoryStream _out = new();

        public Proto Varint(int field, ulong value)
        {
            Raw((ulong)(field << 3));
            Raw(value);
            return this;
        }

        public Proto Fixed64(int field, ulong value)
        {
            Raw((ulong)(field << 3 | 1));
            _out.Write(BitConverter.GetBytes(value));
            return this;
        }

        public Proto Str(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public Proto Msg(int field, Proto message) => Bytes(field, message.ToArray());

        public byte[] ToArray() => _out.ToArray();

        private Proto Bytes(int field, byte[] bytes)
        {
            Raw((ulong)(field << 3 | 2));
            Raw((ulong)bytes.Length);
            _out.Write(bytes);
            return this;
        }

        private void Raw(ulong value)
        {
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0)
                    b |= 0x80;
                _out.WriteByte(b);
            }
            while (value != 0);
        }
    }
}
