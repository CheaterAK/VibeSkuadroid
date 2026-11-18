using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Rules;

/// <summary>
/// Aura bookkeeping the client does on top of what the server sends: skua.swf's <c>rebuildAuraArray</c> shape, and
/// the AQW 5.0 HUD aura table (stack counts and durations per subject) that skua.swf's <c>GetAuraSnapshots</c> reads.
/// </summary>
/// <remarks>
/// rebuildAuraArray follows skua.swf Auras.as (this repository). The HUD table is ported from Seraph
/// app/lib/skua/swfworld.js (hudAuraSync, hudAuraSeed, hudAuraSnap, hudAuraClear, auraSnapshots), MIT, which
/// follows the game's World.as.
/// </remarks>
public static class AuraRules
{
    /// <summary>
    /// skua.swf Auras.rebuildAuraArray: drops entries without a name or with <c>e == 1</c>, replaces cLeaf with
    /// "cycle_" and defaults a missing or non-numeric <c>val</c> to 1.
    /// </summary>
    /// <param name="auras">The leaf's auras.</param>
    /// <param name="nowMs">When set, an aura whose <c>ts + dur*1000 + 2000</c> is in the past is dropped too
    /// (headless addition: a lost <c>aura-</c> would otherwise keep it forever; the Flash client times auras out itself).</param>
    public static JsonArray RebuildAuraArray(JsonArray? auras, long? nowMs = null)
    {
        JsonArray rebuilt = new();
        if (auras is null)
            return rebuilt;
        foreach (JsonObject a in auras.OfType<JsonObject>())
        {
            string? name = JsonUtil.AsString(a["nam"]);
            if (string.IsNullOrEmpty(name) || JsonUtil.AsInt(a["e"]) == 1)
                continue;
            if (nowMs is long now && JsonUtil.AsDoubleOrNull(a["dur"]) is double dur && dur > 0
                && JsonUtil.AsLongOrNull(a["ts"]) is long ts && ts > 0 && ts + (long)(dur * 1000) + 2000 < now)
                continue;
            JsonObject r = new();
            bool hasVal = false;
            foreach (KeyValuePair<string, JsonNode?> pair in a)
            {
                if (pair.Key == "cLeaf")
                {
                    r[pair.Key] = "cycle_";
                }
                else if (pair.Key == "val")
                {
                    r[pair.Key] = JsonUtil.AsDoubleOrNull(pair.Value) is double v && !double.IsNaN(v) ? pair.Value!.DeepClone() : 1;
                    hasVal = true;
                }
                else
                {
                    r[pair.Key] = pair.Value?.DeepClone();
                }
            }
            if (!hasVal)
                r["val"] = 1;
            rebuilt.Add(r);
        }
        return rebuilt;
    }
}

/// <summary>One HUD aura of a subject.</summary>
public sealed class HudAura
{
    public long Seq { get; set; }

    /// <summary>Stack count (<c>n</c>).</summary>
    public int Stacks { get; set; } = 1;

    public string Icon { get; set; } = string.Empty;

    public bool Persist { get; set; }

    /// <summary>Full duration in seconds, 0 = no expiry.</summary>
    public double Duration { get; set; }

    /// <summary>Expiry, ms since the epoch, 0 = none.</summary>
    public long End { get; set; }
}

/// <summary>
/// The HUD aura table of every subject (<c>p:&lt;uid&gt;</c>, <c>m:&lt;MonMapID&gt;</c>): name to stacks and timing.
/// Thread safe.
/// </summary>
public sealed class HudAuraTable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, HudAura>> _subjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _descriptions = new(StringComparer.Ordinal);
    private long _seq;

    /// <summary>Forgets everything (new connection or new map for monsters).</summary>
    public void Clear()
    {
        lock (_gate)
            _subjects.Clear();
    }

    /// <summary>Forgets the subjects whose key starts with <paramref name="prefix"/> (for example "m:").</summary>
    public void ClearSubjects(string prefix)
    {
        lock (_gate)
        {
            foreach (string key in _subjects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _subjects.Remove(key);
        }
    }

    /// <summary>World.auraDescs: aura name to tooltip text.</summary>
    public void SetDescription(string name, string description)
    {
        lock (_gate)
            _descriptions[name] = description;
    }

    /// <summary>
    /// World.hudAuraSync: an aura event (<c>aura+</c>, <c>aura++</c>, <c>aura=</c>, <c>aura-</c>, <c>aura--</c>) on
    /// <paramref name="subject"/>. <paramref name="subjectDead"/>: a dead monster's table is left alone.
    /// </summary>
    public void Apply(string subject, string kind, JsonObject ev, long nowMs, bool subjectDead = false)
    {
        bool add = kind is "aura+" or "aura++";
        bool eq = kind == "aura=";
        if (!add && !eq && kind is not "aura-" and not "aura--")
            return;
        List<JsonObject> list = new();
        if (ev["auras"] is JsonArray many)
            list.AddRange(many.OfType<JsonObject>());
        else if (ev["aura"] is JsonObject one)
            list.Add(one);
        if (list.Count == 0 || (subject.StartsWith("m:", StringComparison.Ordinal) && subjectDead))
            return;
        lock (_gate)
        {
            if (!_subjects.TryGetValue(subject, out Dictionary<string, HudAura>? hud))
                _subjects[subject] = hud = new Dictionary<string, HudAura>(StringComparer.Ordinal);
            foreach (JsonObject a in list)
            {
                string? name = JsonUtil.AsString(a["nam"]);
                if (name is null)
                    continue;
                hud.TryGetValue(name, out HudAura? h);
                int? stk = JsonUtil.AsIntOrNull(a["stk"]);
                if (eq)
                {
                    if (h is not null && stk is int s)
                        h.Stacks = Math.Max(1, s);
                }
                else if (!add)
                {
                    if (h is null)
                        continue;
                    if (stk is int left && left > 0)
                    {
                        h.Stacks = left;
                        if (JsonUtil.AsDoubleOrNull(a["dur"]) is double d && h.End > 0)
                        {
                            h.End = nowMs + (long)(d * 1000);
                            if (h.Duration < d)
                                h.Duration = d;
                        }
                    }
                    else
                    {
                        hud.Remove(name);
                    }
                }
                else
                {
                    if (JsonUtil.AsString(a["desc"]) is string desc)
                        _descriptions[name] = desc;
                    if (h is null)
                        hud[name] = h = new HudAura { Seq = ++_seq };
                    h.Stacks = stk ?? 1;
                    if (JsonUtil.AsString(a["icon"]) is string icon)
                        h.Icon = icon;
                    h.Persist = JsonUtil.AsBool(a["persist"]);
                    double dur = JsonUtil.AsDoubleOrNull(a["dur"]).GetValueOrDefault();
                    if (JsonUtil.AsString(a["t"]) == "s" && dur > 0)
                    {
                        h.Duration = !JsonUtil.AsBool(a["isNew"]) && h.Duration > dur ? h.Duration : dur;
                        h.End = nowMs + (long)(dur * 1000);
                    }
                    else
                    {
                        h.Duration = 0;
                        h.End = 0;
                    }
                }
            }
        }
    }

    /// <summary>World.hudAuraSeed: a full list <c>[[name, icon, dur, stacks, persist, fullDur], ...]</c> replaces the subject's table.</summary>
    public void Seed(string subject, JsonArray? au, long nowMs)
    {
        lock (_gate)
        {
            Dictionary<string, HudAura> hud = new(StringComparer.Ordinal);
            _subjects[subject] = hud;
            if (au is null)
                return;
            foreach (JsonArray x in au.OfType<JsonArray>())
            {
                if (x.Count < 5 || JsonUtil.AsString(x[0]) is not string name)
                    continue;
                double dur = JsonUtil.AsDoubleOrNull(x[2]).GetValueOrDefault();
                double full = x.Count > 5 ? JsonUtil.AsDoubleOrNull(x[5]).GetValueOrDefault() : 0;
                hud[name] = new HudAura
                {
                    Seq = ++_seq,
                    Stacks = Math.Max(1, JsonUtil.AsInt(x[3])),
                    Icon = JsonUtil.AsString(x[1]) ?? string.Empty,
                    Persist = JsonUtil.AsInt(x[4]) == 1,
                    Duration = dur > 0 ? (full > dur ? full : dur) : 0,
                    End = dur > 0 ? nowMs + (long)(dur * 1000) : 0,
                };
            }
        }
    }

    /// <summary>World.hudAuraClear: leaving combat clears all, <c>clearAuras</c> keeps persistent ones.</summary>
    public void ClearSubject(string subject, bool keepPersistent)
    {
        lock (_gate)
        {
            if (!_subjects.TryGetValue(subject, out Dictionary<string, HudAura>? hud))
                return;
            foreach (string key in hud.Where(p => !keepPersistent || !p.Value.Persist).Select(p => p.Key).ToList())
                hud.Remove(key);
        }
    }

    /// <summary>skua.swf Auras.GetAuraSnapshots for one subject, oldest first.</summary>
    public JsonArray Snapshots(string? subject, long nowMs)
    {
        JsonArray result = new();
        if (subject is null)
            return result;
        lock (_gate)
        {
            if (!_subjects.TryGetValue(subject, out Dictionary<string, HudAura>? hud))
                return result;
            foreach (KeyValuePair<string, HudAura> p in hud.OrderBy(x => x.Value.Seq))
            {
                HudAura a = p.Value;
                result.Add(new JsonObject
                {
                    ["nam"] = p.Key,
                    ["n"] = a.Stacks,
                    ["dur"] = a.Duration,
                    ["remaining"] = a.End > 0 ? Math.Max(0, (a.End - nowMs) / 1000.0) : 0,
                    ["persist"] = a.Persist,
                    ["icon"] = a.Icon,
                    ["desc"] = _descriptions.GetValueOrDefault(p.Key) ?? string.Empty,
                    ["seq"] = a.Seq,
                });
            }
        }
        return result;
    }
}
