using System;
using System.Linq;
using Skua.Core.Interfaces;

namespace Skua.Plugins.Shared.DailyTracker;

/// <summary>
/// Reads whether the tracked daily and weekly quests (and ultra boss lockouts) are done today, through Skua's script
/// API. Extracted unchanged from the desktop plugin's window; blocking (call it off the UI thread).
/// </summary>
internal static class DailyQuestStatusReader
{
    /// <summary>
    /// Loads every tracked quest the client does not know yet in one batch (Skua sends them in chunks of 30) and
    /// waits up to about 3 s for them to arrive. Never throws.
    /// </summary>
    public static void EnsureAllQuestsLoaded(IScriptInterface bot)
    {
        int[] tracked = DailyQuestCatalog.TrackedQuests;
        try
        {
            if (tracked.All(id => bot.Quests.Tree.Any(q => q.ID == id)))
                return;

            bot.Quests.Load(tracked);
            bot.Wait.ForTrue(() => tracked.All(id => bot.Quests.Tree.Any(q => q.ID == id)), null, 15, 200);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Batch quest loading failed: {ex}");
        }
    }

    /// <summary>
    /// Whether <paramref name="questId"/> is done today. A Lord of Order step also counts as done when the chain's
    /// quest value reached it.
    /// </summary>
    /// <returns>False (with <paramref name="error"/>) only when the daily check itself failed; the Lord of Order
    /// fallback failing leaves <paramref name="isDone"/> false.</returns>
    public static bool TryReadState(IScriptInterface bot, int questId, out bool isDone, out Exception? error)
    {
        error = null;
        try
        {
            isDone = bot.Quests.IsDailyComplete(questId);
        }
        catch (Exception ex)
        {
            isDone = false;
            error = ex;
            return false;
        }

        if (!isDone && DailyQuestCatalog.IsLordOfOrder(questId))
        {
            try
            {
                int targetVal = questId - DailyQuestCatalog.LordOfOrderFirst + 1;
                if (bot.Flash.CallGameFunction<int>("world.getQuestValue", DailyQuestCatalog.LordOfOrderSlot) >= targetVal)
                    isDone = true;
            }
            catch
            {
            }
        }
        return true;
    }
}
