using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices.Fake;

namespace TianWen.Lib.Imaging.ColorCalibration;

/// <summary>
/// Photometric color calibration against the Tycho-2 catalog.
/// Matches detected stars to Tycho-2 entries via WCS coordinates, extracts aperture
/// photometry from the unstretched image, compares observed RGB ratios to expected
/// ratios from B-V (blackbody) or Pickles SED + system throughput, and computes
/// per-channel white balance multipliers.
/// </summary>
public static class Tycho2ColorCalibration
{
    /// <summary>One matched star with observed and expected photometry.</summary>
    public readonly record struct StarMatch(
        double ObservedR, double ObservedG, double ObservedB,
        double ExpectedR, double ExpectedG, double ExpectedB,
        double Magnitude);

    /// <summary>Outcome of the white-balance fit. Carries both the converged
    /// multipliers and the iteration trail (initial-vs-final match count +
    /// iteration count) so the caller can log "started with 127 candidates,
    /// 119 survived 3-iter kappa-sigma" without re-deriving the numbers.</summary>
    public readonly record struct SpccWhiteBalanceResult(
        float R, float G, float B,
        int InitialMatches,
        int FinalMatches,
        int Iterations)
    {
        /// <summary>Back-compat shim: callers that only want the survivor
        /// count read this; equivalent to <see cref="FinalMatches"/>.</summary>
        public int MatchCount => FinalMatches;
    }

    /// <summary>Default kappa for iterative SPCC rejection. 3.0 keeps ~99 %
    /// of a clean Gaussian sample while dropping the photometric outliers
    /// that bias the median (close-pair contamination, faint-star aperture
    /// noise, mismatched Tycho-2 catalog rows that passed the 5" coord
    /// gate). Looser than the hot-pixel sigma=8 because the WB fit's signal
    /// IS noisy by nature -- we want to drop only the obvious mismatches.</summary>
    private const float DefaultKappaSigma = 3.0f;

    /// <summary>Iteration cap; real data converges in 2-4 iters.</summary>
    private const int DefaultMaxIterations = 8;

    /// <summary>Minimum survivors before the iteration aborts. Below this,
    /// the kappa-sigma cut is on shaky statistical footing -- we'd rather
    /// keep an outlier than under-fit a 4-star sample.</summary>
    private const int MinSurvivors = 5;

    // Delegate type for computing expected RGB from a stellar B-V colour index.
    // Used by ExtractPhotometry to support both blackbody and SED-based approaches.
    private delegate (double R, double G, double B) ExpectedColorFunc(double bv);

    /// <summary>
    /// Computes per-channel white balance multipliers by matching detected stars to
    /// Tycho-2 and comparing observed photometry to B-V predicted colors.
    /// </summary>
    public static SpccWhiteBalanceResult? ComputeWhiteBalance(
        Image image,
        StarList stars,
        WCS wcs,
        ICelestialObjectDB db,
        int apertureRadius = 6,
        float matchRadiusArcsec = 5.0f,
        float maxMagDiff = 1.5f,
        int minStars = 5)
    {
        if (image.ChannelCount < 3 && image.ImageMeta.SensorType is not SensorType.RGGB) return null;

        var dtYr = ComputeDtJulianYears(image);
        var matches = MatchStars(stars, wcs, db, matchRadiusArcsec, maxMagDiff, dtYr);
        if (matches.Count < minStars) return null;

        var photometry = ExtractPhotometry(image, matches, apertureRadius,
            bv => SyntheticStarFieldRenderer.BMinusVToRGB(bv));
        if (photometry.Count < minStars) return null;

        return ComputeMultipliers(photometry);
    }

    /// <summary>
    /// Spectrophotometric color calibration: matches detected stars to Tycho-2,
    /// maps each star's B-V to the closest Pickles stellar SED, integrates the SED
    /// through the per-channel system throughput curves to compute expected R/G/B
    /// ratios, then fits per-channel white balance multipliers from the observed
    /// aperture photometry.
    /// </summary>
    /// <param name="image">Unstretched RGB or Bayer image.</param>
    /// <param name="stars">Detected stars with centroids.</param>
    /// <param name="wcs">Plate-solve WCS solution.</param>
    /// <param name="db">Initialised celestial object database.</param>
    /// <param name="tsysR">System throughput for the red channel (QE × filter × CFA).</param>
    /// <param name="tsysG">System throughput for the green channel.</param>
    /// <param name="tsysB">System throughput for the blue channel.</param>
    /// <returns>White balance (R, G, B) multipliers, or null if insufficient matches.</returns>
    public static SpccWhiteBalanceResult? ComputeSpectrophotometricWhiteBalance(
        Image image,
        StarList stars,
        WCS wcs,
        ICelestialObjectDB db,
        FilterCurve tsysR, FilterCurve tsysG, FilterCurve tsysB,
        int apertureRadius = 6,
        float matchRadiusArcsec = 5.0f,
        float maxMagDiff = 1.5f,
        int minStars = 5)
    {
        if (image.ChannelCount < 3 && image.ImageMeta.SensorType is not SensorType.RGGB) return null;

        // Ensure SED database is loaded
        if (!FilterCurveDatabase.IsLoaded) return null;

        var dtYr = ComputeDtJulianYears(image);
        var matches = MatchStars(stars, wcs, db, matchRadiusArcsec, maxMagDiff, dtYr);
        if (matches.Count < minStars) return null;

        var photometry = ExtractPhotometry(image, matches, apertureRadius,
            bv => ComputeExpectedRgbFromSed(bv, tsysR, tsysG, tsysB));
        if (photometry.Count < minStars) return null;

        return ComputeMultipliers(photometry);
    }

    /// <summary>
    /// Maps a B-V colour index to expected per-channel RGB via the closest
    /// Pickles SED integrated through the system throughput curves.
    /// Normalised so that the maximum channel = 1.0.
    /// </summary>
    private static (double R, double G, double B) ComputeExpectedRgbFromSed(
        double bv, FilterCurve tsysR, FilterCurve tsysG, FilterCurve tsysB)
    {
        if (!FilterCurveDatabase.TryGetSedByBv(bv, out var sed))
            // Fall back to blackbody if SED lookup fails
            return SyntheticStarFieldRenderer.BMinusVToRGB(bv);

        var fluxR = FilterCurve.IntegrateSedThroughput(sed, tsysR);
        var fluxG = FilterCurve.IntegrateSedThroughput(sed, tsysG);
        var fluxB = FilterCurve.IntegrateSedThroughput(sed, tsysB);

        if (fluxG <= 0)
            return SyntheticStarFieldRenderer.BMinusVToRGB(bv);

        // Normalise so max channel = 1.0 (consistent with BMinusVToRGB convention)
        var r = fluxR / fluxG;
        var g = 1.0;
        var b = fluxB / fluxG;
        var max = Math.Max(r, Math.Max(g, b));
        return (r / max, g / max, b / max);
    }

    /// <summary>
    /// Maps the image's FITS DATE-OBS to fractional Julian years since J2000.0
    /// for use as the dt input to <see cref="CoordinateUtils.PropagatePm"/>.
    /// Returns <c>0.0</c> (no propagation) when no plausible exposure date is
    /// available -- synthetic test frames, missing FITS header, or any
    /// pre-1900 value -- so the matcher keeps its prior behaviour exactly.
    /// </summary>
    private static double ComputeDtJulianYears(Image image)
    {
        var exposureStart = image.ImageMeta.ExposureStartTime;
        return exposureStart.Year > 1900 ? exposureStart.JulianYearsSinceJ2000() : 0.0;
    }

    /// <summary>
    /// Matches detected stars to Tycho-2 catalog entries. Catalog stars are
    /// projected from J2000 to the image epoch via proper motion before the
    /// angular-separation cut -- without this, the ~2.5% of Tycho-2 stars with
    /// |pm| > 30 mas/yr drift past the <paramref name="matchRadiusArcsec"/>
    /// tolerance over 26 years of catalog age, get mis-matched or dropped, and
    /// then survive into the photometric kappa-sigma pass as outliers that
    /// bias the white-balance fit.
    /// </summary>
    private static List<(ImagedStar Star, CelestialObject Tycho)> MatchStars(
        StarList stars, WCS wcs, ICelestialObjectDB db,
        float matchRadiusArcsec, float maxMagDiff,
        double dtJulianYears)
    {
        var matches = new List<(ImagedStar, CelestialObject)>();
        var matchRadiusDeg = matchRadiusArcsec / 3600.0;

        foreach (var star in stars)
        {
            var sky = wcs.PixelToSky(star.XCentroid + 1, star.YCentroid + 1);
            if (sky is not { } pos) continue;

            var candidates = db.CoordinateGrid[pos.RA, pos.Dec];
            if (candidates is not { Count: > 0 }) continue;

            var (bestMatch, bestDist) = FindBestMatch(star, pos, db, candidates, matchRadiusDeg, maxMagDiff, dtJulianYears);

            if (bestMatch is { } match && !Half.IsNaN(match.BMinusV) && match.V_Mag is var vMag && !Half.IsNaN(vMag))
            {
                matches.Add((star, match));
            }
        }

        return matches;
    }

    private static (CelestialObject? Match, double DistanceDeg) FindBestMatch(
        ImagedStar star, (double RA, double Dec) sky,
        ICelestialObjectDB db, IReadOnlyCollection<CatalogIndex> candidates,
        double matchRadiusDeg, float maxMagDiff,
        double dtJulianYears)
    {
        CelestialObject? bestMatch = null;
        var bestDist = matchRadiusDeg;

        foreach (var idx in candidates)
        {
            if (!db.TryLookupByIndex(idx, out var obj)) continue;
            if (obj.ObjectType is not ObjectType.Star) continue;
            if (Half.IsNaN(obj.BMinusV)) continue;

            // Propagate Tycho-2 J2000 position to the image epoch. Non-Tycho-2
            // candidates (cross-ref'd HD/HIP that landed here via the composite
            // RA/Dec grid) skip the propagation -- the cross-walk loses pm
            // anyway, and the typical 0.18" median drift sits well inside the
            // 5" SPCC tolerance for those.
            double matchRa = obj.RA, matchDec = obj.Dec;
            if (dtJulianYears != 0.0
                && db.TryGetTycho2Star(idx, out var tyc)
                && (tyc.PmRaTenthMasPerYr != 0 || tyc.PmDecTenthMasPerYr != 0))
            {
                (matchRa, matchDec) = CoordinateUtils.PropagatePm(
                    obj.RA, obj.Dec,
                    tyc.PmRaMasPerYr, tyc.PmDecMasPerYr,
                    dtJulianYears);
            }

            var distDeg = AngularSeparation(sky.RA, sky.Dec, matchRa, matchDec);
            if (distDeg < bestDist)
            {
                bestDist = distDeg;
                bestMatch = obj;
            }
        }

        return (bestMatch, bestDist);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double AngularSeparation(double ra1, double dec1, double ra2, double dec2)
    {
        var dRa = (ra1 - ra2) * Math.PI / 180.0;
        var dDec = (dec1 - dec2) * Math.PI / 180.0;
        var a = Math.Sin(dDec / 2); a *= a;
        var b = Math.Sin(dRa / 2); b *= b;
        var c = Math.Cos(dec1 * Math.PI / 180.0) * Math.Cos(dec2 * Math.PI / 180.0);
        return 2.0 * Math.Asin(Math.Sqrt(a + c * b)) * 180.0 / Math.PI;
    }

    /// <summary>
    /// Extracts per-channel aperture photometry for each matched star.
    /// For Bayer (RGGB) images, samples raw sub-pixels to capture the true sensor
    /// channel response including the 2x green oversampling bias.
    /// </summary>
    private static List<StarMatch> ExtractPhotometry(
        Image image, List<(ImagedStar Star, CelestialObject Tycho)> matches, int apertureRadius,
        ExpectedColorFunc expectedColor)
    {
        var (channelCount, width, height) = image.Shape;
        var isBayer = channelCount == 1 && image.ImageMeta.SensorType is SensorType.RGGB;
        var result = new List<StarMatch>(matches.Count);

        foreach (var (star, tycho) in matches)
        {
            var (cx, cy) = (star.XCentroid, star.YCentroid);
            var annulusInner = apertureRadius + 2;
            var annulusOuter = apertureRadius + 5;

            var obsR = 0.0; var obsG = 0.0; var obsB = 0.0;
            var apR = 0; var apG = 0; var apB = 0;
            var bgR = 0.0; var bgG = 0.0; var bgB = 0.0;
            var bgRc = 0; var bgGc = 0; var bgBc = 0;

            for (var y = (int)(cy - annulusOuter); y <= (int)(cy + annulusOuter); y++)
            {
                for (var x = (int)(cx - annulusOuter); x <= (int)(cx + annulusOuter); x++)
                {
                    if (x < 0 || x >= width || y < 0 || y >= height) continue;
                    var dx = x - cx; var dy = y - cy;
                    var dist = Math.Sqrt(dx * dx + dy * dy);

                    var isAp = dist <= apertureRadius;
                    var isBg = !isAp && dist >= annulusInner && dist <= annulusOuter;
                    if (!isAp && !isBg) continue;

                    if (isBayer)
                    {
                        var v = image[0, y, x];
                        if (float.IsNaN(v)) continue;
                        var ch = BayerChannel(x, y);
                        if (isAp) { AddChannel(ch, v, ref obsR, ref obsG, ref obsB, ref apR, ref apG, ref apB); }
                        else { AddChannel(ch, v, ref bgR, ref bgG, ref bgB, ref bgRc, ref bgGc, ref bgBc); }
                    }
                    else
                    {
                        if (float.IsNaN(image[0, y, x])) continue;
                        if (isAp)
                        {
                            obsR += image[0, y, x]; obsG += image[1, y, x]; obsB += image[2, y, x];
                            apR++;
                        }
                        else
                        {
                            bgR += image[0, y, x]; bgG += image[1, y, x]; bgB += image[2, y, x];
                            bgRc++;
                        }
                    }
                }
            }

            var apPixels = isBayer ? apR + apG + apB : apR;
            var bgPixels = isBayer ? bgRc + bgGc + bgBc : bgRc;
            if (apPixels < 3 || bgPixels < 5) continue;

            if (isBayer)
            {
                var netR = obsR - (bgR / Math.Max(bgRc, 1)) * apR;
                var netG = obsG - (bgG / Math.Max(bgGc, 1)) * apG;
                var netB = obsB - (bgB / Math.Max(bgBc, 1)) * apB;
                if (netR <= 0 || netG <= 0 || netB <= 0) continue;
                var (expR, expG, expB) = expectedColor((double)tycho.BMinusV);
                result.Add(new StarMatch(netR, netG, netB, expR, expG, expB, (double)tycho.V_Mag));
            }
            else
            {
                var bgPerPixelR = bgR / bgPixels; var bgPerPixelG = bgG / bgPixels; var bgPerPixelB = bgB / bgPixels;
                var netR = obsR - bgPerPixelR * apPixels;
                var netG = obsG - bgPerPixelG * apPixels;
                var netB = obsB - bgPerPixelB * apPixels;
                if (netR <= 0 || netG <= 0 || netB <= 0) continue;
                var (expR, expG, expB) = expectedColor((double)tycho.BMinusV);
                result.Add(new StarMatch(netR, netG, netB, expR, expG, expB, (double)tycho.V_Mag));
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddChannel(int ch, double v, ref double r, ref double g, ref double b, ref int rc, ref int gc, ref int bc)
    {
        switch (ch)
        {
            case 0: r += v; rc++; break;
            case 1: g += v; gc++; break;
            case 2: b += v; bc++; break;
        }
    }

    /// <summary>Returns 0=R, 1=G, 2=B for an RGGB Bayer pixel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BayerChannel(int x, int y) => ((y & 1) << 1) | (x & 1) switch
    {
        0 when (y & 1) == 0 => 0, // R at even, even
        1 when (y & 1) == 0 => 1, // G at odd, even
        0 when (y & 1) == 1 => 1, // G at even, odd
        _ => 2,                    // B at odd, odd
    };

    /// <summary>
    /// Computes per-channel white balance multipliers from observed and expected channel values.
    /// Exposed for testing. Internally runs iterative kappa-sigma rejection on the
    /// per-channel ratios; the returned wbR / wbB are the converged medians clamped
    /// to [0.5, 2.0] (values outside that range indicate sensor/model mismatch
    /// rather than correctable color cast).
    /// </summary>
    internal static (float R, float G, float B) ComputeMultipliers(
        ReadOnlySpan<float> obsR, ReadOnlySpan<float> obsG, ReadOnlySpan<float> obsB,
        ReadOnlySpan<float> expR, ReadOnlySpan<float> expG, ReadOnlySpan<float> expB)
    {
        var n = obsR.Length;
        var rRatios = new float[n];
        var bRatios = new float[n];

        var k = 0;
        for (var i = 0; i < n; i++)
        {
            if (obsG[i] <= 0 || expG[i] <= 0 || obsR[i] <= 0 || obsB[i] <= 0) continue;
            var norm = obsG[i] / expG[i];
            rRatios[k] = expR[i] / obsR[i] * norm;
            bRatios[k] = expB[i] / obsB[i] * norm;
            k++;
        }

        var result = FitIterativeKappaSigma(rRatios.AsSpan(0, k), bRatios.AsSpan(0, k));
        var wbR = Math.Clamp(result.R, 0.5f, 2.0f);
        var wbB = Math.Clamp(result.B, 0.5f, 2.0f);
        return (wbR, 1f, wbB);
    }

    /// <summary>
    /// Computes per-channel white balance multipliers via iterative kappa-sigma
    /// fit on the observed/expected ratios. Same algorithm as the public
    /// span overload, but returns the iteration outcome (initial vs. final
    /// match counts + iteration count) so the caller can log the funnel.
    /// </summary>
    private static SpccWhiteBalanceResult ComputeMultipliers(List<StarMatch> photometry)
    {
        var rRatios = new float[photometry.Count];
        var bRatios = new float[photometry.Count];

        // Same valid-input filter as the span overload -- skip rows that
        // would produce a divide-by-zero or NaN in the ratio. Pack the
        // surviving rows into the front of the array so the iteration
        // works on a contiguous span.
        var k = 0;
        for (var i = 0; i < photometry.Count; i++)
        {
            var m = photometry[i];
            if (m.ObservedG <= 0 || m.ExpectedG <= 0 || m.ObservedR <= 0 || m.ObservedB <= 0) continue;
            var norm = (float)(m.ObservedG / m.ExpectedG);
            rRatios[k] = (float)(m.ExpectedR / m.ObservedR * norm);
            bRatios[k] = (float)(m.ExpectedB / m.ObservedB * norm);
            k++;
        }

        var fit = FitIterativeKappaSigma(rRatios.AsSpan(0, k), bRatios.AsSpan(0, k));
        return new SpccWhiteBalanceResult(
            R: Math.Clamp(fit.R, 0.1f, 10f),
            G: 1f,
            B: Math.Clamp(fit.B, 0.1f, 10f),
            InitialMatches: photometry.Count,
            FinalMatches: fit.FinalCount,
            Iterations: fit.Iterations);
    }

    /// <summary>
    /// Iterative kappa-sigma fit on two correlated ratio streams (one per
    /// non-reference channel). Each iteration:
    /// <list type="number">
    ///   <item>Compute median of currently-live R / B ratios.</item>
    ///   <item>Compute MAD of (ratio - median) over those same live entries.</item>
    ///   <item>Reject rows where either channel exceeds
    ///   <c>kappa * 1.4826 * MAD</c> from the channel median.</item>
    ///   <item>Stop when no new rejections, or survivors would dip below
    ///   <see cref="MinSurvivors"/>, or max iterations hit.</item>
    /// </list>
    /// Rejection is OR-combined across channels (a star with bad R ratio
    /// AND good B ratio still drops -- it's likely a contaminated photometry
    /// entry overall, not just one channel). When MAD is 0 on a channel
    /// (uniform inlier subset) that channel contributes no rejection
    /// signal -- the loop continues on the other channel until convergence.
    /// </summary>
    private static (float R, float B, int FinalCount, int Iterations) FitIterativeKappaSigma(
        Span<float> rRatios, Span<float> bRatios,
        float kappa = DefaultKappaSigma, int maxIterations = DefaultMaxIterations)
    {
        var n = rRatios.Length;
        if (n == 0) return (1f, 1f, 0, 0);
        if (n < MinSurvivors)
        {
            // Sample too small for kappa-sigma to be statistically meaningful;
            // fall back to plain median. Tests with n=1 / n=5 hit this path.
            var rSorted = rRatios.ToArray(); Array.Sort(rSorted);
            var bSorted = bRatios.ToArray(); Array.Sort(bSorted);
            return (rSorted[n / 2], bSorted[n / 2], n, 0);
        }

        // Track survival via a parallel alive[] flag so we don't need to
        // physically compact the arrays on each iteration. Allocating once
        // up front is cheaper than per-iter compaction for the typical
        // 100-300 star photometry list.
        Span<bool> alive = stackalloc bool[n];
        alive.Fill(true);
        var liveCount = n;
        var medianR = 0f;
        var medianB = 0f;
        var iter = 0;

        // Work buffer reused per iteration to read out live values, sort,
        // and compute median/MAD.
        Span<float> workR = stackalloc float[n];
        Span<float> workB = stackalloc float[n];

        for (iter = 0; iter < maxIterations; iter++)
        {
            // Collect live values into the contiguous prefix of the work buf.
            var liveIdx = 0;
            for (var i = 0; i < n; i++)
            {
                if (alive[i])
                {
                    workR[liveIdx] = rRatios[i];
                    workB[liveIdx] = bRatios[i];
                    liveIdx++;
                }
            }
            workR[..liveIdx].Sort();
            workB[..liveIdx].Sort();
            medianR = workR[liveIdx / 2];
            medianB = workB[liveIdx / 2];

            // Compute MAD per channel against its own median. We can reuse
            // the work buffers because we've already consumed the sorted
            // values to read the medians.
            for (var i = 0; i < liveIdx; i++)
            {
                workR[i] = MathF.Abs(workR[i] - medianR);
                workB[i] = MathF.Abs(workB[i] - medianB);
            }
            workR[..liveIdx].Sort();
            workB[..liveIdx].Sort();
            var madR = workR[liveIdx / 2];
            var madB = workB[liveIdx / 2];

            // Kappa-sigma threshold; MAD * 1.4826 ≈ stddev for a Gaussian.
            // A zero MAD means the inlier subset is uniform on that channel,
            // which signals "no spread to threshold against" -- skip
            // rejection on that channel rather than rejecting everything
            // (threshold=0 would).
            var thresholdR = madR > 0f ? kappa * 1.4826f * madR : float.PositiveInfinity;
            var thresholdB = madB > 0f ? kappa * 1.4826f * madB : float.PositiveInfinity;

            // First pass: count the rejections this iteration would make.
            // We need this up front so the floor check (MinSurvivors) can
            // decide whether to commit them -- a single iteration that
            // would drop us below the floor would otherwise leave us in
            // an under-fit state.
            var wouldReject = 0;
            for (var i = 0; i < n; i++)
            {
                if (!alive[i]) continue;
                if (MathF.Abs(rRatios[i] - medianR) > thresholdR ||
                    MathF.Abs(bRatios[i] - medianB) > thresholdB)
                {
                    wouldReject++;
                }
            }
            if (wouldReject == 0) break;
            if (liveCount - wouldReject < MinSurvivors)
            {
                // Don't commit; we'd be fitting too thin a remainder.
                // Keep the current medians as the converged answer.
                break;
            }

            // Commit rejections.
            for (var i = 0; i < n; i++)
            {
                if (!alive[i]) continue;
                if (MathF.Abs(rRatios[i] - medianR) > thresholdR ||
                    MathF.Abs(bRatios[i] - medianB) > thresholdB)
                {
                    alive[i] = false;
                    liveCount--;
                }
            }
        }

        return (medianR, medianB, liveCount, iter);
    }
}
