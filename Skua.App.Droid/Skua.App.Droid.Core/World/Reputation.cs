using System.Globalization;
using Skua.Core.Interfaces;
using Skua.Core.Models.Factions;

namespace Skua.App.Droid.Core.World;

/// <summary>
/// One faction of the Reputation list: Skua's <see cref="Faction"/> (what <see cref="IScriptFaction.FactionList"/>
/// returns) with the numbers the row shows.
/// </summary>
/// <param name="Id">Faction ID.</param>
/// <param name="Name">In-game name.</param>
/// <param name="Rank">1 to 10.</param>
/// <param name="Rep">Reputation earned inside the current rank.</param>
/// <param name="RequiredRep">Reputation the current rank spans (0 at rank 10).</param>
/// <param name="RemainingRep">Reputation still needed for the next rank.</param>
/// <param name="TotalRep">All reputation earned in this faction.</param>
public sealed record FactionRow(int Id, string Name, int Rank, int Rep, int RequiredRep, int RemainingRep, int TotalRep)
{
    /// <summary>The game's highest rank.</summary>
    public const int MaxRank = 10;

    public bool IsMax => Rank >= MaxRank;

    /// <summary>Progress to the next rank, 0 to 1 (1 at the highest rank).</summary>
    public double Progress => IsMax ? 1 : RequiredRep <= 0 ? 0 : Math.Clamp((double)Rep / RequiredRep, 0, 1);

    /// <summary>The same in percent, for the progress bar.</summary>
    public double ProgressPercent => Progress * 100;

    public string RankText => IsMax ? "Rank 10 · MAX" : $"Rank {Rank}";

    /// <summary>"1,200 / 2,700 (1,500 to rank 4)", or "MAX" at rank 10.</summary>
    public string ProgressText => IsMax
        ? "MAX"
        : string.Format(CultureInfo.InvariantCulture, "{0:N0} / {1:N0} ({2:N0} to rank {3})", Rep, RequiredRep, Math.Max(0, RemainingRep), Rank + 1);

    public string TotalText => string.Format(CultureInfo.InvariantCulture, "#{0} · total {1:N0}", Id, TotalRep);

    public static FactionRow From(Faction f) =>
        new(f.ID, f.Name ?? string.Empty, f.Rank, f.Rep, f.RequiredRep, f.RemainingRep, f.TotalRep);
}

/// <summary>The Reputation list: every faction of the character, read through Skua's <see cref="IScriptFaction"/>.</summary>
public static class ReputationList
{
    /// <summary>
    /// The factions of Skua's <see cref="IScriptFaction.FactionList"/> on <paramref name="skua"/> (the engine's Skua
    /// services), sorted. Null when there is no Skua host. Reads the game: call it off the UI thread.
    /// </summary>
    public static IReadOnlyList<FactionRow>? Read(IServiceProvider? skua)
    {
        if (skua?.GetService(typeof(IScriptFaction)) is not IScriptFaction factions)
            return null;
        List<Faction>? list = factions.FactionList;
        return Sort((list ?? []).Where(f => f is not null).Select(FactionRow.From));
    }

    /// <summary>Highest rank first, then the most reputation, then by name.</summary>
    public static IReadOnlyList<FactionRow> Sort(IEnumerable<FactionRow> rows) =>
        rows.OrderByDescending(r => r.Rank)
            .ThenByDescending(r => r.TotalRep)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Rows whose name contains <paramref name="query"/> (any case) or whose ID is <paramref name="query"/>.</summary>
    public static IReadOnlyList<FactionRow> Filter(IReadOnlyList<FactionRow> rows, string? query)
    {
        string q = query?.Trim() ?? string.Empty;
        if (q.Length == 0)
            return rows;
        return rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Id.ToString(CultureInfo.InvariantCulture) == q).ToList();
    }
}
