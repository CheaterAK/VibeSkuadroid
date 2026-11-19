namespace Skua.Core.Models;

/// <summary>
/// A fast travel destination as Skua saves it in the "FastTravels" setting: one line "name,map,cell,pad". The desktop
/// Fast Travel window and the Android app's Fast Travel page read, write, check and search destinations through this.
/// </summary>
/// <param name="Name">What the list shows ("Nulgath").</param>
/// <param name="Map">The map to join ("tercessuinotlim").</param>
/// <param name="Cell">The cell to land in.</param>
/// <param name="Pad">The pad to land on.</param>
public sealed record FastTravelEntry(string Name, string Map, string Cell, string Pad)
{
    /// <summary>Reads a saved line; lines with fewer than four comma separated fields are skipped (extra fields are ignored).</summary>
    public static bool TryParse(string? line, out FastTravelEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(line))
            return false;
        string[] values = line.Split(',');
        if (values.Length < 4)
            return false;
        entry = new FastTravelEntry(values[0], values[1], values[2], values[3]);
        return true;
    }

    /// <summary>Every field is filled in (the desktop editor's check before adding).</summary>
    public static bool IsComplete(string? name, string? map, string? cell, string? pad) =>
        !string.IsNullOrWhiteSpace(cell)
        && !string.IsNullOrWhiteSpace(pad)
        && !string.IsNullOrWhiteSpace(map)
        && !string.IsNullOrWhiteSpace(name);

    /// <summary>The search of the destination list: name, map or cell contains the filter (blank matches all).</summary>
    public static bool Matches(string name, string map, string cell, string? filter)
    {
        string f = filter?.Trim() ?? string.Empty;
        return f.Length == 0
            || name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || map.Contains(f, StringComparison.OrdinalIgnoreCase)
            || cell.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The saved line.</summary>
    public static string Format(string name, string map, string cell, string pad) => $"{name},{map},{cell},{pad}";

    /// <summary>The saved line ("name,map,cell,pad").</summary>
    public override string ToString() => Format(Name, Map, Cell, Pad);
}
