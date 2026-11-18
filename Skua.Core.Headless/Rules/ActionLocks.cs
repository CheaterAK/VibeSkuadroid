using System.Text.Json.Nodes;

namespace Skua.Core.Headless.Rules;

/// <summary>
/// World.lock: the client's per-action throttles. The game refuses to send an action again before its cooldown, which
/// keeps the server from answering "Please slow down". Skua reads <c>world.lock.&lt;action&gt;</c>
/// (<c>{cd, ts}</c>) to wait for an action to be available.
/// </summary>
/// <remarks>The cooldowns are those of Seraph app/lib/skua/swfworld.js (LOCKS), ported from World.as, MIT.</remarks>
public sealed class ActionLocks
{
    /// <summary>Action name to cooldown in ms.</summary>
    public static IReadOnlyDictionary<string, int> Cooldowns { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["loadShop"] = 3000, ["loadEnhShop"] = 3000, ["loadHairShop"] = 3000, ["equipItem"] = 1500, ["unequipItem"] = 1500,
        ["buyItem"] = 1000, ["sellItem"] = 1000, ["getMapItem"] = 1000, ["tryQuestComplete"] = 1250, ["acceptQuest"] = 1000,
        ["doIA"] = 1000, ["rest"] = 1900, ["who"] = 3000, ["tfer"] = 3000, ["wearItem"] = 1500, ["unwearItem"] = 1500,
        ["addLoadout"] = 1500, ["removeLoadout"] = 1500, ["wearLoadout"] = 3000, ["equipLoadout"] = 3000,
        ["friendshipTalk"] = 1000, ["friendshipChoice"] = 1000, ["friendshipInfo"] = 1500, ["friendshipGift"] = 1500,
        ["friendshipStats"] = 3000, ["dungeonQueue"] = 5000, ["dungeonRejoin"] = 5000, ["dungeonMTC"] = 1000,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _stamps = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ActionLocks(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// World.coolDown: true (and the action is stamped) when it may be sent now; false while it cools down.
    /// Actions without a lock are always allowed.
    /// </summary>
    public bool TryAcquire(string action)
    {
        if (!Cooldowns.TryGetValue(action, out int cd))
            return true;
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_gate)
        {
            if (now - _stamps.GetValueOrDefault(action) < cd)
                return false;
            _stamps[action] = now;
            return true;
        }
    }

    /// <summary>Marks an action as just sent (Skua sent the packet itself, bypassing the game function).</summary>
    public void Stamp(string action)
    {
        if (!Cooldowns.ContainsKey(action))
            return;
        lock (_gate)
            _stamps[action] = _time.GetUtcNow().ToUnixTimeMilliseconds();
    }

    /// <summary>True when the action is not cooling down.</summary>
    public bool IsAvailable(string action)
    {
        if (!Cooldowns.TryGetValue(action, out int cd))
            return true;
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_gate)
            return now - _stamps.GetValueOrDefault(action) >= cd;
    }

    public void Reset()
    {
        lock (_gate)
            _stamps.Clear();
    }

    /// <summary><c>world.lock</c> as the game holds it: <c>{"loadShop":{"cd":3000,"ts":...}, ...}</c>.</summary>
    public JsonObject ToJson()
    {
        JsonObject o = new();
        lock (_gate)
        {
            foreach (KeyValuePair<string, int> pair in Cooldowns)
                o[pair.Key] = new JsonObject { ["cd"] = pair.Value, ["ts"] = _stamps.GetValueOrDefault(pair.Key) };
        }
        return o;
    }
}
