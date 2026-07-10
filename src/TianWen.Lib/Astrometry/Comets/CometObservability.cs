using System;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// Comet observability planning: for a site + date range, works out which night a comet is best placed
/// for observation -- a combination of predicted brightness and how high it climbs while the sky is
/// astronomically dark -- and the best time that night. Pure computation over <see cref="CometEphemeris"/>
/// plus the shared <see cref="ObservationScheduler.CalculateNightWindow"/> dark-window solver (which has
/// the astronomical -&gt; nautical -&gt; polar fallback chain), so it is reusable (the MCP <c>catalog.lookup</c>
/// tool today, planner comet proposals later) and unit-testable against a fake clock.
///
/// <para>Accuracy tracks the two-body ephemeris (arcminute-class near the element epoch); it treats the
/// comet as fixed within a single night (it moves only arcminutes) and evaluates altitude at the dark
/// -window edges plus transit, which is where the peak within the window always lies.</para>
/// </summary>
public static class CometObservability
{
    /// <summary>One night's observability summary for a comet at a site.</summary>
    public readonly record struct NightObservation(
        DateTimeOffset NightLocal,
        DateTimeOffset DarkStartUtc,
        DateTimeOffset DarkEndUtc,
        double VMag,
        double MaxAltitudeDeg,
        DateTimeOffset BestTimeUtc,
        double RaJ2000Hours,
        double DecJ2000Deg,
        bool Observable);

    /// <summary>A multi-night scan: every sampled night plus the index of the recommended best.</summary>
    public readonly record struct ObservingWindow(
        ImmutableArray<NightObservation> Nights,
        int BestIndex);

    /// <summary>
    /// Coarse-then-fine best-night search: a weekly scan over <paramref name="coarseSamples"/> weeks locates
    /// the best week, then a nightly scan +/- one week around it pins the exact night. Returns the single
    /// recommended <see cref="NightObservation"/> (top-scored overall) and the coarse weekly samples for a
    /// display outlook. <paramref name="best"/> is default and the samples empty only when no night yields a
    /// solvable ephemeris. Mutates <paramref name="transform"/>'s DateTimeOffset while scanning (caller owns it).
    /// </summary>
    public static bool TryFindBest(
        in CometElements elements, Transform transform, DateTimeOffset startUtc, double minAltitudeDeg,
        out NightObservation best, out ImmutableArray<NightObservation> coarseSamples,
        int coarseWeeks = 26, double coarseStepDays = 7.0)
    {
        var coarse = Scan(elements, transform, startUtc, coarseWeeks, coarseStepDays, minAltitudeDeg);
        coarseSamples = coarse.Nights;
        if (coarse.BestIndex < 0)
        {
            best = default;
            return false;
        }

        // Refine nightly +/- one coarse step around the best week so the recommendation is a precise night.
        var bestCoarse = coarse.Nights[coarse.BestIndex];
        var fineStartUtc = bestCoarse.DarkStartUtc.AddDays(-coarseStepDays);
        var fine = Scan(elements, transform, fineStartUtc, (int)(coarseStepDays * 2) + 1, 1.0, minAltitudeDeg);
        best = fine.BestIndex >= 0 ? fine.Nights[fine.BestIndex] : bestCoarse;
        return true;
    }

    /// <summary>
    /// Scans <paramref name="sampleCount"/> nights spaced <paramref name="stepDays"/> apart from
    /// <paramref name="startUtc"/>, returning per-night observability and the index of the recommended
    /// night. Recommendation: brightest among nights clearing <paramref name="minAltitudeDeg"/> while dark;
    /// if none clear it (or the comet has no photometric model) the highest-altitude night wins.
    /// </summary>
    public static ObservingWindow Scan(
        in CometElements elements, Transform transform, DateTimeOffset startUtc,
        int sampleCount, double stepDays, double minAltitudeDeg)
    {
        var siteTz = transform.TryGetSiteTimeZone(out var tz, out _) ? tz : TimeSpan.Zero;
        var lat = transform.SiteLatitude;
        var lon = transform.SiteLongitude;

        var nights = ImmutableArray.CreateBuilder<NightObservation>(sampleCount);
        for (var i = 0; i < sampleCount; i++)
        {
            var whenUtc = startUtc.AddDays(i * stepDays);
            transform.DateTimeOffset = whenUtc.ToOffset(siteTz);
            var (dark, twilight) = ObservationScheduler.CalculateNightWindow(transform);
            if (twilight <= dark)
            {
                continue;
            }

            // Comet position at the dark-window midpoint (it moves only arcminutes across one night, so a
            // single position is fine for both the altitude peak and the reported magnitude).
            var midUtc = dark + (twilight - dark) / 2.0;
            if (!CometEphemeris.TryGetEquatorialJ2000WithMagnitude(elements, midUtc, out var ra, out var dec, out var mag))
            {
                continue;
            }

            var (maxAlt, bestTime) = PeakAltitudeInWindow(ra, dec, lat, lon, dark, twilight);
            nights.Add(new NightObservation(
                NightLocal: new DateTimeOffset(transform.DateTimeOffset.Date, siteTz),
                DarkStartUtc: dark,
                DarkEndUtc: twilight,
                VMag: mag,
                MaxAltitudeDeg: maxAlt,
                BestTimeUtc: bestTime,
                RaJ2000Hours: ra,
                DecJ2000Deg: dec,
                Observable: maxAlt >= minAltitudeDeg));
        }

        var arr = nights.ToImmutable();
        return new ObservingWindow(arr, PickBest(arr, minAltitudeDeg));
    }

    private static int PickBest(ImmutableArray<NightObservation> nights, double minAlt)
    {
        var best = -1;
        var bestScore = double.NegativeInfinity;
        for (var i = 0; i < nights.Length; i++)
        {
            var score = Score(nights[i], minAlt);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }

    // Clearing the minimum altitude is a hard gate (a huge bump); among nights that clear it the brightest
    // wins (brightness matters far more than a few extra degrees for a comet, which swings many magnitudes
    // across an apparition); altitude is only the final tie-break. A comet with no photometric model scores
    // on altitude alone.
    private static double Score(in NightObservation n, double minAlt)
    {
        var clears = n.MaxAltitudeDeg >= minAlt ? 1000.0 : 0.0;
        var brightness = double.IsNaN(n.VMag) ? 0.0 : (20.0 - n.VMag) * 10.0;
        return clears + brightness + n.MaxAltitudeDeg * 0.1;
    }

    // The comet's altitude peaks within the dark window at either an edge or the upper-meridian transit, so
    // evaluate those three and take the max. Transit only counts when it actually falls inside the window.
    private static (double AltDeg, DateTimeOffset TimeUtc) PeakAltitudeInWindow(
        double raHours, double decDeg, double lat, double lon,
        DateTimeOffset dark, DateTimeOffset twilight)
    {
        var bestAlt = AltitudeDeg(raHours, decDeg, lat, lon, dark);
        var bestTime = dark;

        var edgeAlt = AltitudeDeg(raHours, decDeg, lat, lon, twilight);
        if (edgeAlt > bestAlt)
        {
            bestAlt = edgeAlt;
            bestTime = twilight;
        }

        var reference = dark + (twilight - dark) / 2.0;
        if (RiseTransitSetHelper.TryComputeRiseTransitSet(raHours, decDeg, lat, lon, reference,
                out _, out var transit, out _, out _, out _)
            && transit >= dark && transit <= twilight)
        {
            var transitAlt = AltitudeDeg(raHours, decDeg, lat, lon, transit);
            if (transitAlt > bestAlt)
            {
                bestAlt = transitAlt;
                bestTime = transit;
            }
        }

        return (bestAlt, bestTime);
    }

    private static double AltitudeDeg(double raHours, double decDeg, double lat, double lon, DateTimeOffset utc)
    {
        var lst = SiteContext.ComputeLST(utc.ToUniversalTime(), lon);
        var ha = (lst - raHours) * Math.PI / 12.0;
        var (sinDec, cosDec) = Math.SinCos(decDeg * Math.PI / 180.0);
        var (sinLat, cosLat) = Math.SinCos(lat * Math.PI / 180.0);
        var sinAlt = sinLat * sinDec + cosLat * cosDec * Math.Cos(ha);
        return Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)) * 180.0 / Math.PI;
    }
}
