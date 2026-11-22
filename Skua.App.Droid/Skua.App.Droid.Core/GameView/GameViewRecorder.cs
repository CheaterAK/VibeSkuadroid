using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Skua.App.Droid.Core.GameView;

/// <summary>Process and WebView-host memory of the app, sampled by the Android side every 10 s.</summary>
public readonly record struct NativeMemorySample(double PssMB, double RssMB, double JavaHeapMB, double NativeHeapMB, double SystemAvailMB, bool SystemLowMemory);

/// <summary>
/// Collects what the game view measures (page records, app events, memory, crashes, console errors, bridge state)
/// on the view's clock (seconds since it opened) and formats the shareable report (ported from the game view spike).
/// Never stores packet contents or typed text; everything kept is scrubbed of registered secrets (the password while
/// auto-login holds it) and credential-like key=value pairs.
/// </summary>
public sealed partial class GameViewRecorder
{
    public const string PagePrefix = "[vgame] ";

    /// <summary>The milestones the report lists, in order.</summary>
    public static readonly string[] Milestones =
    [
        "page_start", "wasm_instantiated", "ruffle_instance", "first_frame", "skua_swf_ready", "game_swf_loaded", "login_screen",
        "server_socket_connected", "socket_connected", "in_game", "ruffle_panic",
    ];
    private const int MaxEvents = 300, MaxConsole = 40;

    // Everything the page can add to is bounded (the page's records come from game.aq.com content as much as from the
    // app's own script). Samples keep the most recent MaxSamples (the oldest quarter is dropped when full: fps every
    // 2 s is about 2 h); headers, milestones and blocked-request keys stop growing at their caps.
    internal const int MaxSamples = 4000, MaxHeaders = 40, MaxHeaderValue = 1000, MaxMarks = 64, MaxBlockedKeys = 200;

    private readonly Func<double> _clock;
    private readonly List<string> _secrets = [];
    private readonly object _lock = new();
    private readonly List<(string Key, string Value)> _header = [];
    private readonly Dictionary<string, double> _marks = [];
    private readonly List<(double T, string Kind, string Detail)> _events = [];
    private readonly List<(double T, double Fps, double Raf, double LongTaskPct, double PacketsPerSec)> _fps = [];
    private readonly List<(double T, double? WasmMB, double? JsHeapMB)> _pageMem = [];
    private readonly List<(double T, NativeMemorySample M)> _nativeMem = [];
    private readonly Dictionary<string, (int Count, double FirstT)> _console = [];
    private readonly Dictionary<string, int> _blocked = [];
    private readonly List<string> _crashes = [];
    private int _droppedEvents, _consoleTotal;

    public GameViewRecorder(Func<double> clockSeconds, string? secretToRedact = null)
    {
        _clock = clockSeconds;
        if (!string.IsNullOrEmpty(secretToRedact))
            _secrets.Add(secretToRedact);
    }

    private string? _lastStage;

    /// <summary>Raised (on the caller's thread) when the page's stage changes: login_screen, in_game, left_game.</summary>
    public event Action<string>? StageChanged;

    /// <summary>Raised (on the caller's thread) the first time a milestone is reached.</summary>
    public event Action<string>? MarkAdded;

    /// <summary>A value that must never appear in anything kept (redacted from now on).</summary>
    public void AddSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return;
        lock (_lock)
            if (!_secrets.Contains(secret))
                _secrets.Add(secret);
    }

    public double Now => _clock();

    public void Header(string key, string value)
    {
        string kept = Truncate(Scrub(value), MaxHeaderValue);
        lock (_lock)
        {
            int i = _header.FindIndex(h => h.Key == key);
            if (i >= 0 && key == "page")
                _header[i] = (key, kept); // the page's config record: the latest only
            else if (_header.Count < MaxHeaders)
                _header.Add((Truncate(key, 40), kept));
        }
    }

    /// <summary>Adds a sample, dropping the oldest quarter when the list is full.</summary>
    private static void AddSample<T>(List<T> list, T item)
    {
        if (list.Count >= MaxSamples)
            list.RemoveRange(0, MaxSamples / 4);
        list.Add(item);
    }

    internal (int Headers, int Fps, int PageMem, int NativeMem, int Marks, int BlockedKeys) Counts()
    {
        lock (_lock)
            return (_header.Count, _fps.Count, _pageMem.Count, _nativeMem.Count, _marks.Count, _blocked.Count);
    }

    public void Event(string kind, string detail = "")
    {
        lock (_lock)
        {
            if (_events.Count >= MaxEvents) { _droppedEvents++; return; }
            _events.Add((Now, kind, Scrub(detail)));
        }
    }

    public void Mark(string name, string detail = "")
    {
        lock (_lock)
        {
            if (_marks.ContainsKey(name) || (_marks.Count >= MaxMarks && Array.IndexOf(Milestones, name) < 0)) return;
            _marks[name] = Now;
        }
        Event("mark " + name, detail);
        try { MarkAdded?.Invoke(name); } catch { /* observers must not break recording */ }
    }

    public bool HasMark(string name) { lock (_lock) return _marks.ContainsKey(name); }

    public double? MarkTime(string name) { lock (_lock) return _marks.TryGetValue(name, out double t) ? t : null; }

    public void RendererGone(bool didCrash, int priorityAtExit, string priorityName)
    {
        string s = $"t={Now:F1}s didCrash={didCrash} rendererPriorityAtExit={priorityAtExit} ({priorityName})";
        lock (_lock) _crashes.Add(s);
        Event("renderer_gone", s);
    }

    public void Blocked(string key)
    {
        lock (_lock)
        {
            if (_blocked.TryGetValue(key, out int n))
                _blocked[key] = n + 1;
            else if (_blocked.Count < MaxBlockedKeys)
                _blocked[key] = 1;
            else
                _blocked["(other)"] = _blocked.TryGetValue("(other)", out int o) ? o + 1 : 1;
        }
    }

    public void NativeMemory(NativeMemorySample m) { lock (_lock) AddSample(_nativeMem, (Now, m)); }

    /// <summary>Handles one WebView console message; returns true if it was a page record.</summary>
    public bool Console(string message, string level)
    {
        if (message.StartsWith(PagePrefix, StringComparison.Ordinal))
        {
            PageRecord(message[PagePrefix.Length..]);
            return true;
        }
        if (level is "ERROR" or "WARNING")
        {
            string key = level[0] + " " + Truncate(Scrub(message), 240);
            lock (_lock)
            {
                _consoleTotal++;
                if (_console.TryGetValue(key, out var c)) _console[key] = (c.Count + 1, c.FirstT);
                else if (_console.Count < MaxConsole) _console[key] = (1, Now);
            }
        }
        return false;
    }

    private void PageRecord(string json)
    {
        JsonElement r;
        try { r = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { Event("bad_page_record", Truncate(json, 80)); return; }
        string type = Str(r, "type") ?? "?";
        switch (type)
        {
            case "mark":
                string name = Str(r, "name") ?? "?";
                string extra = string.Join(" ", r.EnumerateObject().Where(p => p.Name is not ("t" or "type" or "name")).Select(p => $"{p.Name}={Val(p.Value)}"));
                Mark(name, extra);
                break;
            case "fps":
                lock (_lock) AddSample(_fps, (Now, Num(r, "fps") ?? 0, Num(r, "raf") ?? 0, Num(r, "longTaskPct") ?? 0, Num(r, "packetsPerSec") ?? 0));
                break;
            case "mem":
                lock (_lock) AddSample(_pageMem, (Now, Num(r, "wasmMB"), Num(r, "jsHeapMB")));
                break;
            case "stage":
                string stage = Str(r, "name") ?? "?";
                if (stage != _lastStage)
                {
                    _lastStage = stage;
                    Event("stage", stage);
                    try { StageChanged?.Invoke(stage); } catch { /* observers must not break recording */ }
                }
                break;
            case "config":
                Header("page", string.Join(" ", r.EnumerateObject().Where(p => p.Name is not ("t" or "type")).Select(p => $"{p.Name}={Val(p.Value)}")));
                break;
            default:
                Event(type, string.Join(" ", r.EnumerateObject().Where(p => p.Name is not ("t" or "type")).Select(p => $"{p.Name}={Val(p.Value)}")));
                break;
        }
    }

    private static string Val(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();

    private static string? Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement r, string name) =>
        r.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string Truncate(string s, int n) => s.Length > n ? s[..n] + "..." : s;

    [GeneratedRegex(@"(?i)\b(pass(word)?|pwd|token|session|ticket|auth\w*|key|user(name)?)\b(\s*[=:]\s*|""\s*:\s*"")[^\s&,;""]+")]
    private static partial Regex CredentialPairs();

    /// <summary>Removes registered secrets and credential-like key=value pairs.</summary>
    public string Scrub(string s)
    {
        lock (_lock)
            foreach (string secret in _secrets)
                s = s.Replace(secret, "(secret)", StringComparison.Ordinal);
        return CredentialPairs().Replace(s, m => m.Groups[1].Value + "=(redacted)");
    }

    /// <summary>One line for the on-screen HUD.</summary>
    public string Hud()
    {
        lock (_lock)
        {
            string fps = _fps.Count > 0 ? _fps[^1].Fps.ToString("F1", CultureInfo.InvariantCulture) : "-";
            string lt = _fps.Count > 0 ? _fps[^1].LongTaskPct.ToString("F0", CultureInfo.InvariantCulture) + "%" : "-";
            string pss = _nativeMem.Count > 0 ? _nativeMem[^1].M.PssMB.ToString("F0", CultureInfo.InvariantCulture) : "-";
            string wasm = _pageMem.Count > 0 && _pageMem[^1].WasmMB is double w ? w.ToString("F0", CultureInfo.InvariantCulture) : "-";
            string stage = _marks.ContainsKey("in_game") ? "in game"
                : _marks.ContainsKey("server_socket_connected") ? "connected"
                : _marks.ContainsKey("login_screen") ? "login"
                : _marks.ContainsKey("first_frame") ? "loading" : "starting";
            return $"{stage} | {fps} fps | main {lt} | app PSS {pss} MB | wasm {wasm} MB | crashes {_crashes.Count} | t {Now:F0}s";
        }
    }

    public string BuildReport()
    {
        lock (_lock)
        {
            StringBuilder sb = new();
            CultureInfo ci = CultureInfo.InvariantCulture;
            sb.AppendLine("VibeSkua game view (Ruffle) report");
            foreach ((string k, string v) in _header) sb.AppendLine($"{k}: {v}");
            sb.AppendLine($"report at t={Now:F1}s since the view opened");
            sb.AppendLine();

            sb.AppendLine("== Milestones (s since the view opened) ==");
            foreach (string m in Milestones)
                sb.AppendLine($"  {m,-23} {(_marks.TryGetValue(m, out double t) ? t.ToString("F1", ci) : "-")}");
            sb.AppendLine();

            // The official loader has no in-game signal: the first game server connection stands in for it.
            (string label, double? from) = _marks.TryGetValue("in_game", out double ig) ? ("in game", ig)
                : _marks.TryGetValue("server_socket_connected", out double sc) ? ("after server connect", sc)
                : ("", (double?)null);
            sb.AppendLine("== Frame rate (rendered frames/s, sampled every 2 s) ==");
            AppendFps(sb, "whole run", _fps);
            if (from is double g) AppendFps(sb, label, _fps.Where(f => f.T >= g + 5).ToList());
            sb.AppendLine();

            sb.AppendLine("== Memory every 10 s (app process: PSS, RSS, Java heap, native heap; page: Ruffle wasm, JS heap; system avail) ==");
            int pi = 0;
            foreach ((double t, NativeMemorySample m) in _nativeMem)
            {
                while (pi + 1 < _pageMem.Count && _pageMem[pi + 1].T <= t + 1) pi++;
                string page = _pageMem.Count > 0 && _pageMem[pi].T <= t + 1
                    ? $"wasm {Fmt(_pageMem[pi].WasmMB)} js {Fmt(_pageMem[pi].JsHeapMB)}"
                    : "wasm - js -";
                sb.AppendLine(string.Create(ci, $"  t={t,6:F0}s PSS {m.PssMB,6:F0} RSS {m.RssMB,6:F0} java {m.JavaHeapMB,5:F0} native {m.NativeHeapMB,5:F0} | {page} | avail {m.SystemAvailMB:F0}{(m.SystemLowMemory ? " LOW" : "")}"));
            }
            if (_nativeMem.Count > 1)
            {
                double first = _nativeMem[0].M.PssMB, last = _nativeMem[^1].M.PssMB, peak = _nativeMem.Max(x => x.M.PssMB);
                sb.AppendLine(string.Create(ci, $"  app PSS first {first:F0} last {last:F0} peak {peak:F0} MB"));
            }
            List<double> wasm = _pageMem.Where(p => p.WasmMB.HasValue).Select(p => p.WasmMB!.Value).ToList();
            if (wasm.Count > 0) sb.AppendLine(string.Create(ci, $"  Ruffle wasm first {wasm[0]:F0} last {wasm[^1]:F0} peak {wasm.Max():F0} MB"));
            sb.AppendLine("  (the WebView renderer process is not visible to the app; wasm + JS heap are its main parts)");
            sb.AppendLine();

            sb.AppendLine($"== WebView renderer process gone: {_crashes.Count} ==");
            foreach (string c in _crashes) sb.AppendLine("  " + c);
            sb.AppendLine();

            sb.AppendLine($"== Console errors/warnings: {_consoleTotal} (first {MaxConsole} distinct) ==");
            foreach (var (k, v) in _console.OrderBy(x => x.Value.FirstT))
                sb.AppendLine(string.Create(ci, $"  t={v.FirstT,6:F1}s x{v.Count,-4} {k}"));
            sb.AppendLine();

            sb.AppendLine($"== Blocked requests: {_blocked.Values.Sum()} ==");
            foreach (var (k, v) in _blocked.OrderByDescending(x => x.Value)) sb.AppendLine($"  x{v} {k}");
            sb.AppendLine();

            sb.AppendLine($"== Events ({_events.Count}{(_droppedEvents > 0 ? $", {_droppedEvents} dropped" : "")}) ==");
            foreach ((double t, string kind, string detail) in _events)
                sb.AppendLine(string.Create(ci, $"  t={t,7:F1}s {kind} {detail}"));
            return sb.ToString();
        }

        static string Fmt(double? v) => v.HasValue ? v.Value.ToString("F0", CultureInfo.InvariantCulture) : "-";
    }

    /// <summary>One line for the engine's Console when the view closes (numbers only, no page text).</summary>
    public string Summary()
    {
        lock (_lock)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            List<string> parts = [];
            parts.Add(_marks.TryGetValue("in_game", out double ig) ? string.Create(ci, $"in game after {ig:F0} s")
                : _marks.ContainsKey("login_screen") ? "login screen reached, never in game" : "the game did not load");
            List<double> fps = (_marks.TryGetValue("in_game", out double from) ? _fps.Where(f => f.T >= from + 5) : _fps).Select(f => f.Fps).ToList();
            if (fps.Count > 0)
                parts.Add(string.Create(ci, $"{fps.Average():F1} fps"));
            if (_fps.Count > 0)
                parts.Add(string.Create(ci, $"main thread {_fps.Average(f => f.LongTaskPct):F0}% busy"));
            if (_nativeMem.Count > 0)
                parts.Add(string.Create(ci, $"PSS peak {_nativeMem.Max(m => m.M.PssMB):F0} MB"));
            if (_crashes.Count > 0)
                parts.Add($"renderer gone {_crashes.Count}x");
            int blocked = _blocked.Values.Sum();
            if (blocked > 0)
                parts.Add($"{blocked} requests blocked");
            parts.Add(string.Create(ci, $"open {Now:F0} s"));
            return string.Join(", ", parts);
        }
    }

    private static void AppendFps(StringBuilder sb, string label, List<(double T, double Fps, double Raf, double LongTaskPct, double PacketsPerSec)> s)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        if (s.Count == 0) { sb.AppendLine($"  {label}: no samples"); return; }
        List<double> f = s.Select(x => x.Fps).Order().ToList();
        double p10 = f[(int)Math.Floor((f.Count - 1) * 0.1)];
        sb.AppendLine(string.Create(ci,
            $"  {label}: n={s.Count} avg {f.Average():F2} min {f[0]:F2} p10 {p10:F2} max {f[^1]:F2} | main-thread busy (long tasks) avg {s.Average(x => x.LongTaskPct):F0}% max {s.Max(x => x.LongTaskPct):F0}% | rAF avg {s.Average(x => x.Raf):F0}/s | packets avg {s.Average(x => x.PacketsPerSec):F1}/s"));
    }
}
