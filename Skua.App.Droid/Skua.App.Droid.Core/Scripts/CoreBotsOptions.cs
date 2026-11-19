using Skua.App.Droid.Core.Engine;
using System.Text.RegularExpressions;

namespace Skua.App.Droid.Core.Scripts;

/// <summary>
/// CoreBots' own settings ("CBO"): <c>options/CBO_Storage(&lt;username&gt;).txt</c> in Skua's data folder, one
/// <c>Key: Value</c> line per setting, read by CoreBots.ReadCBO at every start (class/gear sets for solo, farm, dodge
/// and boss, private rooms, delays, ...). The desktop edits them in a window; here they are edited as a
/// <see cref="ScriptOptionSet"/> in the same editor as script options.
/// </summary>
/// <remarks>
/// The keys are not hard-coded: they are read from the synced CoreBots sources (every <c>CBOBool("..")</c>,
/// <c>CBOInt("..")</c> and <c>CBOString("..")</c> call), so new CoreBots settings appear without an app update.
/// Without the file CoreBots runs on its defaults (no class or gear switching, public rooms, its default delays);
/// saving creates it. Lines this editor does not know are kept as they are.
/// </remarks>
public static partial class CoreBotsOptions
{
    public const string CoreBotsFile = "CoreBots.cs";

    [GeneratedRegex("""CBO(Bool|Int|String)\("([A-Za-z0-9_]+)"\s*,""")]
    private static partial Regex KeyPattern();

    // AQW account names: letters, digits, spaces, underscores and hyphens. CoreBots names the file after
    // Username() (sfc.myUserName, the lower-case account name), so every real name is still matched.
    private const string AccountChars = "[A-Za-z0-9 _-]{1,64}";

    [GeneratedRegex("^" + AccountChars + @"\z")]
    private static partial Regex AccountPattern();

    [GeneratedRegex(@"^CBO_Storage\((.*)\)\z", RegexOptions.Singleline)]
    private static partial Regex StoragePattern();

    /// <summary>
    /// Whether <paramref name="account"/> may name a settings file. It comes from the game server (the character
    /// name), so anything outside the account name characters (a path separator, a parenthesis, "..") is refused.
    /// </summary>
    public static bool IsSafeAccountName(string? account) =>
        account is not null && AccountPattern().IsMatch(account) && account.Trim().Length > 0;

    /// <summary>The CoreBots sources the keys are read from (the Core*.cs files at the library root).</summary>
    public static IEnumerable<string> Sources(string libraryRoot) =>
        Directory.Exists(libraryRoot)
            ? Directory.EnumerateFiles(libraryRoot, "Core*.cs", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)
            : [];

    public static bool Available(string libraryRoot) => File.Exists(Path.Combine(libraryRoot, CoreBotsFile));

    /// <summary>The settings CoreBots reads, in first-use order, with their kind.</summary>
    public static IReadOnlyList<(string Key, ScriptOptionKind Kind)> ReadKeys(string libraryRoot)
    {
        List<(string, ScriptOptionKind)> keys = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string file in Sources(libraryRoot))
        {
            foreach (Match m in KeyPattern().Matches(File.ReadAllText(file)))
            {
                if (!seen.Add(m.Groups[2].Value))
                    continue;
                ScriptOptionKind kind = m.Groups[1].Value switch
                {
                    "Bool" => ScriptOptionKind.Toggle,
                    "Int" => ScriptOptionKind.Number,
                    _ => ScriptOptionKind.Text,
                };
                keys.Add((m.Groups[2].Value, kind));
            }
        }
        return keys;
    }

    /// <summary>
    /// The settings file of <paramref name="account"/>: an existing one whose name matches ignoring case (the game
    /// reports the name in its own casing), else <c>CBO_Storage(&lt;account in lower case&gt;).txt</c> as AQW reports it.
    /// </summary>
    /// <exception cref="EngineException">The account name is not one a settings file can be named after.</exception>
    public static string FileFor(string optionsDir, string account)
    {
        if (!IsSafeAccountName(account))
            throw new EngineException("CoreBots options are not available for this account name.");
        string expected = $"CBO_Storage({account}).txt";
        if (Directory.Exists(optionsDir))
        {
            string? existing = Directory.EnumerateFiles(optionsDir, "CBO_Storage(*).txt")
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), expected, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return Inside(optionsDir, Path.GetFileName(existing));
        }
        return Inside(optionsDir, $"CBO_Storage({account.ToLowerInvariant()}).txt");
    }

    /// <summary>The full path of <paramref name="fileName"/> directly in <paramref name="optionsDir"/>, or an error.</summary>
    private static string Inside(string optionsDir, string fileName)
    {
        string dir = Path.GetFullPath(optionsDir);
        string full = Path.GetFullPath(Path.Combine(dir, fileName));
        if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(dir), StringComparison.Ordinal))
            throw new EngineException("CoreBots options are not available for this account name.");
        return full;
    }

    public static ScriptOptionSet Load(string libraryRoot, string optionsDir, string account)
    {
        string file = FileFor(optionsDir, account);
        Dictionary<string, string> values = ReadValues(file);
        List<ScriptOptionItem> items = ReadKeys(libraryRoot)
            .Select(k => new ScriptOptionItem(
                Category(k.Key),
                k.Key,
                Spaced(k.Key),
                string.Empty,
                k.Kind,
                values.TryGetValue(k.Key, out string? v) ? v : k.Kind == ScriptOptionKind.Toggle ? "False" : string.Empty,
                [],
                false))
            .ToList();
        return new ScriptOptionSet(Path.Combine(libraryRoot, CoreBotsFile), Path.GetFileNameWithoutExtension(file), items);
    }

    /// <summary>Writes the edited values; unknown lines are kept, unset values are left out (CoreBots' default).</summary>
    /// <exception cref="EngineException">A number is not a whole number.</exception>
    public static void Save(ScriptOptionSet set, string optionsDir)
    {
        if (StoragePattern().Match(set.Storage) is not { Success: true } storage || !IsSafeAccountName(storage.Groups[1].Value))
            throw new EngineException("Not saved: the CoreBots options file name is not valid.");
        string file = Inside(optionsDir, set.Storage + ".txt");
        List<string> problems = new();
        foreach (ScriptOptionItem i in set.Items)
        {
            if (i.Kind == ScriptOptionKind.Number && i.Value.Trim().Length > 0 && !int.TryParse(i.Value.Trim(), out _))
                problems.Add($"{i.Title}: \"{i.Value}\" is not a whole number");
        }
        if (problems.Count > 0)
            throw new EngineException("Not saved. " + string.Join("; ", problems));

        Dictionary<string, string> previous = ReadValues(file);
        Dictionary<string, ScriptOptionItem> edited = set.Items.ToDictionary(i => i.Name, StringComparer.Ordinal);
        List<string> lines = new();
        HashSet<string> written = new(StringComparer.Ordinal);
        if (File.Exists(file))
        {
            foreach (string line in File.ReadAllLines(file))
            {
                string key = KeyOf(line);
                if (!edited.TryGetValue(key, out ScriptOptionItem? item))
                {
                    lines.Add(line);
                    continue;
                }
                written.Add(key);
                if (Keep(item, previous))
                    lines.Add($"{key}: {item.Value.Trim()}");
            }
        }
        foreach (ScriptOptionItem item in set.Items)
        {
            if (!written.Contains(item.Name) && Keep(item, previous))
                lines.Add($"{item.Name}: {item.Value.Trim()}");
        }
        Directory.CreateDirectory(optionsDir);
        string temp = file + ".tmp";
        File.WriteAllLines(temp, lines);
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>The lines of <paramref name="account"/>'s settings file (none when it does not exist).</summary>
    /// <exception cref="EngineException">The account name is not one a settings file can be named after.</exception>
    public static IReadOnlyList<string> ReadLines(string optionsDir, string account)
    {
        string file = FileFor(optionsDir, account);
        return File.Exists(file) ? File.ReadAllLines(file) : [];
    }

    /// <summary>
    /// Key and value of each line as CoreBots reads them (<c>Key: Value</c>; the first line of a key wins), values
    /// trimmed as the desktop window reads them.
    /// </summary>
    public static Dictionary<string, string> Values(IEnumerable<string> lines)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
                values.TryAdd(line[..colon].Trim(), line[(colon + 2)..].Trim());
        }
        return values;
    }

    /// <summary>The key of a settings line.</summary>
    public static string KeyOfLine(string line) => KeyOf(line);

    /// <summary>Replaces <paramref name="account"/>'s settings file with <paramref name="lines"/> (atomically).</summary>
    /// <exception cref="EngineException">The account name is not one a settings file can be named after.</exception>
    public static void WriteLines(string optionsDir, string account, IEnumerable<string> lines)
    {
        string file = FileFor(optionsDir, account);
        Directory.CreateDirectory(optionsDir);
        string temp = file + ".tmp";
        File.WriteAllLines(temp, lines);
        File.Move(temp, file, overwrite: true);
    }

    private static bool Keep(ScriptOptionItem item, Dictionary<string, string> previous) =>
        item.Kind == ScriptOptionKind.Toggle
            ? item.Value == "True" || previous.ContainsKey(item.Name)
            : item.Value.Trim().Length > 0;

    // CoreBots matches with StartsWith(key) and splits on ": ".
    private static string KeyOf(string line)
    {
        int colon = line.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? line.Trim() : line[..colon].Trim();
    }

    private static Dictionary<string, string> ReadValues(string file)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        if (!File.Exists(file))
            return values;
        foreach (string line in File.ReadAllLines(file))
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
                values.TryAdd(line[..colon].Trim(), line[(colon + 2)..]);
        }
        return values;
    }

    private static string Category(string key) =>
        key.Contains("Solo", StringComparison.Ordinal) ? "Solo"
        : key.Contains("Farm", StringComparison.Ordinal) ? "Farm"
        : key.Contains("Dodge", StringComparison.Ordinal) || key.EndsWith("3Select", StringComparison.Ordinal) ? "Dodge"
        : key.Contains("Boss", StringComparison.Ordinal) || key.EndsWith("4Select", StringComparison.Ordinal) ? "Boss"
        : key.EndsWith("1Select", StringComparison.Ordinal) ? "Solo"
        : key.EndsWith("2Select", StringComparison.Ordinal) ? "Farm"
        : "General";

    private static string Spaced(string key) => Regex.Replace(key, "(?<=[a-z0-9])(?=[A-Z])", " ");
}
