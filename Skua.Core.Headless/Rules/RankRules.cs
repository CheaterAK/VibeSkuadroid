namespace Skua.Core.Headless.Rules;

/// <summary>A rank and where the points stand inside it.</summary>
/// <param name="Rank">1 to 10.</param>
/// <param name="PointsInRank">Points earned inside the current rank (<c>iCurCP</c> / <c>iSpillRep</c>).</param>
/// <param name="PointsToNextRank">Points the current rank spans (<c>iCPToRank</c> / <c>iRepToRank</c>); 0 at rank 10.</param>
public readonly record struct RankProgress(int Rank, long PointsInRank, long PointsToNextRank);

/// <summary>
/// Class rank from class points and faction rank from reputation. Both use the game's one table
/// (<c>Game.initArrRep</c>): the points at which rank n+1 starts are <c>sum((i+1)^3 * 100)</c> plus 100 for rank 2,
/// so ranks start at 0, 900, 3 600, 10 000 ... and rank 10 at 302 500.
/// </summary>
/// <remarks>Ported from Seraph app/lib/skua/swfworld.js (RANK_POINTS, rankFromPoints, updateRep, initFaction), MIT.</remarks>
public static class RankRules
{
    /// <summary>The highest rank.</summary>
    public const int MaxRank = 10;

    private static readonly long[] s_points = BuildTable();

    /// <summary>Points at which each rank starts: index 0 is rank 1 (0 points), index 9 is rank 10.</summary>
    public static IReadOnlyList<long> RankStarts => s_points;

    private static long[] BuildTable()
    {
        long[] r = new long[MaxRank];
        r[0] = 0;
        for (int i = 1; i < MaxRank; i++)
        {
            long p = (long)Math.Pow(i + 1, 3) * 100;
            r[i] = i > 1 ? p + r[i - 1] : p + 100;
        }
        return r;
    }

    /// <summary>The rank reached with <paramref name="points"/> (negative counts as 0).</summary>
    public static int RankFromPoints(long points)
    {
        long p = Math.Max(0, points);
        int rank = 1;
        for (int i = 1; i < s_points.Length; i++)
        {
            if (p < s_points[i])
                return rank;
            rank++;
        }
        return rank;
    }

    /// <summary>Rank and progress for class points (<c>objData.iCP</c>) or faction reputation (<c>iRep</c>).</summary>
    public static RankProgress Progress(long points)
    {
        long p = Math.Max(0, points);
        int rank = RankFromPoints(p);
        long start = s_points[rank - 1];
        long span = rank < MaxRank ? s_points[rank] - start : 0;
        return new RankProgress(rank, p - start, span);
    }
}
