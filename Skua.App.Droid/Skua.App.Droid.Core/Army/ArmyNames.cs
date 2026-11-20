using System.Text.RegularExpressions;
using Skua.App.Droid.Core.Engine;

namespace Skua.App.Droid.Core.Army;

/// <summary>
/// The checks every name typed for an army command passes before anything is sent (the Control page's rules: map
/// names with an optional room number, cell and pad names, player names).
/// </summary>
public static partial class ArmyNames
{
    public static bool IsMap(string? text) => text is not null && MapName().IsMatch(text);

    public static bool IsCell(string? text) => text is not null && CellName().IsMatch(text);

    public static bool IsPlayer(string? text) => text is not null && PlayerName().IsMatch(text);

    /// <summary>Skua option storage names (<c>Queue_&lt;script&gt;_&lt;id&gt;</c>, a script's own): a file name in the options folder.</summary>
    public static bool IsOptionStorage(string? text) => text is not null && StorageName().IsMatch(text);

    public static string RequireMap(string text) => IsMap(text) ? text : throw new EngineException($"\"{text}\" is not a map name.");

    public static string RequireCell(string text) => IsCell(text) ? text : throw new EngineException($"\"{text}\" is not a cell or pad name.");

    public static string RequirePlayer(string text) => IsPlayer(text) ? text.Trim() : throw new EngineException($"\"{text}\" is not a player name.");

    public static string RequireOptionStorage(string text) => IsOptionStorage(text) ? text : throw new EngineException("Invalid option profile name.");

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,40}(-[A-Za-z0-9]{1,12})?$")]
    private static partial Regex MapName();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]{1,40}$")]
    private static partial Regex CellName();

    [GeneratedRegex(@"^[A-Za-z0-9_\- ]{1,32}$")]
    private static partial Regex PlayerName();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.\(\) ]{1,120}$")]
    private static partial Regex StorageName();
}
