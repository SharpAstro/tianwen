using System;
using System.Collections.Generic;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.ColorCalibration;

/// <summary>A frame's photometric zero point: the median of <c>V + 2.5 log10(flux)</c> over its Tycho-2 matches.</summary>
/// <param name="ZeroPoint">The median zero point, in magnitudes, against the flux unit the stars were measured in.</param>
/// <param name="Scatter">1.4826 times the median absolute deviation of the per-star samples, in magnitudes.</param>
/// <param name="Stars">How many matched stars the median is over.</param>
public readonly record struct PhotometricZeroPointResult(float ZeroPoint, float Scatter, int Stars);

/// <summary>
/// The photometric zero point of one frame (#1974 section 2): a frame's detected stars are matched to Tycho-2 by position
/// (<see cref="Tycho2ColorCalibration.MatchStars"/>, the matcher SPCC uses), and the zero point is the median over the matches
/// of the catalogue's V magnitude plus 2.5 log10 of the star's instrumental flux, so that <c>m = zp - 2.5 log10(flux)</c>.
/// The ONE computation: SPCC's magnitude gate reads its zero point through <see cref="TryFromMatches"/> and the grade reads
/// the frame's transparency through <see cref="TryMeasure"/>, so the two cannot drift.
/// </summary>
public static class PhotometricZeroPoint
{
    /// <summary>The fewest matched stars a median zero point is read from; below it a few mismatches set it.</summary>
    public const int MinStars = 20;

    /// <summary>The default match radius, as SPCC's.</summary>
    private const float MatchRadiusArcsec = 5.0f;

    /// <summary>The default magnitude gate, as SPCC's.</summary>
    private const float MaxMagDiff = 1.5f;

    /// <summary>
    /// The median zero point of already matched stars (and its scatter). Median, not mean, so the inevitable few mismatches do not bias it. False
    /// when fewer than <paramref name="minStars"/> matches have a positive flux and a finite V magnitude.
    /// </summary>
    internal static bool TryFromMatches(List<(ImagedStar Star, CelestialObject Tycho)> matches, out PhotometricZeroPointResult result, int minStars = MinStars)
    {
        Span<float> samples = matches.Count <= 256
            ? stackalloc float[matches.Count]
            : new float[matches.Count];
        var k = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var (s, t) = matches[i];
            if (s.Flux <= 0 || Half.IsNaN(t.V_Mag)) continue;
            samples[k++] = (float)t.V_Mag + 2.5f * MathF.Log10(s.Flux);
        }
        if (k < minStars)
        {
            result = new PhotometricZeroPointResult(float.NaN, float.NaN, k);
            return false;
        }

        // MedianFast permutes in place (QuickSelect), so only its return value is consumed; the deviations are read off the permuted
        // buffer, which holds the same samples.
        var median = StatisticsHelper.MedianFast(samples[..k]);
        for (var i = 0; i < k; i++)
        {
            samples[i] = MathF.Abs(samples[i] - median);
        }
        var mad = StatisticsHelper.MedianFast(samples[..k]);
        result = new PhotometricZeroPointResult(median, 1.4826f * mad, k);
        return true;
    }

    /// <summary>
    /// Matches <paramref name="stars"/> to Tycho-2 through <paramref name="wcs"/> and reads the zero point, in the flux unit the
    /// stars were measured in. False without a usable WCS, when too few stars match, or when the frame has no catalogue to match.
    /// </summary>
    /// <param name="stars">The frame's detected stars, in the pixel frame <paramref name="wcs"/> answers in.</param>
    /// <param name="wcs">The frame's WCS (header or a solve).</param>
    /// <param name="db">The initialised catalogue.</param>
    /// <param name="width">The frame's width in pixels.</param>
    /// <param name="height">The frame's height in pixels.</param>
    /// <param name="dtJulianYears">Years since J2000 for proper motion (<c>Tycho2ColorCalibration.ComputeDtJulianYears</c>), 0 for none.</param>
    /// <param name="result">The zero point, its scatter and the stars it is over.</param>
    public static bool TryMeasure(StarList stars, WCS wcs, ICelestialObjectDB db, int width, int height, double dtJulianYears,
        out PhotometricZeroPointResult result)
    {
        result = new PhotometricZeroPointResult(float.NaN, float.NaN, 0);
        if (!wcs.HasCDMatrix || stars.Count < MinStars)
        {
            return false;
        }
        var (matches, _) = Tycho2ColorCalibration.MatchStars(stars, wcs, db, MatchRadiusArcsec, MaxMagDiff, dtJulianYears, width, height);
        return TryFromMatches(matches, out result);
    }

    /// <summary><see cref="TryMeasure(StarList, WCS, ICelestialObjectDB, int, int, double, out PhotometricZeroPointResult)"/> over an image's own size and exposure date.</summary>
    public static bool TryMeasure(Image image, StarList stars, WCS wcs, ICelestialObjectDB db, out PhotometricZeroPointResult result)
        => TryMeasure(stars, wcs, db, image.Width, image.Height, Tycho2ColorCalibration.ComputeDtJulianYears(image), out result);
}
