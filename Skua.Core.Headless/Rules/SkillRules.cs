namespace Skua.Core.Headless.Rules;

/// <summary>
/// Skill timing rules of the game client: haste shortens cooldowns (capped at 50 %, slows down to -100 %), every
/// non-auto skill shares a global cooldown of 1.5 s scaled by haste, and a skill waits for its own cooldown from the
/// moment its <c>gar</c> was sent.
/// </summary>
/// <remarks>
/// From skua.swf Skills.actionTimeCheck (this repository, Skua.AS3) and the game's World.actionTimeCheck as ported in
/// Seraph app/lib/skua/swfworld.js (MIT).
/// </remarks>
public static class SkillRules
{
    /// <summary>World.GCDO: the base global cooldown in ms.</summary>
    public const int BaseGlobalCooldown = 1500;

    /// <summary><c>1 - min(max($tha, -1), 0.5)</c>: the factor every cooldown is multiplied by.</summary>
    public static double HasteMultiplier(double haste)
    {
        return 1 - Math.Min(Math.Max(haste, -1), 0.5);
    }

    /// <summary>Game.as stu: <c>GCD = round(haste multiplier * GCDO)</c>.</summary>
    public static int GlobalCooldown(double haste)
    {
        return (int)Math.Round(HasteMultiplier(haste) * BaseGlobalCooldown, MidpointRounding.AwayFromZero);
    }

    /// <summary>A skill's effective cooldown: <c>round((OldCD ?? cd) * haste multiplier)</c>.</summary>
    public static long EffectiveCooldown(int cooldown, int? oldCooldown, double haste)
    {
        return (long)Math.Round((oldCooldown ?? cooldown) * HasteMultiplier(haste), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// skua.swf Skills.actionTimeCheck: the global cooldown always applies (auto attack included) and then the skill's
    /// own cooldown since its last use (<paramref name="lastUse"/>, ms since the epoch).
    /// </summary>
    public static bool IsOffCooldown(long now, long lastUse, int cooldown, int? oldCooldown, double haste, long globalCooldownStart, int globalCooldown)
    {
        if (now - globalCooldownStart < globalCooldown)
            return false;
        return now - lastUse >= EffectiveCooldown(cooldown, oldCooldown, haste);
    }

    /// <summary>World.testAction's mana test: <c>round(mp * $cmc)</c> must not exceed the current mana.</summary>
    public static bool HasMana(int manaCost, double manaCostMultiplier, int currentMana)
    {
        return Math.Round(manaCost * manaCostMultiplier, MidpointRounding.AwayFromZero) <= currentMana;
    }

    /// <summary>Aura categories that stop a character from acting (World.testAction).</summary>
    public static IReadOnlyCollection<string> DisablingAuraCategories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "stun", "stone", "paralyze", "disable", "disabled",
    };

    /// <summary>
    /// When the next auto attack should fire after the server answered one (World.showActionResult):
    /// <c>cd - (now - ts)</c> clamped to [cd - 100, cd], with <c>cd</c> scaled by haste.
    /// </summary>
    public static long AutoAttackDelay(int cooldown, double haste, long now, long sentAt)
    {
        long cd = (long)Math.Round(cooldown * HasteMultiplier(haste), MidpointRounding.AwayFromZero);
        long wait = cd - (now - sentAt);
        return Math.Min(cd, Math.Max(cd - 100, wait));
    }
}
