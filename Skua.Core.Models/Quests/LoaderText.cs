namespace Skua.Core.Models.Quests;

/// <summary>
/// The Loader's input and text rules (desktop Loader window; the Android app's Loader page uses the same): which ID
/// lists it accepts, how they are split, the quest list search, the copy formats and the update range.
/// </summary>
public static class LoaderText
{
    private static readonly char[] Separators = { ',', ' ' };

    /// <summary>Digits, commas and spaces only, with at least one digit (the Load button's check).</summary>
    public static bool IsIdList(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;
        string normalized = input.Replace(",", "").Replace(" ", "");
        return normalized.Length > 0 && normalized.All(char.IsDigit);
    }

    /// <summary>The IDs of a list split on commas and spaces (a part that is not a number counts as 0, as on the desktop).</summary>
    public static int[] ParseIds(string? input)
    {
        string[] parts = (input ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        int[] ids = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], out int id))
                ids[i] = id;
        }
        return ids;
    }

    /// <summary>The quest list search: name or ID contains the query (blank matches all).</summary>
    public static bool QuestMatches(int id, string? name, string? query)
    {
        string q = query?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(q)
            || (name ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
            || id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Copy names": the names joined with commas.</summary>
    public static string Names(IEnumerable<string> names) => string.Join(",", names);

    /// <summary>"Copy IDs": the IDs joined with commas.</summary>
    public static string Ids(IEnumerable<int> ids) => string.Join(",", ids);

    /// <summary>"Copy names and IDs": one "// name | id" line per quest.</summary>
    public static string NamesAndIds(IEnumerable<(string Name, int Id)> quests) =>
        string.Join(Environment.NewLine, quests.Select(q => NameAndIdLine(q.Name, q.Id)));

    /// <summary>One line of "Copy names and IDs".</summary>
    public static string NameAndIdLine(string name, int id) => $"// {name} | {id}";

    /// <summary>The update range "startID, endID"; null when valid, else the message the desktop shows.</summary>
    public static string? ParseRange(string? input, out int startId, out int endId)
    {
        startId = endId = 0;
        string[] parts = (input ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out startId) || !int.TryParse(parts[1], out endId))
            return "Invalid format. Use: startID, endID";
        if (startId > endId)
            return "Start ID must be less than or equal to End ID";
        return null;
    }
}
