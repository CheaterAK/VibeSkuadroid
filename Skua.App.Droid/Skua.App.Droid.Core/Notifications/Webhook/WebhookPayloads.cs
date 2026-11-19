using System.Text;
using System.Text.Json.Nodes;
using Skua.App.Droid.Core.Engine;

namespace Skua.App.Droid.Core.Notifications;

/// <summary>
/// The Discord cards the desktop's DiscordWebhookService and Scheduler send (same titles, texts and colours), built
/// only from the values a card may carry: who (the character name when the user allows it, else "VibeSkua Bot"),
/// script names, item names, map names, counts and durations. No builder takes free text (exception messages,
/// log lines) or anything account related (account name, password, token, e-mail), so none can end up in a payload.
/// </summary>
/// <remarks>
/// Every payload sets <c>allowed_mentions.parse = []</c> (an item or script name containing @everyone pings nobody)
/// and escapes Discord markdown in names; names are cut to <see cref="MaxNameLength"/> characters.
/// </remarks>
public static class WebhookPayloads
{
    /// <summary>The desktop's name for the bot when no character name is shown.</summary>
    public const string AnonymousName = "VibeSkua Bot";

    public const int Green = 0x00FF00;
    public const int Red = 0xFF0000;
    public const int Orange = 0xFFA500;
    public const int Gold = 0xFFD700;

    public const int MaxNameLength = 100;

    /// <summary>"Script Started": <c>**{who}** has begun execution of **{script}**.</c></summary>
    public static JsonObject ScriptStarted(string who, string? script) =>
        Card("Script Started", $"**{Md(who)}** has begun execution of **{Md(script ?? "a script")}**.", Green);

    /// <summary>"Farming Session Concluded" with what the desktop shows (gold earned, time elapsed) and Skua's counters.</summary>
    public static JsonObject SessionConcluded(string who, string? script, TimeSpan elapsed, long? goldEarned, ScriptCounters? counters, string? map)
    {
        List<(string, string, bool)> fields = new();
        if (goldEarned is long gold && gold != 0)
            fields.Add(("Gold Earned", gold > 0 ? $"{gold:N0}" : "0", true));
        if (counters is not null)
        {
            fields.Add(("Kills", counters.Kills.ToString("N0"), true));
            fields.Add(("Drops", counters.Drops.ToString("N0"), true));
            fields.Add(("Quests", counters.QuestsCompleted.ToString("N0"), true));
            if (counters.Deaths > 0)
                fields.Add(("Deaths", counters.Deaths.ToString("N0"), true));
            if (counters.Relogins > 0)
                fields.Add(("Relogins", counters.Relogins.ToString("N0"), true));
        }
        if (!string.IsNullOrWhiteSpace(map))
            fields.Add(("Map", Md(map), true));
        fields.Add(("Time Elapsed", Duration(elapsed), false));
        return Card("Farming Session Concluded", $"**{Md(who)}** has stopped execution of **{Md(script ?? "a script")}**.", Red, fields);
    }

    /// <summary>"Script Error": the script name only (the exception text stays on the device).</summary>
    public static JsonObject ScriptError(string who, string? script) =>
        Card("Script Error", script is null ? $"**{Md(who)}** crashed unexpectedly." : $"**{Md(who)}** crashed unexpectedly while running **{Md(script)}**.", Red);

    /// <summary>"Kicked &amp; Relogging" / "Disconnected &amp; Relogging".</summary>
    public static JsonObject Relogin(string who, bool kicked) =>
        Card(kicked ? "Kicked & Relogging" : "Disconnected & Relogging", $"**{Md(who)}** is attempting to reconnect.", Orange);

    /// <summary>The scheduler's "Scheduler Paused" card.</summary>
    public static JsonObject SchedulerPaused() =>
        Card("Scheduler Paused", "**Scheduler** is waiting for account to reconnect before running next script...", Gold);

    /// <summary>The desktop's drop alert ("Rare Drop!") as an embed (no screenshot: there is no game view).</summary>
    public static JsonObject DropAlert(string who, string item, int quantity, bool obtained, string? map)
    {
        string wiki = "https://aqwwiki.wikidot.com/" + Uri.EscapeDataString(Cut(item));
        string text = $"**{Md(who)}** just {(obtained ? "got" : "saw a drop of")} **[{Md(item)}]({wiki})** x{Math.Max(1, quantity)}!";
        List<(string, string, bool)> fields = new();
        if (!string.IsNullOrWhiteSpace(map))
            fields.Add(("Map", Md(map), true));
        return Card("Rare Drop!", text, Gold, fields);
    }

    /// <summary>The settings page's test card.</summary>
    public static JsonObject Test() =>
        Card("Discord Webhook Test Successful!", "Your VibeSkua Discord Webhooks are perfectly configured.", Green);

    /// <summary>The desktop's duration format, hours not wrapping at a day.</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
            t = TimeSpan.Zero;
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private static JsonObject Card(string title, string description, int color, IEnumerable<(string Name, string Value, bool Inline)>? fields = null)
    {
        JsonObject embed = new()
        {
            ["title"] = title,
            ["description"] = description,
            ["color"] = color,
        };
        JsonArray list = new();
        foreach ((string name, string value, bool inline) in fields ?? [])
            list.Add(new JsonObject { ["name"] = name, ["value"] = value, ["inline"] = inline });
        if (list.Count > 0)
            embed["fields"] = list;
        return new JsonObject
        {
            ["username"] = "VibeSkua",
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            ["embeds"] = new JsonArray(embed),
        };
    }

    private static string Cut(string text)
    {
        string clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > MaxNameLength ? clean[..MaxNameLength] : clean;
    }

    /// <summary>Discord markdown escaped (so a name cannot format or link the card).</summary>
    private static string Md(string text)
    {
        StringBuilder sb = new();
        foreach (char c in Cut(text))
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '[' or ']' or '(' or ')' or '#' or '<' or '@')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
