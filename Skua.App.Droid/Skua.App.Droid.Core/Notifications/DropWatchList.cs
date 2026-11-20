namespace Skua.App.Droid.Core.Notifications;

/// <summary>
/// The drop watch list (the desktop's Notify Drop window): item names that trigger a notification when the item
/// drops or is obtained.
/// </summary>
/// <remarks>
/// Input as on the desktop: several names separated by <c>|</c>, each trimmed, duplicates ignored. The desktop matches
/// a drop by exact name; here the comparison also ignores case and surrounding spaces (the game's item names are
/// unique regardless of case, and names typed on a phone keyboard are often capitalised differently).
/// </remarks>
public static class DropWatchList
{
    /// <summary>The desktop's separator for several names in one input.</summary>
    public const char Separator = '|';

    /// <summary>Longest name kept (AQW item names are far shorter).</summary>
    public const int MaxNameLength = 120;

    /// <summary>Most names kept.</summary>
    public const int MaxEntries = 500;

    /// <summary>The names in an input line, trimmed, without empty entries.</summary>
    public static IReadOnlyList<string> Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];
        return input.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Clean)
            .Where(n => n.Length > 0)
            .ToList();
    }

    /// <summary><paramref name="current"/> with the names of <paramref name="input"/> added (no duplicates, order kept).</summary>
    public static IReadOnlyList<string> Add(IEnumerable<string> current, string? input)
    {
        List<string> result = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in current.Select(Clean).Concat(Parse(input)))
        {
            if (name.Length > 0 && result.Count < MaxEntries && seen.Add(name))
                result.Add(name);
        }
        return result;
    }

    /// <summary>Whether <paramref name="itemName"/> is on the list.</summary>
    public static bool Matches(IEnumerable<string> watched, string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return false;
        string name = itemName.Trim();
        foreach (string w in watched)
        {
            if (string.Equals(w.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Clean(string name)
    {
        string trimmed = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }
}
