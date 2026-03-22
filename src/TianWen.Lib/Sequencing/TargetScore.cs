using System;
using System.Collections.Generic;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public readonly record struct ScoredTarget(
    Target Target,
    Half TotalScore,
    Half ObjectBonus,
    IReadOnlyDictionary<RaDecEventTime, RaDecEventInfo> ElevationProfile,
    DateTimeOffset OptimalStart,
    TimeSpan OptimalDuration,
    double OptimalAltitude = 0,
    ObjectType ObjectType = ObjectType.Unknown
) : IComparable<ScoredTarget>
{
    /// <summary>
    /// Combined score used for ranking: altitude score × object desirability bonus.
    /// </summary>
    public readonly double CombinedScore => (double)TotalScore * (double)ObjectBonus;

    /// <summary>
    /// Compares by <see cref="CombinedScore"/> descending (higher = better).
    /// Ties are broken by <see cref="Target.CatalogIndex"/> to ensure deterministic ordering.
    /// </summary>
    public readonly int CompareTo(ScoredTarget other)
    {
        var cmp = other.CombinedScore.CompareTo(CombinedScore); // descending
        if (cmp != 0) return cmp;

        // Break ties deterministically by catalog index
        var thisIdx = Target.CatalogIndex ?? 0;
        var otherIdx = other.Target.CatalogIndex ?? 0;
        return thisIdx.CompareTo(otherIdx);
    }
}
