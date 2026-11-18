using System.Collections.Concurrent;
using System.Text;

namespace Skua.Core.Headless.Flash;

/// <summary>Why a bridge call has no faithful answer.</summary>
public enum FlashGapKind
{
    /// <summary>Not implemented: the Flash client would answer, the headless bridge does not (yet).</summary>
    Missing,

    /// <summary>Cannot exist without Flash (loading SWF code, reflection outside the model); refused on purpose.</summary>
    Impossible,

    /// <summary>Answered with a reasoned approximation that only the real client could confirm.</summary>
    Approximated,
}

/// <summary>One recorded gap.</summary>
/// <param name="Kind">Missing, impossible or approximated.</param>
/// <param name="Name">The callback (<c>attackMonsterID</c>), game function (<c>fn:world.foo</c>) or path (<c>path:ui.foo.bar</c>).</param>
/// <param name="ArgsShape">Argument types, for example <c>(string,int)</c>.</param>
/// <param name="Count">Times seen.</param>
/// <param name="FirstSeen">When it was first seen.</param>
/// <param name="Detail">What was answered instead.</param>
public sealed record FlashGap(FlashGapKind Kind, string Name, string ArgsShape, long Count, DateTimeOffset FirstSeen, string Detail);

/// <summary>
/// Every bridge call without a faithful answer, counted by name and argument shape. Nothing unimplemented is
/// answered silently: it lands here (and, the first time, in the log) and gets the Flash bridge's "no value" answer.
/// </summary>
public sealed class FlashGapLog
{
    private readonly ConcurrentDictionary<(FlashGapKind, string, string), Entry> _entries = new();

    /// <summary>Raised the first time a gap is seen.</summary>
    public event Action<FlashGap>? NewGap;

    public void Record(FlashGapKind kind, string name, object?[]? args, string detail)
    {
        string shape = Shape(args);
        bool added = false;
        Entry entry = _entries.GetOrAdd((kind, name, shape), _ =>
        {
            added = true;
            return new Entry(detail, DateTimeOffset.UtcNow);
        });
        Interlocked.Increment(ref entry.Count);
        if (added)
            NewGap?.Invoke(new FlashGap(kind, name, shape, 1, entry.FirstSeen, detail));
    }

    /// <summary>All gaps, most frequent first.</summary>
    public IReadOnlyList<FlashGap> Snapshot()
    {
        return _entries
            .Select(p => new FlashGap(p.Key.Item1, p.Key.Item2, p.Key.Item3, Interlocked.Read(ref p.Value.Count), p.Value.FirstSeen, p.Value.Detail))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();
    }

    public void Clear() => _entries.Clear();

    /// <summary>A plain-text table.</summary>
    public string Format()
    {
        IReadOnlyList<FlashGap> gaps = Snapshot();
        if (gaps.Count == 0)
            return "Flash gap log: empty (every call had an answer).";
        StringBuilder sb = new();
        sb.Append("Flash gap log: ").Append(gaps.Count).AppendLine(" distinct gaps");
        foreach (FlashGap g in gaps)
            sb.Append("  ").Append(g.Kind.ToString().PadRight(12)).Append(g.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(7)).Append("  ").Append(g.Name).Append(g.ArgsShape).Append("  -- ").AppendLine(g.Detail);
        return sb.ToString();
    }

    private static string Shape(object?[]? args)
    {
        if (args is null || args.Length == 0)
            return "()";
        return "(" + string.Join(',', args.Select(a => a switch
        {
            null => "null",
            string => "string",
            bool => "bool",
            int or long or short or byte => "int",
            float or double or decimal => "number",
            System.Dynamic.ExpandoObject => "object",
            Array => "array",
            _ => a.GetType().Name,
        })) + ")";
    }

    private sealed class Entry
    {
        public long Count;

        public Entry(string detail, DateTimeOffset firstSeen)
        {
            Detail = detail;
            FirstSeen = firstSeen;
        }

        public string Detail { get; }

        public DateTimeOffset FirstSeen { get; }
    }
}
