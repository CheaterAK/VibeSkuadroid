using Skua.Core.Headless.Protocol;
using Skua.Core.Headless.Rules;
using Skua.Core.Headless.World;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Client;

// Targeting, the skill bar, skill use and the auto attack (World.as setTarget / testAction / getActionResult /
// showActionResult / autoActionHandler, as ported in Seraph swfworld.js).
public sealed partial class GameClient
{
    private const int PotionSlot = 5;

    // ---------------------------------------------------------------- the skill bar

    // Game "sAct": a new class replaces the bar (the potion slot i1 survives); the same class only unlocks skills.
    private void OnSkillBar(JsonObject d)
    {
        JsonObject actions = d["actions"] as JsonObject ?? new JsonObject();
        List<JsonObject?> incoming = (actions["active"] as JsonArray)?.Select(x => x as JsonObject).ToList() ?? new();
        lock (_gate)
        {
            if (_newClass || _active.Count == 0)
            {
                JsonObject? potion = _active.FirstOrDefault(x => x is not null && JsonUtil.AsString(x["ref"]) == "i1");
                _active.Clear();
                if (potion is not null)
                {
                    for (int i = 0; i < PotionSlot; i++)
                        _active.Add(null);
                    _active.Add(potion);
                }
                for (int i = 0; i < incoming.Count; i++)
                {
                    if (incoming[i] is not JsonObject a)
                        continue;
                    bool isPotion = JsonUtil.AsString(a["ref"]) == "i1";
                    int slot = isPotion ? PotionSlot : i;
                    if (isPotion && potion is not null)
                        continue;
                    JsonObject skill = (JsonObject)a.DeepClone();
                    skill["sArg1"] = string.Empty;
                    skill["sArg2"] = string.Empty;
                    skill["ts"] = 0;
                    skill["actID"] = -1;
                    skill["lock"] = false;
                    if (!JsonUtil.AsBool(skill["auto"]))
                        skill["auto"] = false;
                    PlaceSkill(slot, skill);
                }
            }
            else
            {
                for (int i = 0; i < incoming.Count; i++)
                {
                    if (incoming[i] is not JsonObject a)
                        continue;
                    int slot = JsonUtil.AsString(a["ref"]) == "i1" ? PotionSlot : i;
                    JsonObject? have = slot < _active.Count ? _active[slot] : null;
                    if (have is not null)
                    {
                        if (JsonUtil.AsBool(a["isOK"]) && !JsonUtil.AsBool(have["isOK"]))
                            have["isOK"] = true;
                    }
                    else
                    {
                        JsonObject skill = (JsonObject)a.DeepClone();
                        skill["ts"] = 0;
                        skill["actID"] = -1;
                        skill["lock"] = false;
                        PlaceSkill(slot, skill);
                    }
                }
            }
            _passive.Clear();
            if (actions["passive"] is JsonArray passive)
            {
                foreach (JsonObject p in passive.OfType<JsonObject>())
                {
                    JsonObject copy = (JsonObject)p.DeepClone();
                    copy["sArg1"] = string.Empty;
                    copy["sArg2"] = string.Empty;
                    _passive.Add(copy);
                }
            }
            _newClass = false;
        }
    }

    private void PlaceSkill(int slot, JsonObject skill)
    {
        while (_active.Count <= slot)
            _active.Add(null);
        _active[slot] = skill;
    }

    // Game "seia": the equipped consumable's action data goes into the potion slot.
    private void OnSeia(JsonObject d)
    {
        if (JsonUtil.AsInt(d["iRes"]) != 1)
            return;
        lock (_gate)
        {
            foreach (JsonObject a in _active.OfType<JsonObject>().Where(a => JsonUtil.AsString(a["ref"]) == "i1"))
            {
                a.Remove("tgtMax");
                a.Remove("tgtMin");
                a.Remove("auras");
                if (!ActionTimeCheck(a, ignoreGcd: true))
                {
                    if (a["OldCD"] is null)
                        a["OldCD"] = a["cd"]?.DeepClone();
                }
                else
                {
                    a["ts"] = 0;
                }
                if (d["o"] is JsonObject o)
                {
                    foreach (KeyValuePair<string, JsonNode?> pair in o)
                    {
                        if (pair.Key is not "nam" and not "ref" and not "desc" and not "typ")
                            a[pair.Key] = pair.Value?.DeepClone();
                    }
                }
            }
        }
    }

    /// <summary><c>world.actions</c>: <c>{active: [...], passive: [...]}</c> with the client's timing fields.</summary>
    public JsonObject ActionsJson()
    {
        lock (_gate)
        {
            return new JsonObject
            {
                ["active"] = new JsonArray(_active.Select(a => a?.DeepClone()).ToArray()),
                ["passive"] = new JsonArray(_passive.Select(a => (JsonNode?)a.DeepClone()).ToArray()),
            };
        }
    }

    /// <summary>Number of entries in <c>world.actions.active</c>.</summary>
    public int ActiveSkillCount
    {
        get { lock (_gate) return _active.Count; }
    }

    /// <summary>Writes a field of an active skill (Skua sets <c>ts</c> through a linked object). False when there is no such skill.</summary>
    public bool SetSkillField(int index, string field, JsonNode? value)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _active.Count || _active[index] is not JsonObject skill)
                return false;
            skill[field] = value?.DeepClone();
            return true;
        }
    }

    // ---------------------------------------------------------------- timing

    private double Haste()
    {
        JsonObject? sta = World.GetStats();
        return JsonUtil.AsDoubleOrNull(sta?["$tha"]).GetValueOrDefault();
    }

    private int GlobalCooldown(double haste) => SkillRules.GlobalCooldown(haste);

    // World.actionTimeCheck (the game's: the auto attack is ready whenever it is not already running).
    private bool ActionTimeCheck(JsonObject a, bool ignoreGcd = false)
    {
        long now = Now;
        if (JsonUtil.AsBool(a["auto"]))
            return _autoTimer is null;
        double haste = Haste();
        if (!ignoreGcd && now - _gcdStart < GlobalCooldown(haste))
            return false;
        int? old = JsonUtil.AsIntOrNull(a["OldCD"]);
        if (now - JsonUtil.AsLong(a["ts"]) >= SkillRules.EffectiveCooldown(JsonUtil.AsInt(a["cd"]), old, haste))
        {
            a.Remove("OldCD");
            return true;
        }
        return false;
    }

    // skua.swf Skills.actionTimeCheck: the global cooldown always applies, auto attack included.
    private bool SkuaTimeCheck(JsonObject a)
    {
        long now = Now;
        double haste = Haste();
        int? old = JsonUtil.AsIntOrNull(a["OldCD"]);
        if (!SkillRules.IsOffCooldown(now, JsonUtil.AsLong(a["ts"]), JsonUtil.AsInt(a["cd"]), old, haste, _gcdStart, GlobalCooldown(haste)))
            return false;
        a.Remove("OldCD");
        return true;
    }

    /// <summary>skua.swf Skills.canUseSkill.</summary>
    public bool CanUseSkill(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _active.Count || _active[index] is not JsonObject skill)
                return false;
            JsonObject? leaf = TargetLeaf(_target);
            if (leaf is null || JsonUtil.AsInt(leaf["intHP"]) <= 0)
                return false;
            return SkuaTimeCheck(skill) && JsonUtil.AsBool(skill["isOK"]) && !JsonUtil.AsBool(skill["skillLock"]) && !JsonUtil.AsBool(skill["lock"]);
        }
    }

    /// <summary>skua.swf Skills.useSkill: when off cooldown, World.testAction. True when it was tried.</summary>
    public bool UseSkill(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _active.Count || _active[index] is not JsonObject skill || !SkuaTimeCheck(skill))
                return false;
            TestAction(skill, 0);
            return true;
        }
    }

    /// <summary>World.testAction on the skill with this <c>ref</c> (World.testAction(skill) called with a skill object).</summary>
    public bool TestSkill(string skillRef)
    {
        lock (_gate)
        {
            JsonObject? skill = _active.OfType<JsonObject>().FirstOrDefault(a => JsonUtil.AsString(a["ref"]) == skillRef);
            if (skill is null)
                return false;
            TestAction(skill, 0);
            return true;
        }
    }

    /// <summary>World.testAction on the auto attack (RemoteControl's startAutoAttack, and approachTarget).</summary>
    public void StartAutoAttack()
    {
        lock (_gate)
        {
            if (AutoAttackSkill() is JsonObject aa)
                TestAction(aa, 0);
        }
    }

    // ---------------------------------------------------------------- targets

    /// <summary>The live leaf of a target (monTree / uoTree), or null when it is gone.</summary>
    private JsonObject? TargetLeaf(TargetRef? t)
    {
        if (t is null)
            return null;
        if (t.Kind == 'm')
            return World.GetMonster(t.Id)?.Live;
        if (t.Id == World.MyUserId)
            return World.GetPlayer()?.DataLeaf;
        return World.GetAreaUsers().FirstOrDefault(u => u.UserId == t.Id || (t.Name is not null && u.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))?.Leaf;
    }

    private TargetRef Me() => new('p', World.MyUserId, World.MyUsername);

    private bool IsMe(TargetRef? t) => t is { Kind: 'p' } && t.Id == World.MyUserId;

    // World.setTarget
    private void SetTargetCore(TargetRef? t)
    {
        if (t is not null && IsMe(t) && LiteFlag("bUntargetSelf"))
            return;
        if (Equals(_target, t))
            return;
        if (t is not null)
        {
            if (t.Kind == 'p' && !IsPvp())
                CancelAutoAttackCore();
            _target = t;
        }
        else
        {
            JsonObject? leaf = World.GetPlayer()?.DataLeaf;
            if (leaf is not null && JsonUtil.AsInt(leaf["intState"], 1) > 0)
                ExitCombatCore();
            _target = null;
        }
    }

    private static bool IsPvp() => false;

    public void SetTarget(TargetRef? target)
    {
        lock (_gate)
            SetTargetCore(target);
    }

    /// <summary>World.cancelTarget: stops the auto attack first; a second call drops the target.</summary>
    public void CancelTarget()
    {
        lock (_gate)
        {
            if (_autoTimer is not null)
            {
                CancelAutoAttackCore();
                return;
            }
            if (_target is not null)
                SetTargetCore(null);
        }
    }

    /// <summary>skua.swf Player.untargetSelf.</summary>
    public void UntargetSelf()
    {
        lock (_gate)
        {
            if (IsMe(_target))
                CancelTarget();
        }
    }

    /// <summary>
    /// World.approachTarget: with a living monster targeted the auto attack starts. The walk into range is left out:
    /// the server only checks both are in the same cell.
    /// </summary>
    public void ApproachTarget()
    {
        lock (_gate)
        {
            TargetRef? t = _target;
            JsonObject? myLeaf = World.GetPlayer()?.DataLeaf;
            if (t is null || myLeaf is null || JsonUtil.AsInt(myLeaf["intState"], 1) == 0)
                return;
            if (t.Kind != 'm' && !IsPvp())
                return;
            JsonObject? leaf = TargetLeaf(t);
            if (leaf is null || JsonUtil.AsInt(leaf["intState"], 1) == 0)
                return;
            if (_autoTimer is null && AutoAttackSkill() is JsonObject aa)
                TestAction(aa, 0);
        }
    }

    public void CancelAutoAttack()
    {
        lock (_gate)
            CancelAutoAttackCore();
    }

    private void CancelAutoAttackCore()
    {
        _autoTimer?.Dispose();
        _autoTimer = null;
    }

    /// <summary>World.exitCombat.</summary>
    public void ExitCombat()
    {
        lock (_gate)
            ExitCombatCore();
    }

    private void ExitCombatCore()
    {
        foreach (JsonObject a in _active.OfType<JsonObject>())
            a["lock"] = false;
        CancelAutoAttackCore();
    }

    private JsonObject? AutoAttackSkill()
    {
        return _active.OfType<JsonObject>().FirstOrDefault(a => JsonUtil.AsBool(a["auto"])) ?? (_active.Count > 0 ? _active[0] : null);
    }

    /// <summary>Monsters of the current map, as skua.swf's getMonData (objData plus live HP and state).</summary>
    public JsonArray MonstersJson(bool currentCellOnly)
    {
        string cell = World.GetArea().Cell;
        JsonArray list = new();
        foreach (MonsterInfo m in World.GetMonsters())
        {
            if (currentCellOnly && (m.Live is null || JsonUtil.AsString(m.Live["strFrame"]) != cell))
                continue;
            list.Add(MonData(m));
        }
        return list;
    }

    private static JsonObject MonsterObjData(MonsterInfo m)
    {
        JsonObject o = (JsonObject)m.Definition.DeepClone();
        JsonUtil.Merge(o, m.Placement);
        if (m.Live?["MonID"] is JsonNode liveId)
            o["MonID"] = liveId.DeepClone();
        return o;
    }

    // Monster.getMonData
    private static JsonObject MonData(MonsterInfo m)
    {
        JsonObject o = MonsterObjData(m);
        if (m.Live is JsonObject leaf)
        {
            o["intHP"] = leaf["intHP"]?.DeepClone();
            o["intHPMax"] = leaf["intHPMax"]?.DeepClone();
            o["intState"] = leaf["intState"]?.DeepClone();
        }
        return o;
    }

    /// <summary>
    /// skua.swf Monster.getTargetMonster: the targeted monster's data, or <c>{}</c> (and the target dropped) when
    /// there is none or it is dead.
    /// </summary>
    public JsonObject TargetMonsterJson()
    {
        lock (_gate)
        {
            TargetRef? t = _target;
            JsonObject? leaf = TargetLeaf(t);
            if (t is null || (leaf is not null && JsonUtil.AsInt(leaf["intHP"]) <= 0))
            {
                CancelTarget();
                return new JsonObject();
            }
            if (t.Kind != 'm' || World.GetMonster(t.Id) is not MonsterInfo m)
                return new JsonObject();
            return MonData(m);
        }
    }

    /// <summary><c>world.myAvatar.target</c>: <c>{npcType, objData, dataLeaf}</c>, or null.</summary>
    public JsonObject? TargetJson()
    {
        lock (_gate)
        {
            TargetRef? t = _target;
            if (t is null)
                return null;
            if (t.Kind == 'm')
            {
                MonsterInfo? m = World.GetMonster(t.Id);
                if (m is null)
                    return null;
                return new JsonObject { ["npcType"] = "monster", ["objData"] = MonsterObjData(m), ["dataLeaf"] = m.Live?.DeepClone() };
            }
            if (IsMe(t))
            {
                PlayerInfo? me = World.GetPlayer();
                return new JsonObject { ["npcType"] = "player", ["uid"] = World.MyUserId, ["pnm"] = World.MyUsername, ["objData"] = me?.ObjData, ["dataLeaf"] = me?.DataLeaf };
            }
            AreaUserInfo? u = World.GetAreaUsers().FirstOrDefault(x => x.UserId == t.Id);
            return u is null ? null : new JsonObject { ["npcType"] = "player", ["uid"] = t.Id, ["pnm"] = u.Name.ToLowerInvariant(), ["objData"] = u.ObjData, ["dataLeaf"] = u.Leaf };
        }
    }

    // Monster.getBestMonsterTarget: in this cell with a clip, live ones first, then lowest HP, then lowest MonMapID.
    private MonsterInfo? BestMonster(Func<MonsterInfo, bool> match)
    {
        string cell = World.GetArea().Cell;
        return World.GetMonsters()
            .Where(m => m.Live is not null && JsonUtil.AsString(m.Live["strFrame"]) == cell && match(m))
            .OrderBy(m => m.Hp > 0 ? 0 : 1)
            .ThenBy(m => m.Hp)
            .ThenBy(m => m.MonMapId)
            .FirstOrDefault();
    }

    /// <summary>skua.swf attackMonsterByName (<c>*</c> = any): target and approach. False when none is here.</summary>
    public bool AttackMonster(string name)
    {
        return Attack(BestMonsterByName(name), approach: true);
    }

    /// <summary>skua.swf attackMonsterByID: matches MonMapID or MonID.</summary>
    public bool AttackMonster(int id)
    {
        return Attack(BestMonster(m => m.MonMapId == id || m.MonId == id), approach: true);
    }

    /// <summary>skua.swf selectMonsterByName: target without approaching.</summary>
    public bool SelectMonster(string name)
    {
        return Attack(BestMonsterByName(name), approach: false);
    }

    /// <summary>skua.swf selectMonsterByID.</summary>
    public bool SelectMonster(int id)
    {
        return Attack(BestMonster(m => m.MonMapId == id || m.MonId == id), approach: false);
    }

    private MonsterInfo? BestMonsterByName(string name)
    {
        string lower = name.ToLowerInvariant();
        return BestMonster(m => name == "*" || m.Name.ToLowerInvariant().Contains(lower, StringComparison.Ordinal));
    }

    private bool Attack(MonsterInfo? m, bool approach)
    {
        if (m is null)
            return false;
        lock (_gate)
        {
            SetTargetCore(new TargetRef('m', m.MonMapId, null));
            if (approach)
                ApproachTarget();
        }
        return true;
    }

    /// <summary>skua.swf Player.attackPlayer.</summary>
    public bool AttackPlayer(string name)
    {
        AreaUserInfo? u = World.GetAreaUsers().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (u?.UserId is not int uid)
            return false;
        lock (_gate)
        {
            SetTargetCore(new TargetRef('p', uid, u.Name.ToLowerInvariant()));
            ApproachTarget();
        }
        return true;
    }

    // ---------------------------------------------------------------- testAction / gar

    private sealed record Candidate(TargetRef Ref, JsonObject Leaf, bool Monster, string? Race);

    private List<Candidate> CellTargets()
    {
        string cell = World.GetArea().Cell;
        List<Candidate> list = new();
        foreach (MonsterInfo m in World.GetMonsters())
        {
            if (m.Live is JsonObject leaf && JsonUtil.AsString(leaf["strFrame"]) == cell)
                list.Add(new Candidate(new TargetRef('m', m.MonMapId, null), leaf, true, m.Race));
        }
        foreach (AreaUserInfo u in World.GetAreaUsers())
        {
            if (u.UserId is int uid && u.Cell == cell)
                list.Add(new Candidate(new TargetRef('p', uid, u.Name.ToLowerInvariant()), u.Leaf, false, null));
        }
        return list;
    }

    // World.testAction: readiness, mana, stuns, target selection, then getActionResult.
    private void TestAction(JsonObject a, int depth)
    {
        if (depth > 3)
            return;
        PlayerInfo? me = World.GetPlayer();
        JsonObject? cLeaf = me?.DataLeaf;
        if (cLeaf is null)
            return;
        int tgtMin = JsonUtil.AsIntOrNull(a["tgtMin"]) ?? 1;
        int tgtMax = JsonUtil.AsIntOrNull(a["tgtMax"]) ?? 1;
        string tgt = JsonUtil.AsString(a["tgt"]) ?? string.Empty;
        string typ = JsonUtil.AsString(a["typ"]) ?? string.Empty;
        string? err = null;
        if (!ActionTimeCheck(a))
            err = "not ready";
        JsonObject sta = cLeaf["sta"] as JsonObject ?? World.GetStats() ?? new JsonObject();
        if (err is null && a["mp"] is not null
            && !SkillRules.HasMana(JsonUtil.AsInt(a["mp"]), JsonUtil.AsDoubleOrNull(sta["$cmc"]) ?? 1, JsonUtil.AsInt(cLeaf["intMP"])))
            err = "mana";
        if (err is null && JsonUtil.AsString(a["ref"]) == "i1" && string.IsNullOrEmpty(JsonUtil.AsString(a["sArg1"])))
            err = "no item";
        if (err is null && cLeaf["auras"] is JsonArray myAuras
            && myAuras.OfType<JsonObject>().Any(x => JsonUtil.AsString(x["cat"]) is string cat && SkillRules.DisablingAuraCategories.Contains(cat)))
            err = "stunned";

        TargetRef? t = _target;
        List<Candidate> scan = CellTargets();
        TargetRef self = Me();
        switch (tgt)
        {
            case "h":
                if (t is null)
                {
                    if (tgtMin > 0)
                    {
                        Candidate? first = scan.FirstOrDefault(x => x.Monster && JsonUtil.AsInt(x.Leaf["intState"], 1) != 0);
                        if (first is not null)
                        {
                            SetTargetCore(first.Ref);
                            TestAction(a, depth + 1);
                            return;
                        }
                        err ??= "no target";
                        if (typ == "aa")
                            CancelAutoAttackCore();
                    }
                }
                else if (t.Kind == 'p')
                {
                    err ??= "bad target";
                    if (typ == "aa")
                        CancelAutoAttackCore();
                }
                else if (tgtMin > 0 && TargetLeaf(t) is JsonObject tl && JsonUtil.AsInt(tl["intState"], 1) == 0)
                {
                    err ??= "target dead";
                }
                break;

            case "f":
                if (t is null)
                {
                    SetTargetCore(self);
                    t = self;
                }
                if (t.Kind == 'm')
                    t = self;
                break;

            case "s":
                t = self;
                break;
        }
        if (err is not null)
            return;

        TargetRef? primary = t;
        string? filter = JsonUtil.AsString(a["filter"]);
        List<(Candidate C, int Hp)> targets = new();
        foreach (Candidate x in scan)
        {
            if (JsonUtil.AsInt(x.Leaf["intState"], 1) == 0)
                continue;
            if (filter is not null && x.Race is { Length: > 0 } race && !race.Equals(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (tgt == "h" && !x.Monster)
                continue;
            if (tgt == "f" && x.Monster)
                continue;
            if (tgt == "s" && !Equals(x.Ref, self) && !(x.Ref.Kind == 'p' && x.Ref.Id == self.Id))
                continue;
            targets.Add((x, JsonUtil.AsInt(x.Leaf["intHP"])));
        }
        targets = targets.OrderBy(x => x.Hp).ToList();
        int pi = targets.FindIndex(x => primary is not null && x.C.Ref.Kind == primary.Kind && x.C.Ref.Id == primary.Id);
        if (pi > 0)
        {
            (Candidate C, int Hp) p = targets[pi];
            targets.RemoveAt(pi);
            targets.Insert(0, p);
        }
        targets = targets.Take(Math.Max(0, tgtMax)).ToList();
        JsonObject? tLeaf = targets.Count > 0 ? targets[0].C.Leaf : null;
        if (JsonUtil.AsInt(cLeaf["intState"], 1) == 0)
            return;
        if (!JsonUtil.AsBool(a["lock"]) && (tLeaf is null || JsonUtil.AsInt(tLeaf["intState"], 1) != 0) && targets.Count >= tgtMin)
            GetActionResult(a, targets.Select(x => x.C.Ref).ToList());
        if ((_target is null || tLeaf is null || JsonUtil.AsInt(tLeaf["intState"], 1) == 0) && tgt == "h")
            ExitCombatCore();
    }

    /// <summary>The <c>gar</c> target list: <c>a1&gt;m:5,a1&gt;m:6</c>, or <c>a1&gt;</c> with no target.</summary>
    public static string GarTargets(string skillRef, IReadOnlyList<TargetRef> targets)
    {
        return targets.Count == 0 ? skillRef + ">" : string.Join(',', targets.Select(t => $"{skillRef}>{t.Subject}"));
    }

    // World.getActionResult: sends the gar, locks the skill, starts the global cooldown.
    private void GetActionResult(JsonObject a, List<TargetRef> targets)
    {
        string skillRef = JsonUtil.AsString(a["ref"]) ?? "aa";
        List<object?> args = new() { _actionId, GarTargets(skillRef, targets) };
        if (skillRef == "i1")
            args.Add(JsonUtil.AsString(a["sArg1"]) ?? string.Empty);
        args.Add("wvz");
        Send(Packets.Xt("gar", 1, args.ToArray()));
        long now = Now;
        a["lock"] = true;
        a["actID"] = _actionId;
        _actionId = _actionId >= 250 ? 0 : _actionId + 1;
        long previous = JsonUtil.AsLong(a["ts"]);
        a["lastTS"] = previous;
        a["ts"] = now;
        if (!JsonUtil.AsBool(a["auto"]))
        {
            _lastAct = (a, previous, _gcdStart, now);
            _gcdStart = now;
            if (_autoTimer is null && JsonUtil.AsString(a["tgt"]) == "h" && AutoAttackSkill() is JsonObject aa && !ReferenceEquals(aa, a))
                TestAction(aa, 0);
        }
        else
        {
            // The server's answer re-arms the auto attack (showActionResult); if none comes, try again anyway.
            long cd = SkillRules.EffectiveCooldown(JsonUtil.AsInt(a["cd"], 2000), null, Haste());
            ArmAutoAttack(cd + (long)Options.AutoAttackRetrySlack.TotalMilliseconds);
        }
    }

    // "Please slow down. Last action was too soon!" instead of an answer: the server did not do the last skill.
    private void ActionRefused()
    {
        lock (_gate)
        {
            if (_lastAct is not { } u)
                return;
            long now = Now;
            if (now - u.At > 1000 || (JsonUtil.AsLongOrNull(u.Skill["_refusedAt"]) is long refusedAt && now - refusedAt < 3000))
                return;
            _lastAct = null;
            u.Skill["_refusedAt"] = now;
            u.Skill["ts"] = u.PrevTs;
            u.Skill["lock"] = false;
            if (_gcdStart == u.At)
                _gcdStart = u.PrevGcd;
            RaiseLog($"Server refused {JsonUtil.AsString(u.Skill["ref"])} (too soon); cooldown restored.");
        }
    }

    private void ArmAutoAttack(long delayMs)
    {
        CancelAutoAttackCore();
        ITimer? timer = null;
        timer = _time.CreateTimer(_ =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_autoTimer, timer))
                    return;
                _autoTimer = null;
                timer?.Dispose();
                AutoActionHandler();
            }
        }, null, TimeSpan.FromMilliseconds(Math.Max(100, delayMs)), Timeout.InfiniteTimeSpan);
        _autoTimer = timer;
    }

    // World.autoActionHandler
    private void AutoActionHandler()
    {
        TargetRef? t = _target;
        JsonObject? leaf = TargetLeaf(t);
        if (LiteFlag("bUntargetDead") && t is not null && !IsMe(t) && leaf is not null && JsonUtil.AsInt(leaf["intState"], 1) == 0)
        {
            CancelTarget();
            return;
        }
        JsonObject? me = World.GetPlayer()?.DataLeaf;
        if (me is not null && JsonUtil.AsInt(me["intState"], 1) != 0 && leaf is not null && JsonUtil.AsInt(leaf["intState"], 1) != 0 && AutoAttackSkill() is JsonObject aa)
            TestAction(aa, 0);
        else
            ExitCombatCore();
    }

    // Game sar / sars: the answer to one of our actions.
    private void OnActionResult(JsonObject o, bool sar)
    {
        JsonObject? r = sar ? o["actionResult"] as JsonObject : o;
        if (r is null)
            return;
        string cInf = JsonUtil.AsString(r["cInf"]) ?? JsonUtil.AsString(o["cInf"]) ?? string.Empty;
        bool mine = cInf == $"p:{World.MyUserId}";
        int? actId = JsonUtil.AsIntOrNull(o["actID"]) ?? JsonUtil.AsIntOrNull(r["actID"]);
        if (actId is null)
            return;
        if (JsonUtil.AsIntOrNull(o["iRes"]) == 0)
        {
            if (cInf.StartsWith('p'))
                ShowActionResult(false, actId.Value);
            return;
        }
        if (mine)
            ShowActionResult(true, actId.Value);
    }

    // World.showActionResult
    private void ShowActionResult(bool success, int actId)
    {
        lock (_gate)
        {
            JsonObject? a = _active.OfType<JsonObject>().FirstOrDefault(x => JsonUtil.AsIntOrNull(x["actID"]) == actId);
            if (a is null)
                return;
            a["lock"] = false;
            a["actID"] = -1;
            long now = Now;
            if (JsonUtil.AsString(a["typ"]) == "aa" || JsonUtil.AsBool(a["auto"]))
                ArmAutoAttack(SkillRules.AutoAttackDelay(JsonUtil.AsInt(a["cd"], 2000), Haste(), now, JsonUtil.AsLong(a["ts"], now)));
            if (!success)
            {
                a["ts"] = a["lastTS"]?.DeepClone() ?? 0;
            }
            else
            {
                foreach (JsonObject x in _active.OfType<JsonObject>())
                {
                    if (!ReferenceEquals(x, a))
                        x["lock"] = false;
                }
            }
        }
    }

    /// <summary>The skill index Skua would use for the slot holding <paramref name="skillRef"/>, or -1.</summary>
    public int IndexOfSkill(string skillRef)
    {
        lock (_gate)
            return _active.FindIndex(x => x is not null && JsonUtil.AsString(x["ref"]) == skillRef);
    }

    private static string Invariant(long v) => v.ToString(CultureInfo.InvariantCulture);
}
