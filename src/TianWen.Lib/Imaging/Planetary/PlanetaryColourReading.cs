using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A planet's colour as the eye reads it (#1273), in OKLab: the <paramref name="Cast"/> (the interior's mean colour, whose chroma is how
/// strongly the whole planet is tinted), the <paramref name="Spread"/> (the RMS of each interior pixel's a, b about that mean, its colour
/// contrast: belts against zones, the rings against the globe), and the <paramref name="Rim"/> (the mean colour of the band about the
/// planet's outline, where a colour fringe or a saturation pushed past it shows).
/// </summary>
public readonly record struct ColourReading(OkLab Cast, double Spread, OkLab Rim, int InteriorPixels, int RimPixels)
{
    /// <summary>
    /// The rim's colour off the planet's hue, pixel by pixel: the 90th percentile, over the rim's visible pixels (at least
    /// <see cref="PlanetaryColourReading.RimVisibleLuminance"/> of the interior's luminance), of the part of each one's a, b not along the cast's
    /// direction (with any part opposite it), in OKLab chroma. A rim's MEAN colour averages a fringe on one side of the limb with the
    /// planet's own colour elsewhere (a cyan rim read 20 degrees off, #1273); a fringe anywhere lifts this. Absolute, never over the cast's
    /// chroma, which on Jupiter's faint cast turned a bluish limb into five times its cast.
    /// </summary>
    public double RimOffHue { get; init; } = double.NaN;

    /// <summary>The interior's mean linear luminance, its sky off: what every pixel was divided by before it was read.</summary>
    public double Luminance { get; init; } = double.NaN;

    /// <summary>
    /// Each lit interior pixel's OKLab chroma about grey, ascending (at <see cref="PlanetaryColourReading.LitLuminance"/> of the interior's
    /// luminance or more): what tells white zones beside orange belts from blue zones beside orange belts, which the <see cref="Spread"/> about
    /// the mean cannot (#1273's first look raised the spread as asked and turned the zones blue).
    /// </summary>
    public ImmutableArray<float> InteriorChroma { get; init; } = [];

    /// <summary>The interior's chroma about grey at quantile <paramref name="q"/> (0 to 1), read between the sorted pixels; NaN with none.</summary>
    public double ChromaAt(double q)
    {
        var sorted = InteriorChroma;
        if (sorted.IsDefaultOrEmpty)
        {
            return double.NaN;
        }
        var at = Math.Clamp(q, 0, 1) * (sorted.Length - 1);
        var k = Math.Min((int)at, sorted.Length - 2);
        return k < 0 ? sorted[0] : sorted[k] + ((at - k) * (sorted[k + 1] - sorted[k]));
    }

    /// <summary>The rim's chroma over the cast's: a rim tinted no more than the planet reads under one.</summary>
    public double RimOverCast => Rim.Chroma / Cast.Chroma;

    /// <summary>How far the rim's hue lies from the cast's, degrees: a cyan rim on a yellow planet reads near 180.</summary>
    public double RimHueOffsetDeg => OkLab.HueDistanceDeg(Cast.HueDeg, Rim.HueDeg);

    /// <summary>
    /// The reading in words, for a verb's report: the interior's cast, spread and chroma quantiles, and the rim's, for <c>planetary-judge</c>
    /// and <c>planetary-look</c> alike (the audit on #1343 found the line written twice).
    /// </summary>
    public string Describe()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"(OKLab at one luminance, {InteriorPixels} px inside 0.9 of the outline): cast chroma {Cast.Chroma:0.0000} at hue {Cast.HueDeg:0.0} deg, spread {Spread:0.0000}, chroma p10/p50/p90 {ChromaAt(0.10):0.0000}/{ChromaAt(0.50):0.0000}/{ChromaAt(0.90):0.0000}; the rim ({RimPixels} px, 0.9 to 1.1) chroma {Rim.Chroma:0.0000}, {RimOverCast:0.00} of the cast's, hue {Rim.HueDeg:0.0} deg ({RimHueOffsetDeg:0.0} from the cast's); off the cast's hue, pixel by pixel, chroma {RimOffHue:0.0000} (p90)");
    }
}

/// <summary>
/// Reads a planet's colour in OKLab (#1273, docs/plans/planetary-restoration.md): the interior is the planet inside
/// <see cref="InteriorOutline"/> of its outline (<see cref="MetricDisk.ClearRadiiAt"/>, Saturn's rings with its globe), the rim the band
/// from there to <see cref="RimOutline"/> (the limb, the rings' edges, and the gaps between the rings and the globe). Every pixel is taken
/// with its sky off and divided by the interior's mean luminance, so two pictures of one planet at different levels read alike: OKLab's
/// a, b grow as the cube root of a colour's level.
/// </summary>
public static class PlanetaryColourReading
{
    /// <summary>The interior lies inside this fraction of the planet's outline, clear of the limb's blur and any fringe.</summary>
    public const double InteriorOutline = 0.9;

    /// <summary>
    /// The quantiles a chroma distribution is compared and mapped at (#1273): the 1st and 99th percentiles at the ends, so an outlier pixel
    /// never sets the curve, and every 5th between.
    /// </summary>
    public static ImmutableArray<double> QuantileGrid { get; } =
        [0.01, 0.05, 0.10, 0.15, 0.20, 0.25, 0.30, 0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.90, 0.95, 0.99];

    /// <summary>The rim runs from <see cref="InteriorOutline"/> out to this fraction of the outline.</summary>
    public const double RimOutline = 1.1;

    /// <summary>
    /// A rim pixel counts toward <see cref="ColourReading.RimOffHue"/> only at this fraction of the interior's mean luminance or more: OKLab's
    /// cube root makes a near-black pixel's noise read as colour, and a fringe too faint to see is no fringe.
    /// </summary>
    public const double RimVisibleLuminance = 0.1;

    /// <summary>
    /// An interior pixel counts toward <see cref="ColourReading.InteriorChroma"/> only at this fraction of the interior's mean luminance or
    /// more: a ring gap's or a shadow's chroma is the cube root's noise, and a look leaves such a pixel as it was, so a distribution holding it
    /// would place every lit pixel's quantile by pixels the look never moves.
    /// </summary>
    public const double LitLuminance = 0.2;

    /// <summary>The reading of a linear RGB picture of the planet <paramref name="disk"/> describes, each channel's <paramref name="sky"/> taken off.</summary>
    public static ColourReading Read(ReadOnlySpan<float> red, ReadOnlySpan<float> green, ReadOnlySpan<float> blue, int width, int height,
        in MetricDisk disk, in LinearRgb sky)
    {
        var y = CameraColorMatrix.SrgbToXyz.Slice(3, 3);
        var (wr, wg, wb) = (y[0], y[1], y[2]);
        var interior = new List<int>();
        var rim = new List<int>();
        for (var row = 0; row < height; row++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (row * width) + x;
                if (!float.IsFinite(red[i]) || !float.IsFinite(green[i]) || !float.IsFinite(blue[i]))
                {
                    continue;
                }
                var outline = disk.ClearRadiiAt(x, row);
                if (outline < InteriorOutline)
                {
                    interior.Add(i);
                }
                else if (outline < RimOutline)
                {
                    rim.Add(i);
                }
            }
        }
        double luminance = 0;
        foreach (var i in interior)
        {
            luminance += (wr * (red[i] - sky.R)) + (wg * (green[i] - sky.G)) + (wb * (blue[i] - sky.B));
        }
        luminance /= Math.Max(1, interior.Count);
        if (interior.Count == 0 || !(luminance > 0))
        {
            var none = new OkLab(double.NaN, double.NaN, double.NaN);
            return new ColourReading(none, double.NaN, none, interior.Count, rim.Count);
        }

        var at = new OkLab[interior.Count];
        double sumL = 0, sumA = 0, sumB = 0;
        for (var k = 0; k < interior.Count; k++)
        {
            at[k] = Of(red, green, blue, interior[k], sky, luminance);
            (sumL, sumA, sumB) = (sumL + at[k].L, sumA + at[k].A, sumB + at[k].B);
        }
        var cast = new OkLab(sumL / at.Length, sumA / at.Length, sumB / at.Length);
        var lit = new List<float>(at.Length);
        for (var k = 0; k < at.Length; k++)
        {
            var i = interior[k];
            if (((wr * (red[i] - sky.R)) + (wg * (green[i] - sky.G)) + (wb * (blue[i] - sky.B))) / luminance >= LitLuminance)
            {
                lit.Add((float)at[k].Chroma);
            }
        }
        var chroma = lit.ToArray();
        Array.Sort(chroma);
        double squares = 0;
        foreach (var c in at)
        {
            var (da, db) = (c.A - cast.A, c.B - cast.B);
            squares += (da * da) + (db * db);
        }
        double rimL = 0, rimA = 0, rimB = 0;
        var (ua, ub) = (cast.A / cast.Chroma, cast.B / cast.Chroma);
        var offHue = new List<double>(rim.Count);
        foreach (var i in rim)
        {
            var c = Of(red, green, blue, i, sky, luminance);
            (rimL, rimA, rimB) = (rimL + c.L, rimA + c.A, rimB + c.B);
            var visible = ((wr * (red[i] - sky.R)) + (wg * (green[i] - sky.G)) + (wb * (blue[i] - sky.B))) / luminance;
            if (visible >= RimVisibleLuminance)
            {
                // The part along the cast's direction, the part across it, and any part opposite it.
                var along = (c.A * ua) + (c.B * ub);
                var (pa, pb) = (c.A - (along * ua), c.B - (along * ub));
                var opposite = Math.Min(along, 0);
                offHue.Add(Math.Sqrt((pa * pa) + (pb * pb) + (opposite * opposite)));
            }
        }
        var rimMean = rim.Count == 0
            ? new OkLab(double.NaN, double.NaN, double.NaN)
            : new OkLab(rimL / rim.Count, rimA / rim.Count, rimB / rim.Count);
        var p90 = double.NaN;
        if (offHue.Count > 0)
        {
            offHue.Sort();
            p90 = offHue[(int)Math.Min(offHue.Count - 1, Math.Floor(0.9 * offHue.Count))];
        }
        return new ColourReading(cast, Math.Sqrt(squares / at.Length), rimMean, interior.Count, rim.Count)
        {
            RimOffHue = p90,
            Luminance = luminance,
            InteriorChroma = ImmutableArray.Create(chroma),
        };

        static OkLab Of(ReadOnlySpan<float> red, ReadOnlySpan<float> green, ReadOnlySpan<float> blue, int i, in LinearRgb sky, double luminance)
            => OkLab.FromLinearSrgb((red[i] - sky.R) / luminance, (green[i] - sky.G) / luminance, (blue[i] - sky.B) / luminance);
    }

    /// <summary>
    /// <paramref name="red"/>, <paramref name="green"/> and <paramref name="blue"/> (another picture's, on the master's grid) with their
    /// luminance, the channels' mean, taken to <paramref name="luminance"/> pixel by pixel and their chromaticity kept: a reference's tone
    /// matched to the master's (<see cref="PlanetaryReferenceJudge.MatchTone"/>) without touching its colour. NaN where either is not finite.
    /// </summary>
    public static (float[] R, float[] G, float[] B) WithLuminance(ReadOnlySpan<float> red, ReadOnlySpan<float> green, ReadOnlySpan<float> blue,
        ReadOnlySpan<float> luminance)
    {
        var (r, g, b) = (new float[red.Length], new float[red.Length], new float[red.Length]);
        for (var i = 0; i < red.Length; i++)
        {
            var own = (red[i] + green[i] + blue[i]) / 3;
            var scale = own > 0 && float.IsFinite(luminance[i]) ? luminance[i] / own : float.NaN;
            (r[i], g[i], b[i]) = (red[i] * scale, green[i] * scale, blue[i] * scale);
        }
        return (r, g, b);
    }
}
