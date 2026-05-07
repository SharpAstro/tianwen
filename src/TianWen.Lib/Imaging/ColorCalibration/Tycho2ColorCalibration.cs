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
    /// <param name="image">Unstretched image (typically 3-channel RGB, must be plate-solved).</param>
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
        if (image.ChannelCount < 3) return null;

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
    /// </summary>
    private static List<StarMatch> ExtractPhotometry(
        Image image, List<(ImagedStar Star, CelestialObject Tycho)> matches, int apertureRadius)
    {
        var (channelCount, width, height) = image.Shape;
        var result = new List<StarMatch>(matches.Count);

        foreach (var (star, tycho) in matches)
        {
            var (cx, cy) = (star.XCentroid, star.YCentroid);
            var annulusInner = apertureRadius + 2;
            var annulusOuter = apertureRadius + 5;

            var obsR = 0.0; var obsG = 0.0; var obsB = 0.0;
            var apPixels = 0; var bgPixels = 0;
            var bgR = 0.0; var bgG = 0.0; var bgB = 0.0;

            for (var y = (int)(cy - annulusOuter); y <= (int)(cy + annulusOuter); y++)
            {
                for (var x = (int)(cx - annulusOuter); x <= (int)(cx + annulusOuter); x++)
                {
                    if (x < 0 || x >= width || y < 0 || y >= height) continue;
                    var dx = x - cx; var dy = y - cy;
                    var dist = Math.Sqrt(dx * dx + dy * dy);

                    if (float.IsNaN(image[0, y, x])) continue;

                    if (dist <= apertureRadius)
                    {
                        obsR += image[0, y, x]; obsG += image[1, y, x]; obsB += image[2, y, x];
                        apPixels++;
                    }
                    else if (dist >= annulusInner && dist <= annulusOuter)
                    {
                        bgR += image[0, y, x]; bgG += image[1, y, x]; bgB += image[2, y, x];
                        bgPixels++;
                    }
                }
            }

            if (apPixels < 3 || bgPixels < 5) continue;

            var bgPerPixelR = bgR / bgPixels; var bgPerPixelG = bgG / bgPixels; var bgPerPixelB = bgB / bgPixels;
            var netR = obsR - bgPerPixelR * apPixels;
            var netG = obsG - bgPerPixelG * apPixels;
            var netB = obsB - bgPerPixelB * apPixels;
            if (netR <= 0 || netG <= 0 || netB <= 0) continue;

            var (expR, expG, expB) = SyntheticStarFieldRenderer.BMinusVToRGB((double)tycho.BMinusV);

            result.Add(new StarMatch(netR, netG, netB, expR, expG, expB, (double)tycho.V_Mag));
        }

        return result;
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
            var scale = (float)(m.ExpectedG / m.ObservedG); // normalise by green
            rRatios[i] = (float)(m.ObservedR * scale / m.ExpectedR);
            gRatios[i] = 1f;
            bRatios[i] = (float)(m.ObservedB * scale / m.ExpectedB);
        }

        Array.Sort(rRatios);
        Array.Sort(bRatios);

        var wbR = rRatios[photometry.Count / 2];
        var wbG = 1f;
        var wbB = bRatios[photometry.Count / 2];

        var max = Math.Max(wbR, Math.Max(wbG, wbB));
        if (max <= 0) max = 1f;
        return (wbR / max, wbG / max, wbB / max);
    }
}
