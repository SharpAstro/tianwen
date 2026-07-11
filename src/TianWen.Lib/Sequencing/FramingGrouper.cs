using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Groups sky targets that fall within a single sensor frame ("smart framing"): given the seed
/// targets the user chose plus candidate neighbours discovered from the catalog, and the sensor FOV,
/// it partitions them into <see cref="FramingGroup"/>s where every member shares one pointing. The
/// classic case is M8 (Lagoon) + M20 (Trifid) ~1.4 deg apart, which a wide field captures together.
///
/// <para>Pure and DB-free: it works on <see cref="FramingCandidate"/> records the caller prepared
/// (positions + on-sky half-extents), so the geometry is unit-testable without a catalog stub. The
/// planner adapter builds candidates from the real object DB + spatial grid and maps groups onto
/// scheduled observations.</para>
///
/// <para>Fit test uses a local tangent plane at the group's seed: east offset
/// <c>dRA_hours * 15 * cos(Dec_seed)</c>, north offset <c>dDec</c>, both in true sky degrees, so the
/// combined footprint bounding box can be compared directly against the FOV rectangle (camera
/// unrotated / RA-Dec aligned, matching MosaicGenerator's panel convention). Accurate to well under a
/// pixel for the few-degree fields this targets.</para>
///
/// <para><b>Complexity:</b> not quadratic in the candidate count. Candidates are Dec-sorted once
/// (<c>O(n log n)</c>); each seed then binary-searches the <c>|dDec| &lt;= fovHeight</c> band (a
/// necessary condition for sharing a frame) and only scans that slice, with an O(1) RA pre-gate before
/// the fit test. Cost is <c>O(n log n + seeds * band * MaxMembers^2)</c> with <c>MaxMembers</c> a small
/// constant -- effectively linear for the Dec-spread inputs this sees. The caller must also keep
/// <c>n</c> itself small by discovering neighbours through the catalog's spatial grid (FOV-footprint
/// cells only), never a full-catalog scan.</para>
/// </summary>
public static class FramingGrouper
{
    /// <summary>
    /// Partition <paramref name="candidates"/> into co-framable groups. Only entries with
    /// <see cref="FramingCandidate.IsSeed"/> anchor a group; non-seed neighbours may attach to a seed's
    /// frame but never start one of their own (and are dropped if they fit no seed). Greedy: each seed
    /// repeatedly accretes the nearest not-yet-assigned candidate that keeps the WHOLE group inside one
    /// frame, up to <see cref="FramingOptions.MaxMembers"/>. Co-framable seeds merge into one group.
    /// </summary>
    /// <param name="candidates">Seeds (pinned targets) plus discovered neighbours.</param>
    /// <param name="fovWidthDeg">Sensor FOV width in degrees (see <see cref="MosaicGenerator.ComputeFieldOfView"/>).</param>
    /// <param name="fovHeightDeg">Sensor FOV height in degrees.</param>
    /// <param name="options">Tunables; defaults applied when null.</param>
    /// <returns>Groups in seed order (RA ascending). Members within a group are ordered by RA.</returns>
    public static ImmutableArray<FramingGroup> Group(
        ReadOnlySpan<FramingCandidate> candidates,
        double fovWidthDeg,
        double fovHeightDeg,
        FramingOptions? options = null)
    {
        options ??= new FramingOptions();
        if (candidates.Length == 0 || fovWidthDeg <= 0 || fovHeightDeg <= 0)
        {
            return [];
        }

        var effW = fovWidthDeg * (1 - options.MarginFraction);
        var effH = fovHeightDeg * (1 - options.MarginFraction);
        if (effW <= 0 || effH <= 0)
        {
            return [];
        }

        // Copy once to a heap array so the grouping (sorts, index tracking, LINQ) is free of the
        // ref-struct span restrictions. N is small here (pinned targets + a few grid-local neighbours),
        // and this is a planner-time call, not a per-frame path -- the copy is immaterial.
        var cand = candidates.ToArray();
        var n = cand.Length;
        var assigned = new bool[n];

        // Dec-sorted index order: lets each seed scan only the candidates whose Dec is within one FOV
        // height (a necessary condition for sharing a frame), via binary search -- so grouping is NOT
        // O(seeds * n).
        var order = new int[n];
        for (var i = 0; i < n; i++)
        {
            order[i] = i;
        }
        Array.Sort(order, (a, b) => cand[a].Dec.CompareTo(cand[b].Dec));

        // Seeds anchor groups, visited in deterministic (RA, Dec, Name) order so grouping is stable.
        var seedIdx = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (cand[i].IsSeed)
            {
                seedIdx.Add(i);
            }
        }
        seedIdx.Sort((a, b) =>
        {
            var c = cand[a].RA.CompareTo(cand[b].RA);
            if (c != 0) return c;
            c = cand[a].Dec.CompareTo(cand[b].Dec);
            if (c != 0) return c;
            return string.CompareOrdinal(cand[a].Name, cand[b].Name);
        });

        var maxMembers = Math.Max(1, options.MaxMembers);
        var groups = ImmutableArray.CreateBuilder<FramingGroup>();
        var members = new List<int>();

        foreach (var s in seedIdx)
        {
            if (assigned[s])
            {
                continue;
            }

            members.Clear();
            members.Add(s);
            assigned[s] = true;

            // Only candidates within one FOV height (in Dec) of the seed can co-frame with it. The seed
            // is the tangent-plane reference and doesn't change, so resolve the band once per group.
            var seedRA = cand[s].RA;
            var seedDec = cand[s].Dec;
            var cosSeed = Math.Max(1e-4, Math.Cos(seedDec * Math.PI / 180.0));
            var lo = LowerBound(order, cand, seedDec - fovHeightDeg);
            var hi = UpperBound(order, cand, seedDec + fovHeightDeg);

            while (members.Count < maxMembers)
            {
                var best = -1;
                var bestDistSq = double.MaxValue;
                for (var oi = lo; oi < hi; oi++)
                {
                    var j = order[oi];
                    if (assigned[j])
                    {
                        continue;
                    }
                    // O(1) RA pre-gate: centres more than one FOV width apart (east-west) can't co-frame.
                    var raOffset = Math.Abs(NormalizeRaHours(cand[j].RA - seedRA) * 15.0 * cosSeed);
                    if (raOffset > fovWidthDeg)
                    {
                        continue;
                    }
                    if (!Fits(cand, members, j, effW, effH))
                    {
                        continue;
                    }
                    var d = CentroidDistSq(cand, members, j);
                    if (d < bestDistSq)
                    {
                        bestDistSq = d;
                        best = j;
                    }
                }
                if (best < 0)
                {
                    break;
                }
                members.Add(best);
                assigned[best] = true;
            }

            if (members.Count == 1 && !options.EmitSingletons)
            {
                continue;
            }

            groups.Add(BuildGroup(cand, members));
        }

        return groups.ToImmutable();
    }

    // Does the current member set PLUS candidate `extra` fit within effW x effH sky degrees, measured
    // in the tangent plane anchored at members[0]?
    private static bool Fits(FramingCandidate[] cand, List<int> members, int extra, double effW, double effH)
    {
        var refRA = cand[members[0]].RA;
        var refDec = cand[members[0]].Dec;
        var cosRef = Math.Max(1e-4, Math.Cos(refDec * Math.PI / 180.0)); // near-pole guard

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var m in members)
        {
            Extend(cand[m], refRA, refDec, cosRef, ref minX, ref maxX, ref minY, ref maxY);
        }
        Extend(cand[extra], refRA, refDec, cosRef, ref minX, ref maxX, ref minY, ref maxY);

        return (maxX - minX) <= effW && (maxY - minY) <= effH;
    }

    private static void Extend(in FramingCandidate c, double refRA, double refDec, double cosRef,
        ref double minX, ref double maxX, ref double minY, ref double maxY)
    {
        var x = NormalizeRaHours(c.RA - refRA) * 15.0 * cosRef; // east, sky degrees
        var y = c.Dec - refDec;                                  // north, sky degrees
        if (x - c.HalfWidthDeg < minX) minX = x - c.HalfWidthDeg;
        if (x + c.HalfWidthDeg > maxX) maxX = x + c.HalfWidthDeg;
        if (y - c.HalfHeightDeg < minY) minY = y - c.HalfHeightDeg;
        if (y + c.HalfHeightDeg > maxY) maxY = y + c.HalfHeightDeg;
    }

    // Squared planar distance from the current members' centre-of-centres to candidate j (extent
    // ignored -- this only ranks which neighbour to try next).
    private static double CentroidDistSq(FramingCandidate[] cand, List<int> members, int j)
    {
        var refRA = cand[members[0]].RA;
        var refDec = cand[members[0]].Dec;
        var cosRef = Math.Max(1e-4, Math.Cos(refDec * Math.PI / 180.0));

        double sx = 0, sy = 0;
        foreach (var m in members)
        {
            sx += NormalizeRaHours(cand[m].RA - refRA) * 15.0 * cosRef;
            sy += cand[m].Dec - refDec;
        }
        sx /= members.Count;
        sy /= members.Count;

        var jx = NormalizeRaHours(cand[j].RA - refRA) * 15.0 * cosRef;
        var jy = cand[j].Dec - refDec;
        var dx = jx - sx;
        var dy = jy - sy;
        return dx * dx + dy * dy;
    }

    private static FramingGroup BuildGroup(FramingCandidate[] cand, List<int> members)
    {
        var refRA = cand[members[0]].RA;
        var refDec = cand[members[0]].Dec;
        var cosRef = Math.Max(1e-4, Math.Cos(refDec * Math.PI / 180.0));

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var m in members)
        {
            Extend(cand[m], refRA, refDec, cosRef, ref minX, ref maxX, ref minY, ref maxY);
        }

        // Pointing = centre of the combined footprint bounding box, mapped back off the tangent plane.
        var centerX = (minX + maxX) * 0.5;
        var centerY = (minY + maxY) * 0.5;
        var centerDec = refDec + centerY;
        var centerRA = refRA + centerX / cosRef / 15.0;
        centerRA = ((centerRA % 24.0) + 24.0) % 24.0;

        // Members + combined name ordered by RA (then name) -- east-to-west reads naturally and is stable.
        var ordered = members
            .OrderBy(m => cand[m].RA)
            .ThenBy(m => cand[m].Name, StringComparer.Ordinal)
            .ToArray();

        var membersBuilder = ImmutableArray.CreateBuilder<FramingCandidate>(ordered.Length);
        var names = new string[ordered.Length];
        for (var k = 0; k < ordered.Length; k++)
        {
            membersBuilder.Add(cand[ordered[k]]);
            names[k] = cand[ordered[k]].Name;
        }

        return new FramingGroup(membersBuilder.MoveToImmutable(), centerRA, centerDec, string.Join(" + ", names));
    }

    // First index in the Dec-sorted `order` whose candidate Dec is >= key.
    private static int LowerBound(int[] order, FramingCandidate[] cand, double key)
    {
        int lo = 0, hi = order.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (cand[order[mid]].Dec < key) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // First index in the Dec-sorted `order` whose candidate Dec is > key (exclusive upper bound).
    private static int UpperBound(int[] order, FramingCandidate[] cand, double key)
    {
        int lo = 0, hi = order.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (cand[order[mid]].Dec <= key) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ΔRA in hours wrapped to [-12, 12] so a group straddling the RA 0/24 seam measures the short way.
    private static double NormalizeRaHours(double dRaHours)
    {
        dRaHours %= 24.0;
        if (dRaHours > 12.0) dRaHours -= 24.0;
        else if (dRaHours < -12.0) dRaHours += 24.0;
        return dRaHours;
    }
}
