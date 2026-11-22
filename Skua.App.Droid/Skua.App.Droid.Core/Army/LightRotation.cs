using Skua.Core.Models.Skills;
using Skua.Core.Skills;

namespace Skua.App.Droid.Core.Army;

/// <summary>One step of a light account's skill rotation.</summary>
/// <param name="Index">Skill slot (0 auto attack, 1-5 skills), as in Skua's rotation strings.</param>
/// <param name="Rules">HP/MP conditions the light client evaluates; empty: always.</param>
/// <param name="SkipOnMatch">Skua's "S" flag: skip the skill when the rules match (instead of using it).</param>
/// <param name="WaitMs">"WW" rule: wait up to this long for the skill to come off cooldown.</param>
/// <param name="Unsupported">Rules a light client cannot judge (auras, party HP): treated as met, reported once.</param>
public sealed record LightSkillStep(int Index, IReadOnlyList<LightSkillRule> Rules, bool SkipOnMatch, int WaitMs, IReadOnlyList<string> Unsupported);

/// <summary>"H&gt;50%", "M&lt;30#" and the like.</summary>
public sealed record LightSkillRule(bool Health, bool Greater, double Value, bool Percent)
{
    public bool Matches(int current, int max)
    {
        double v = Percent ? (max <= 0 ? 0 : current * 100.0 / max) : current;
        return Greater ? v > Value : v < Value;
    }
}

/// <summary>
/// A rotation for a light account, read from the same skill strings the full engine uses (Skua's Advanced Skills,
/// <c>1 H&gt;70% | 2 | 3S | 4WW500</c>) through Skua's own parser (<see cref="AdvancedSkillsParser"/>, a pure static
/// function; no Skua.Core service is created). Light accounts judge HP and MP rules, the wait rule and the skip flag;
/// aura and party rules count as met.
/// </summary>
public sealed class LightRotation
{
    public static LightRotation Default { get; } = Parse("1 | 2 | 3 | 4");

    private LightRotation(IReadOnlyList<LightSkillStep> steps, bool waitForCooldown, int timeoutMs, string text)
    {
        Steps = steps;
        WaitForCooldown = waitForCooldown;
        TimeoutMs = timeoutMs;
        Text = text;
    }

    public IReadOnlyList<LightSkillStep> Steps { get; }

    /// <summary>Skua's "Wait for cooldown" mode (else "Use if available": a skill on cooldown is passed over).</summary>
    public bool WaitForCooldown { get; }

    public int TimeoutMs { get; }

    public string Text { get; }

    /// <summary>Parses a rotation; an unusable string gives the default 1-2-3-4.</summary>
    public static LightRotation Parse(string? skills, string useMode = "UseIfAvailable", int timeoutMs = 100)
    {
        string text = string.IsNullOrWhiteSpace(skills) ? "1 | 2 | 3 | 4" : skills.Trim();
        string line = useMode == "WaitForCooldown" ? $"Base=Light={text}={timeoutMs}" : $"Base=Light={text}=Use if Available";
        AdvancedSkillsConfigJson config;
        try
        {
            config = AdvancedSkillsParser.ParseTextToJson(line);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return text == "1 | 2 | 3 | 4" ? throw e : Default;
        }
        if (!config.TryGetValue("Light", out Dictionary<string, SkillModeJson>? modes) || !modes.TryGetValue("Base", out SkillModeJson? mode) || mode.Skills.Count == 0)
            return text == "1 | 2 | 3 | 4" ? new LightRotation([], false, 100, text) : Default;
        List<LightSkillStep> steps = new();
        foreach (AdvancedSkillJson skill in mode.Skills)
        {
            List<LightSkillRule> rules = new();
            List<string> unsupported = new();
            int wait = 0;
            foreach (SkillRuleJson rule in skill.Rules ?? [])
            {
                switch (rule.Type)
                {
                    case "Health" or "Mana" when rule.Value is float value:
                        rules.Add(new LightSkillRule(rule.Type == "Health", rule.Comparison == "greater", value, rule.IsPercentage ?? true));
                        break;
                    case "Wait":
                        wait = rule.Timeout ?? 0;
                        break;
                    case "Skip":
                        break;
                    default:
                        unsupported.Add(rule.Type);
                        break;
                }
            }
            steps.Add(new LightSkillStep(skill.SkillId, rules, skill.SkipOnMatch, wait, unsupported));
        }
        return new LightRotation(steps, mode.SkillUseMode == "WaitForCooldown", mode.SkillTimeout, text);
    }

    /// <summary>Whether a step's rules allow using it now (the skip flag inverts).</summary>
    public static bool Allows(LightSkillStep step, int hp, int hpMax, int mp, int mpMax)
    {
        if (step.Rules.Count == 0)
            return true;
        bool match = step.Rules.All(r => r.Health ? r.Matches(hp, hpMax) : r.Matches(mp, mpMax));
        return step.SkipOnMatch ? !match : match;
    }
}
