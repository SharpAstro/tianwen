using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Planner-side "smart framing" adapter over the pure <see cref="FramingGrouper"/>: turns the user's
/// pinned targets into <see cref="FramingGroup"/>s (discovering catalogued neighbours that share the
/// same sensor frame, e.g. M8 + M20) and collapses co-framed proposals into a single scheduled
/// pointing. Lives in TianWen.Lib -- pure astro/framing geometry, no UI -- so the catalog shape math
/// (internal <see cref="MosaicGenerator.ComputeRotatedEllipseBBox"/>) stays accessible.
/// </summary>
public static class FramingPlanner
{
    /// <summary>Neighbours fainter than this (Johnson V) aren't worth reframing for -- keeps discovery
    /// from dragging anonymous faint galaxies into a pointing.</summary>
    public const double DefaultNeighbourMagLimit = 12.0;

    /// <summary>Hard cap on discovered neighbours considered per seed (brightest first).</summary>
    public const int DefaultMaxNeighboursPerSeed = 16;

    /// <summary>
    /// Builds framing groups from <paramref name="seeds"/> (the pinned targets) plus catalogued
    /// deep-sky neighbours found in the DB's spatial grid, using the sensor <paramref name="fov"/>.
    /// <para>
    /// Neighbour discovery is <b>grid-local</b>: it samples only the <see cref="ICelestialObjectDB.DeepSkyCoordinateGrid"/>
    /// cells covering each seed's FOV footprint (a handful of 1-degree cells), never a full-catalog
    /// scan -- so cost stays bounded by the seed count, not the catalog size. The grouping itself is
    /// the non-quadratic <see cref="FramingGrouper.Group"/>.
    /// </para>
    /// </summary>
    public static ImmutableArray<FramingGroup> BuildGroups(
        ICelestialObjectDB db,
        (double WidthDeg, double HeightDeg) fov,
        ReadOnlySpan<Target> seeds,
        FramingOptions? options = null,
        double neighbourMagLimit = DefaultNeighbourMagLimit,
        int maxNeighboursPerSeed = DefaultMaxNeighboursPerSeed)
    {
        if (seeds.Length == 0 || fov.WidthDeg <= 0 || fov.HeightDeg <= 0)
        {
            return [];
        }

        var candidates = new List<FramingCandidate>(seeds.Length * 2);
        // Index-based identity, same as the planner: an object's aliases (M 20 / NGC 6514 / ...) are
        // collapsed via the DB's cross-indices (ObservationScheduler.MarkCrossIndicesSeen), so each
        // physical object is accounted exactly once -- for seeds and discovered neighbours alike.
        var seen = new HashSet<CatalogIndex>();

        // Seeds first, so they anchor groups (a discovered neighbour never seeds its own).
        foreach (var t in seeds)
        {
            var (hw, hh) = HalfExtentsDeg(db, t.CatalogIndex);
            candidates.Add(new FramingCandidate(t.RA, t.Dec, hw, hh, t.Name, t.CatalogIndex, VMagOf(db, t.CatalogIndex), IsSeed: true));
            if (t.CatalogIndex is { } si)
            {
                seen.Add(si);
                ObservationScheduler.MarkCrossIndicesSeen(db, si, seen);
            }
        }

        // Grid-local neighbour discovery around each seed.
        var grid = db.DeepSkyCoordinateGrid;
        foreach (var t in seeds)
        {
            DiscoverNeighbours(db, grid, fov, t, seen, candidates, neighbourMagLimit, maxNeighboursPerSeed);
        }

        return FramingGrouper.Group(CollectionsMarshal.AsSpan(candidates), fov.WidthDeg, fov.HeightDeg, options);
    }

    /// <summary>
    /// Collapses proposals that share a multi-target <see cref="FramingGroup"/> into one representative
    /// observation at the group centroid (named e.g. "M8 + M20"), preserving the first member proposal's
    /// imaging parameters (gain/offset/priority/exposure/window). Ungrouped proposals pass through
    /// unchanged; a null/empty group set is an identity transform. Pure -- no DB, no allocation beyond
    /// the result.
    /// </summary>
    public static ImmutableArray<ProposedObservation> CollapseForSchedule(
        ReadOnlySpan<ProposedObservation> proposals,
        ImmutableArray<FramingGroup> groups)
    {
        if (groups.IsDefaultOrEmpty || proposals.Length == 0)
        {
            return proposals.ToImmutableArray();
        }

        var emitted = new bool[groups.Length];
        var result = ImmutableArray.CreateBuilder<ProposedObservation>(proposals.Length);
        foreach (var p in proposals)
        {
            var gi = FindGroupForProposal(groups, p);
            if (gi < 0)
            {
                result.Add(p);
                continue;
            }
            if (emitted[gi])
            {
                continue; // a co-framed sibling already emitted the group's single pointing
            }
            emitted[gi] = true;
            var g = groups[gi];
            // Keep the seed proposal's imaging params; only the pointing + name become the group's.
            result.Add(p with { Target = new Target(g.CenterRA, g.CenterDec, g.Name, p.Target.CatalogIndex) });
        }
        return result.ToImmutable();
    }

    // Index of the first multi-target group whose SEED members include this proposal, or -1.
    private static int FindGroupForProposal(ImmutableArray<FramingGroup> groups, ProposedObservation p)
    {
        for (var gi = 0; gi < groups.Length; gi++)
        {
            if (!groups[gi].IsMultiTarget)
            {
                continue;
            }
            foreach (var m in groups[gi].Members)
            {
                if (!m.IsSeed)
                {
                    continue;
                }
                bool match = m.Index is { } mi && p.Target.CatalogIndex is { } pi
                    ? mi == pi
                    : string.Equals(m.Name, p.Target.Name, StringComparison.Ordinal);
                if (match)
                {
                    return gi;
                }
            }
        }
        return -1;
    }

    // Samples the FOV-footprint grid cells around the seed, collecting catalogued DSO neighbours
    // (bright enough, not already seen), brightest-first up to the per-seed cap.
    private static void DiscoverNeighbours(
        ICelestialObjectDB db, IRaDecIndex grid, (double WidthDeg, double HeightDeg) fov, Target seed,
        HashSet<CatalogIndex> seen, List<FramingCandidate> candidates,
        double magLimit, int maxPerSeed)
    {
        var cosDec = Math.Max(1e-4, Math.Cos(seed.Dec * Math.PI / 180.0));
        // Footprint half-extents, padded ~0.5 deg so an object whose centre sits just outside the frame
        // but whose disc reaches in still surfaces.
        var halfDec = fov.HeightDeg * 0.5 + 0.5;                  // degrees
        var halfRaHours = (fov.WidthDeg * 0.5 + 0.5) / cosDec / 15.0; // hours

        // Grid cells are 1 deg x 1 deg; step ~0.9 deg so none is skipped.
        const double stepDeg = 0.9;
        var stepRaHours = stepDeg / 15.0;

        var local = new List<(CatalogIndex Idx, double SortKey, double VMag, double RA, double Dec, string Name)>();
        for (var dDec = -halfDec; dDec <= halfDec + 1e-9; dDec += stepDeg)
        {
            var dec = Math.Clamp(seed.Dec + dDec, -89.999, 89.999);
            for (var dRa = -halfRaHours; dRa <= halfRaHours + 1e-9; dRa += stepRaHours)
            {
                var ra = ((seed.RA + dRa) % 24.0 + 24.0) % 24.0;
                foreach (var idx in grid[ra, dec])
                {
                    if (!seen.Add(idx))
                    {
                        continue; // a seed or an already-collected neighbour
                    }
                    if (!db.TryLookupByIndex(idx, out var obj))
                    {
                        continue;
                    }

                    // Auto-discovered companions are limited to NAMED deep-sky objects (the Trifid, the
                    // Lagoon, ...): notable enough to reframe for, and it drops field stars, uncertain
                    // candidates, duplicate entries, and the anonymous survey/dark-nebula swarm in one
                    // stroke. (Explicitly pinned seeds still group regardless of name.) The DSO grid holds
                    // bright HD/HIP stars -- it only excludes Tycho-2 -- so IsStar is load-bearing here.
                    if (obj.ObjectType.IsStar || obj.ObjectType.IsCandidate || obj.ObjectType == ObjectType.Duplicate)
                    {
                        continue;
                    }
                    if (obj.CommonNames.Count == 0)
                    {
                        continue;
                    }

                    // Rank brightest-first; a no-magnitude nebula sorts as if moderately bright so it isn't
                    // crowded out when the per-seed cap bites. magLimit is only an upper bound for objects
                    // that DO carry a magnitude -- a named nebula with no listed V is kept regardless.
                    var vmag = Half.IsNaN(obj.V_Mag) ? double.NaN : (double)obj.V_Mag;
                    if (!double.IsNaN(vmag) && vmag > magLimit)
                    {
                        continue;
                    }

                    // Accepted: mark its aliases seen so the same object under another designation
                    // (M 20 / NGC 6514, Sh2-25 via its "M 8" identifier) isn't collected twice --
                    // same pattern as the planner sweep. Alias completeness is the DB's job: the
                    // SIMBAD merge resolves alias-only identifiers through the cross-index table
                    // (ResolveToDirectIndex), so index identity is authoritative here.
                    ObservationScheduler.MarkCrossIndicesSeen(db, idx, seen);

                    var sortKey = !double.IsNaN(vmag) ? vmag : 8.0;
                    local.Add((idx, sortKey, vmag, obj.RA, obj.Dec, obj.DisplayName));
                }
            }
        }

        local.Sort((a, b) => a.SortKey.CompareTo(b.SortKey)); // most notable / brightest first
        var take = Math.Min(local.Count, maxPerSeed);
        for (var k = 0; k < take; k++)
        {
            var n = local[k];
            var (hw, hh) = HalfExtentsDeg(db, n.Idx);
            candidates.Add(new FramingCandidate(n.RA, n.Dec, hw, hh, n.Name, n.Idx, n.VMag, IsSeed: false));
        }
    }

    // On-sky half-extents (degrees) of a catalog object's shape bounding box, or (0,0) point-like.
    private static (double HalfWidthDeg, double HalfHeightDeg) HalfExtentsDeg(ICelestialObjectDB db, CatalogIndex? index)
    {
        if (index is { } idx && db.TryGetShape(idx, out var shape))
        {
            var major = (double)shape.MajorAxis;
            if (!double.IsNaN(major) && major > 0)
            {
                var minorRaw = (double)shape.MinorAxis;
                var minor = double.IsNaN(minorRaw) || minorRaw <= 0 ? major : minorRaw;
                var pa = Half.IsNaN(shape.PositionAngle) ? 0.0 : (double)shape.PositionAngle;
                var (w, h) = MosaicGenerator.ComputeRotatedEllipseBBox(major / 60.0, minor / 60.0, pa);
                return (w * 0.5, h * 0.5);
            }
        }
        return (0.0, 0.0);
    }

    private static double VMagOf(ICelestialObjectDB db, CatalogIndex? index)
        => index is { } idx && db.TryLookupByIndex(idx, out var obj) && !Half.IsNaN(obj.V_Mag)
            ? (double)obj.V_Mag
            : double.NaN;
}
