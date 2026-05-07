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
/// blackbody ratios from B-V, and computes per-channel white balance multipliers.
/// </summary>
public static class Tycho2ColorCalibration
{
    /// <summary>One matched star with observed and expected photometry.</summary>
    public readonly record struct StarMatch(
        double ObservedR, double ObservedG, double ObservedB,
        double ExpectedR, double ExpectedG, double ExpectedB,
        double Magnitude);

    /// <summary>
    /// Computes per-channel white balance multipliers by matching detected stars to
    /// Tycho-2 and comparing observed photometry to B-V predicted colors.
    /// </summary>
    /// <param name="image">Unstretched image (3-channel RGB, or 1-channel Bayer).</param>
    /// <param name="stars">Detected stars with centroids.</param>
    /// <param name="wcs">Plate-solve WCS solution.</param>
    /// <param name="db">Initialised celestial object database.</param>
    /// <param name="apertureRadius">Aperture radius in pixels for photometry.</param>
    /// <param name="matchRadiusArcsec">Maximum angular separation for a valid match.</param>
    /// <param name="maxMagDiff">Maximum magnitude difference for a valid match.</param>
    /// <param name="minStars">Minimum number of matched stars required to return a result.</param>
    /// <returns>White balance (R, G, B) multipliers normalised so max = 1, or null if insufficient matches.</returns>
    public static (float R, float G, float B, int MatchCount)? ComputeWhiteBalance(
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

        var matches = MatchStars(stars, wcs, db, matchRadiusArcsec, maxMagDiff);
        if (matches.Count < minStars) return null;

        var photometry = ExtractPhotometry(image, matches, apertureRadius);
        if (photometry.Count < minStars) return null;

        var (wbR, wbG, wbB) = ComputeMultipliers(photometry);
        return (wbR, wbG, wbB, photometry.Count);
    }

    /// <summary>
    /// Matches detected stars to Tycho-2 catalog entries.
    /// </summary>
    private static List<(ImagedStar Star, CelestialObject Tycho)> MatchStars(
        StarList stars, WCS wcs, ICelestialObjectDB db,
        float matchRadiusArcsec, float maxMagDiff)
    {
        var matches = new List<(ImagedStar, CelestialObject)>();
        var matchRadiusDeg = matchRadiusArcsec / 3600.0;

        foreach (var star in stars)
        {
            var sky = wcs.PixelToSky(star.XCentroid + 1, star.YCentroid + 1);
            if (sky is not { } pos) continue;

            var candidates = db.CoordinateGrid[pos.RA, pos.Dec];
            if (candidates is not { Count: > 0 }) continue;

            var (bestMatch, bestDist) = FindBestMatch(star, pos, db, candidates, matchRadiusDeg, maxMagDiff);

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
        double matchRadiusDeg, float maxMagDiff)
    {
        CelestialObject? bestMatch = null;
        var bestDist = matchRadiusDeg;

        foreach (var idx in candidates)
        {
            if (!db.TryLookupByIndex(idx, out var obj)) continue;
            if (obj.ObjectType is not ObjectType.Star) continue;
            if (Half.IsNaN(obj.BMinusV)) continue;

            var distDeg = AngularSeparation(sky.RA, sky.Dec, obj.RA, obj.Dec);
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
        Image image, List<(ImagedStar Star, CelestialObject Tycho)> matches, int apertureRadius)
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
                var (expR, expG, expB) = SyntheticStarFieldRenderer.BMinusVToRGB((double)tycho.BMinusV);
                result.Add(new StarMatch(netR, netG, netB, expR, expG, expB, (double)tycho.V_Mag));
            }
            else
            {
                var bgPerPixelR = bgR / bgPixels; var bgPerPixelG = bgG / bgPixels; var bgPerPixelB = bgB / bgPixels;
                var netR = obsR - bgPerPixelR * apPixels;
                var netG = obsG - bgPerPixelG * apPixels;
                var netB = obsB - bgPerPixelB * apPixels;
                if (netR <= 0 || netG <= 0 || netB <= 0) continue;
                var (expR, expG, expB) = SyntheticStarFieldRenderer.BMinusVToRGB((double)tycho.BMinusV);
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
    /// Exposed for testing. wbR = median((expectedR/observedR) / (expectedG/observedG)), etc.
    /// </summary>
    internal static (float R, float G, float B) ComputeMultipliers(
        ReadOnlySpan<float> obsR, ReadOnlySpan<float> obsG, ReadOnlySpan<float> obsB,
        ReadOnlySpan<float> expR, ReadOnlySpan<float> expG, ReadOnlySpan<float> expB)
    {
        var n = obsR.Length;
        var rRatios = new float[n];
        var bRatios = new float[n];

        for (var i = 0; i < n; i++)
        {
            if (obsG[i] <= 0 || expG[i] <= 0) continue;
            var norm = obsG[i] / expG[i];
            rRatios[i] = expR[i] / obsR[i] * norm;
            bRatios[i] = expB[i] / obsB[i] * norm;
        }

        Array.Sort(rRatios);
        Array.Sort(bRatios);

        // Trim top/bottom 20% to reject outliers before taking median
        var trim = n / 5;
        var wbR = Math.Clamp(rRatios[n / 2], rRatios[trim], rRatios[n - 1 - trim]);
        var wbB = Math.Clamp(bRatios[n / 2], bRatios[trim], bRatios[n - 1 - trim]);
        // Clamp to reasonable range — values outside [0.5, 2.0] indicate sensor/model
        // mismatch rather than correctable color cast
        wbR = Math.Clamp(wbR, 0.5f, 2.0f);
        wbB = Math.Clamp(wbB, 0.5f, 2.0f);
        return (wbR, 1f, wbB);
    }

    /// <summary>
    /// Computes per-channel white balance multipliers via robust median of observed/expected ratios.
    /// </summary>
    private static (float R, float G, float B) ComputeMultipliers(List<StarMatch> photometry)
    {
        var rRatios = new float[photometry.Count];
        var gRatios = new float[photometry.Count];
        var bRatios = new float[photometry.Count];

        for (var i = 0; i < photometry.Count; i++)
        {
            var m = photometry[i];
            // WB multiplier = expected / observed, normalised so green = 1
            // wR = (expectedR / observedR) * (observedG / expectedG)
            var norm = (float)(m.ObservedG / m.ExpectedG);
            rRatios[i] = (float)(m.ExpectedR / m.ObservedR * norm);
            gRatios[i] = 1f;
            bRatios[i] = (float)(m.ExpectedB / m.ObservedB * norm);
        }

        Array.Sort(rRatios);
        Array.Sort(bRatios);

        var wbR = Math.Clamp(rRatios[photometry.Count / 2], 0.1f, 10f);
        var wbB = Math.Clamp(bRatios[photometry.Count / 2], 0.1f, 10f);
        return (wbR, 1f, wbB);
    }
}
