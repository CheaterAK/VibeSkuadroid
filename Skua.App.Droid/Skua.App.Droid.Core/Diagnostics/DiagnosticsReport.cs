using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Memory;
using Skua.App.Droid.Core.Security;
using Skua.Core.Interfaces;
using Skua.Core.Models.Diagnostics;

namespace Skua.App.Droid.Core.Diagnostics;

/// <summary>One labelled value of a Diagnostics section.</summary>
public sealed record InfoRow(string Label, string Value);

/// <summary>A process exit Android reported (one line of the events log) or the pending crash report's title.</summary>
public sealed record ExitRow(string When, string Text);

/// <summary>Memory as the Diagnostics page shows it.</summary>
public sealed record MemoryReading(
    long RssBytes,
    long PeakRssBytes,
    long PssBytes,
    long ManagedBytes,
    long TotalAllocatedBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    int ScriptAssemblies,
    int ThreadPoolThreads,
    long PendingWorkItems,
    int ThresholdMb,
    bool AutoRestart,
    string Verdict);

/// <summary>Skua's runtime helpers (the desktop's Runtime window): what a script registered, read only.</summary>
public sealed record RuntimeReading(
    IReadOnlyList<int> RegisteredQuests,
    bool DropsEnabled,
    IReadOnlyList<string> DropsToPickUp,
    bool BoostsEnabled,
    IReadOnlyList<string> Boosts,
    bool SkuaEventsEnabled,
    IReadOnlyList<DiagnosticEvent> SkuaEvents);

/// <summary>Everything the Diagnostics page shows, read at once on a worker thread.</summary>
public sealed record DiagnosticsReading(
    DateTimeOffset At,
    EngineSnapshot Engine,
    ConnectionDetails? Connection,
    PacketRate? Rate,
    MemoryReading Memory,
    IReadOnlyList<FlashGapRow>? FlashGaps,
    IReadOnlyList<ExitRow> Exits,
    bool PendingCrashReport,
    RuntimeReading? Runtime);

/// <summary>Packets per minute over the sampled window.</summary>
public sealed record PacketRate(double InPerMinute, double OutPerMinute, TimeSpan Window);

/// <summary>
/// Packets per minute from the connection's running counters, sampled at each refresh over the last minute. A new
/// connection (counters lower than before) starts over.
/// </summary>
public sealed class PacketRateMeter
{
    private readonly Queue<(DateTimeOffset At, long In, long Out)> _samples = new();
    private readonly TimeSpan _window;

    public PacketRateMeter(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>The shortest span a rate is computed over.</summary>
    public static TimeSpan MinimumSpan { get; } = TimeSpan.FromSeconds(3);

    public PacketRate? Add(DateTimeOffset at, ConnectionDetails? connection)
    {
        if (connection is null)
        {
            _samples.Clear();
            return null;
        }
        if (_samples.Count > 0)
        {
            (DateTimeOffset _, long lastIn, long lastOut) = _samples.Last();
            if (connection.PacketsIn < lastIn || connection.PacketsOut < lastOut)
                _samples.Clear();
        }
        _samples.Enqueue((at, connection.PacketsIn, connection.PacketsOut));
        while (_samples.Count > 2 && at - _samples.Peek().At > _window)
            _samples.Dequeue();
        (DateTimeOffset firstAt, long firstIn, long firstOut) = _samples.Peek();
        TimeSpan span = at - firstAt;
        if (span < MinimumSpan)
            return null;
        double minutes = span.TotalMinutes;
        return new PacketRate((connection.PacketsIn - firstIn) / minutes, (connection.PacketsOut - firstOut) / minutes, span);
    }
}

/// <summary>Reads, formats and scrubs the Diagnostics page's data and its share bundle.</summary>
public static partial class DiagnosticsReport
{
    /// <summary>The events log line Android's exit records are written as (see <c>ExitReasons</c> in the Android head).</summary>
    public const string ExitLinePrefix = "previous exit (";

    /// <summary>Process exits in the events log, newest first (at most <paramref name="max"/>).</summary>
    public static IReadOnlyList<ExitRow> Exits(IEnumerable<string> logLines, int max = 10)
    {
        List<ExitRow> rows = new();
        foreach (string line in logLines.Reverse())
        {
            int at = line.IndexOf(ExitLinePrefix, StringComparison.Ordinal);
            if (at < 0)
                continue;
            rows.Add(new ExitRow(line[..at].Trim(), DiagnosticText.Scrub(line[at..])));
            if (rows.Count >= max)
                break;
        }
        return rows;
    }

    /// <summary>Memory now (PSS is read from /proc: a few milliseconds; call on a worker thread).</summary>
    public static MemoryReading ReadMemory(MemoryStatus engine, int thresholdMb, bool autoRestart, string verdict, Func<long>? pss = null)
    {
        return new MemoryReading(
            engine.RssBytes,
            engine.PeakRssBytes,
            (pss ?? (() => ProcMemoryProbe.ReadPssBytes()))(),
            GC.GetTotalMemory(false),
            GC.GetTotalAllocatedBytes(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            engine.ScriptAssembliesLoaded,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            thresholdMb,
            autoRestart,
            verdict);
    }

    /// <summary>Skua's runtime helpers and recorded events on the engine's Skua services; null without a Skua host.</summary>
    public static RuntimeReading? ReadRuntime(IServiceProvider? skua)
    {
        if (skua is null)
            return null;
        IScriptQuest? quests = skua.GetService(typeof(IScriptQuest)) as IScriptQuest;
        IScriptDrop? drops = skua.GetService(typeof(IScriptDrop)) as IScriptDrop;
        IScriptBoost? boosts = skua.GetService(typeof(IScriptBoost)) as IScriptBoost;
        IDiagnosticsService? events = skua.GetService(typeof(IDiagnosticsService)) as IDiagnosticsService;
        List<string> boostNames = new();
        if (boosts is not null)
        {
            if (boosts.UseClassBoost)
                boostNames.Add($"Class ({boosts.ClassBoostID})");
            if (boosts.UseExperienceBoost)
                boostNames.Add($"Experience ({boosts.ExperienceBoostID})");
            if (boosts.UseGoldBoost)
                boostNames.Add($"Gold ({boosts.GoldBoostID})");
            if (boosts.UseReputationBoost)
                boostNames.Add($"Reputation ({boosts.ReputationBoostID})");
        }
        return new RuntimeReading(
            quests?.Registered.ToList() ?? [],
            drops?.Enabled ?? false,
            drops is null ? [] : drops.ToPickup.Concat(drops.ToPickupIDs.Select(id => $"#{id}")).ToList(),
            boosts?.Enabled ?? false,
            boostNames,
            events?.Enabled ?? false,
            events is null ? [] : events.GetRecentEvents().TakeLast(50).Reverse().ToList());
    }

    public static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}h {t.Minutes:00}m")
        : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}m {t.Seconds:00}s");

    public static string Mb(long bytes) => bytes < 0 ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.0} MB");

    public static string Ms(TimeSpan? span) => span is TimeSpan t ? string.Create(CultureInfo.InvariantCulture, $"{t.TotalMilliseconds:0} ms") : "not measured yet";

    /// <summary>The engine section.</summary>
    public static IReadOnlyList<InfoRow> EngineRows(EngineSnapshot s, bool packetCapture, string? interception = null)
    {
        List<InfoRow> rows =
        [
            new("Connection", s.Connection + (string.IsNullOrEmpty(s.StatusText) ? string.Empty : $" ({s.StatusText})")),
            new("Script", s.Script.ScriptName is null ? s.Script.State.ToString() : $"{s.Script.ScriptName}: {(s.Script.IsPaused ? "Paused" : s.Script.State.ToString())}"),
        ];
        if (s.Script.StartedAt is DateTimeOffset started && s.Script.IsBusy)
            rows.Add(new("Running for", Duration(DateTimeOffset.UtcNow - started)));
        if (s.Script.Message is string message && message.Length > 0)
            rows.Add(new("Script message", message));
        if (s.ScriptCounters is ScriptCounters c)
            rows.Add(new("Counters", c.Display));
        if (s.BackgroundWork is string work)
            rows.Add(new("Background work", work));
        rows.Add(new("Packet capture", packetCapture ? "on" : "off"));
        if (interception is not null)
            rows.Add(new("Packet interceptor", interception));
        return rows;
    }

    /// <summary>The connection section.</summary>
    public static IReadOnlyList<InfoRow> ConnectionRows(ConnectionDetails? c, PacketRate? rate, DateTimeOffset now)
    {
        if (c is null)
            return [new("Connection", "none (not logged in)")];
        List<InfoRow> rows =
        [
            new("Server", $"{c.ServerName ?? "?"} ({c.Endpoint ?? "?"})"),
            new("State", c.State),
            new("Client version", c.ClientVersion ?? "?"),
            new("Latency", c.RoundTrip is null ? "not measured yet (needs an answered request)" : $"{Ms(c.RoundTrip)} last, {Ms(c.AverageRoundTrip)} average"),
            new("Packets per minute", rate is null ? "measuring..." : string.Create(CultureInfo.InvariantCulture, $"{rate.InPerMinute:0} in, {rate.OutPerMinute:0} out (last {rate.Window.TotalSeconds:0} s)")),
            new("Packets", string.Create(CultureInfo.InvariantCulture, $"{c.PacketsIn:N0} in ({Mb(c.BytesIn)}), {c.PacketsOut:N0} out ({Mb(c.BytesOut)})")),
            new("Send queue", string.Create(CultureInfo.InvariantCulture, $"{c.Queued} waiting, {c.Dropped} dropped")),
            new("Pacing", string.Create(CultureInfo.InvariantCulture, $"{c.SendGap.TotalMilliseconds:0} ms between packets, {c.SlowSendGap.TotalMilliseconds:0} ms after a slow-down warning")),
            new("Slow-down warnings", c.SlowDownWarnings.ToString(CultureInfo.InvariantCulture)),
            new("Liveness probes", c.Probes.ToString(CultureInfo.InvariantCulture)),
        ];
        if (c.LastInbound is DateTimeOffset last)
            rows.Add(new("Last from server", string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (now - last).TotalSeconds):0.0} s ago")));
        return rows;
    }

    /// <summary>The memory section.</summary>
    public static IReadOnlyList<InfoRow> MemoryRows(MemoryReading m) =>
    [
        new("RSS", $"{Mb(m.RssBytes)} (peak {Mb(m.PeakRssBytes)})"),
        new("PSS", m.PssBytes < 0 ? "n/a" : Mb(m.PssBytes)),
        new("Managed heap", $"{Mb(m.ManagedBytes)} ({Mb(m.TotalAllocatedBytes)} allocated in all)"),
        new("Collections", $"gen 0: {m.Gen0}, gen 1: {m.Gen1}, gen 2: {m.Gen2}"),
        new("Script assemblies", $"{m.ScriptAssemblies} loaded (never unloaded on Android)"),
        new("Thread pool", $"{m.ThreadPoolThreads} threads, {m.PendingWorkItems} work items waiting"),
        new("Watchdog", $"{(m.AutoRestart ? "restarts" : "warns")} above {m.ThresholdMb} MB RSS; now {m.Verdict}"),
    ];

    /// <summary>The runtime helpers section.</summary>
    public static IReadOnlyList<InfoRow> RuntimeRows(RuntimeReading? r)
    {
        if (r is null)
            return [new("Runtime helpers", "not available (no Skua host)")];
        return
        [
            new("Registered quests", r.RegisteredQuests.Count == 0 ? "none" : string.Join(", ", r.RegisteredQuests)),
            new("Drop grabber", $"{(r.DropsEnabled ? "running" : "stopped")}; {(r.DropsToPickUp.Count == 0 ? "nothing to pick up" : string.Join(", ", r.DropsToPickUp))}"),
            new("Boosts", $"{(r.BoostsEnabled ? "running" : "stopped")}; {(r.Boosts.Count == 0 ? "none chosen" : string.Join(", ", r.Boosts))}"),
        ];
    }

    /// <summary>Skua's diagnostic event as the desktop's Diagnostics window shows it (time, category, name, value).</summary>
    public static InfoRow EventRow(DiagnosticEvent e) => new(
        $"{e.Timestamp.ToLocalTime():HH:mm:ss} {e.Category}",
        e.Value is double v ? string.Create(CultureInfo.InvariantCulture, $"{e.Name} {v:N2} {e.Unit}").TrimEnd() : e.Name);

    // ------------------------------------------------------------------ share bundle

    /// <summary>
    /// The share bundle: every section as plain text, the Flash gaps, the process exits, the recent events log and the
    /// engine's recent log, then scrubbed with <see cref="Scrub"/>.
    /// </summary>
    public static string Bundle(
        DiagnosticsReading r,
        string appVersion,
        string device,
        IEnumerable<LogEntry> engineLog,
        IEnumerable<string> eventsLog,
        bool packetCapture,
        string? interception,
        SecretScrubber? engineSecrets,
        IEnumerable<string?> personal)
    {
        StringBuilder sb = new();
        sb.AppendLine("VibeSkua Android diagnostics");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Taken {r.At:yyyy-MM-dd HH:mm:ss} UTC, app {appVersion}, {device}"));
        Section(sb, "Engine", EngineRows(r.Engine, packetCapture, interception));
        Section(sb, "Connection", ConnectionRows(r.Connection, r.Rate, r.At));
        Section(sb, "Memory", MemoryRows(r.Memory));
        Section(sb, "Runtime helpers", RuntimeRows(r.Runtime));
        sb.AppendLine().AppendLine("== Flash gaps ==");
        if (r.FlashGaps is null)
            sb.AppendLine("not available (no Skua host)");
        else if (r.FlashGaps.Count == 0)
            sb.AppendLine("none: every call had an answer");
        foreach (FlashGapRow g in r.FlashGaps ?? [])
            sb.Append(g.Kind.PadRight(12)).Append(g.Count.ToString(CultureInfo.InvariantCulture).PadLeft(7)).Append("  ").Append(g.Display).Append("  -- ").AppendLine(g.Detail);
        sb.AppendLine().AppendLine("== Process exits ==");
        sb.AppendLine(r.PendingCrashReport ? "A crash report waits for the next start (the crash screen shows it)." : "No pending crash report.");
        if (r.Exits.Count == 0)
            sb.AppendLine("No exit recorded by Android (Android 11+ only).");
        foreach (ExitRow e in r.Exits)
            sb.Append(e.When).Append(' ').AppendLine(e.Text);
        if (r.Runtime is { SkuaEvents.Count: > 0 } runtime)
        {
            sb.AppendLine().AppendLine("== Skua events ==");
            foreach (DiagnosticEvent e in runtime.SkuaEvents)
            {
                InfoRow row = EventRow(e);
                sb.Append(row.Label).Append("  ").AppendLine(row.Value);
            }
        }
        sb.AppendLine().AppendLine("== Events log (last lines) ==");
        foreach (string line in eventsLog)
            sb.AppendLine(line);
        sb.AppendLine().AppendLine("== Engine log (last lines) ==");
        foreach (LogEntry e in engineLog)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{e.At:HH:mm:ss} {e.Level} [{e.Source}] {e.Message}"));
        return Scrub(sb.ToString(), engineSecrets, personal);
    }

    private static void Section(StringBuilder sb, string title, IEnumerable<InfoRow> rows)
    {
        sb.AppendLine().Append("== ").Append(title).AppendLine(" ==");
        foreach (InfoRow row in rows)
            sb.Append(row.Label).Append(": ").AppendLine(row.Value);
    }

    /// <summary>
    /// Everything a bundle must not carry: the engine's registered secrets (password, login tokens) and the process
    /// registry's (webhook URL ...), credential-like key/value pairs, Discord webhook URLs, e-mail addresses, and the
    /// account and character names given in <paramref name="personal"/>.
    /// </summary>
    public static string Scrub(string text, SecretScrubber? engineSecrets, IEnumerable<string?> personal)
    {
        string result = engineSecrets?.Scrub(text) ?? text;
        result = DiagnosticText.Scrub(result);
        result = WebhookRegex().Replace(result, "<webhook>");
        result = EmailRegex().Replace(result, "<email>");
        foreach (string name in personal.Where(n => !string.IsNullOrWhiteSpace(n) && n!.Trim().Length >= 3).Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length))
            result = Regex.Replace(result, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}_])", "<account>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return result;
    }

    [GeneratedRegex(@"https?://[^\s""'<>]*/api/(?:v\d+/)?webhooks/[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebhookRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();
}
