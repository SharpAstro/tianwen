using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TianWen.Lib.Stat;
using TianWen.Lib.Imaging.Sources;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>A point source the finder reported: a local maximum of the matched-filtered plane.</summary>
/// <param name="X">Sub-pixel centre, 0-based, pixel centres at integers (the <see cref="ImagedStar"/> convention).</param>
/// <param name="Y">Sub-pixel centre.</param>
/// <param name="PeakX">The integer pixel of the maximum.</param>
/// <param name="PeakY">The integer pixel of the maximum.</param>
/// <param name="Significance">The filtered plane's value at the maximum, in its own robust noise.</param>
/// <param name="Peak">The plane above its local sky at the maximum pixel, in the plane's units.</param>
internal readonly record struct PointSource(float X, float Y, int PeakX, int PeakY, float Significance, float Peak);

/// <summary>
/// Finds every point source in a plane, DAOFIND's way: a local sky from <see cref="BackgroundMap"/> at a block of
/// about 8 FWHM (so it follows nebulosity, and a star on a nebula stands above its own patch of it), a Gaussian
/// matched filter at the PSF's width, and the filtered plane's local maxima above a threshold in its own robust
/// noise, merged within one FWHM, brightest first.
/// </summary>
/// <remarks>
/// <para>It exists because neither detector already here does this job. <c>FindStarsAsync</c> selects stars to
/// MEASURE: it returns nothing over a background at or below zero, skips 15 px at every edge and refuses an HFD over
/// 28, which is every big saturated star. <see cref="SourceSegmentation"/> segments SOURCES: 64 peaks to a segment and
/// a crowded field re-thresholded higher lose the faint stars on a nebula. A star remover has to see every point
/// source, the saturated and the faint and those at the edge, and decide later (by fitting) which are stars.</para>
/// <para>The noise of the filtered plane is measured, not derived: a master's noise is correlated (warp and demosaic
/// or drizzle), so the white-noise reduction of a Gaussian filter would overstate the significance. The ratio of the
/// filtered plane to the sky's rms map is scaled by its own MAD, capped by the noise the plane's differences predict
/// for a correlated noise (<see cref="PredictedFilteredNoise"/>): in a crowded field the MAD is the confusion of faint
/// stars, which the differences barely see.</para>
/// </remarks>
internal static class PointSourceFinder
{
    /// <summary>The block of the local sky for a PSF of <paramref name="fwhm"/> pixels.</summary>
    public static int SkyBlockFor(double fwhm) => Math.Clamp((int)Math.Round(8.0 * fwhm), 16, 64);

    /// <summary>
    /// Finds the point sources of <paramref name="plane"/> (row-major, <paramref name="width"/> x <paramref name="height"/>)
    /// at <paramref name="thresholdSigma"/>, brightest first. Pixels in <paramref name="absent"/> take no part.
    /// </summary>
    public static (PointSource[] Sources, BackgroundMap Sky) Find(
        ReadOnlySpan<float> plane, int width, int height, BitMatrix? absent, double fwhm, float thresholdSigma)
    {
        var sky = BackgroundMap.Estimate(plane, width, height, absent, new BackgroundMapOptions(BlockSize: SkyBlockFor(fwhm)));
        var n = width * height;
        var skyLevel = new float[n];
        var rms = new float[n];
        sky.FillBackground(skyLevel);
        sky.FillRms(rms);

        var above = new float[n];
        for (var i = 0; i < n; i++)
        {
            var v = plane[i];
            above[i] = float.IsFinite(v) && !IsAbsent(absent, i, width) ? v - skyLevel[i] : 0f;
        }

        var sigma = (float)Math.Max(0.5, fwhm / 2.3548);
        var filtered = Image.SeparableGaussianBlur(above, width, height, sigma);
        for (var i = 0; i < n; i++)
        {
            filtered[i] = rms[i] > 0f ? filtered[i] / rms[i] : 0f;
        }
        var noise = RobustSigma(filtered, absent, width);
        // In a crowded field the filtered plane's spread is the faint stars themselves, not noise (eta Car's Milky Way
        // read 107 to 149 where its noise predicts 47), and every star under that confusion was missed. The noise the
        // differences predict is blind to it, so it caps the measured one; on a sparse field the two agree within 8 %.
        var predicted = PredictedFilteredNoise(plane, width, height, absent, sigma);
        var typicalRms = TypicalRms(rms, absent, width);
        if (predicted > 0 && typicalRms > 0f)
        {
            noise = Math.Min(noise, (float)(ConfusionSafety * predicted / typicalRms));
        }
        if (!(noise > 0f))
        {
            return (Array.Empty<PointSource>(), sky);
        }
        for (var i = 0; i < n; i++)
        {
            filtered[i] /= noise;
        }

        var found = new List<PointSource>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var z = filtered[i];
                if (!(z >= thresholdSigma) || IsAbsent(absent, i, width) || !IsLocalMaximum(filtered, width, height, x, y, z))
                {
                    continue;
                }
                var (sx, sy) = SubPixel(filtered, width, height, x, y);
                found.Add(new PointSource(x + sx, y + sy, x, y, z, above[i]));
            }
        }
        found.Sort(static (a, b) => b.Significance.CompareTo(a.Significance));
        return (Merge(found, Math.Max(1.5, fwhm), width, height), sky);
    }

    /// <summary>The margin on <see cref="PredictedFilteredNoise"/>, which reads a sparse field's filtered noise 5 to 8 % low.</summary>
    internal const double ConfusionSafety = 1.15;

    private static bool IsAbsent(BitMatrix? absent, int index, int width)
        => absent is { } mask && mask[index / width, index % width];

    /// <summary>
    /// The noise a Gaussian filter of <paramref name="filterSigma"/> pixels leaves on <paramref name="plane"/>, from
    /// pixel differences alone: the noise taken as white noise through a Gaussian of width s (a master's warp and
    /// demosaic), whose lag-1 and lag-2 difference spreads fix s and the pixel sigma, after which the filter's output
    /// noise is sigma s / sqrt(filterSigma^2 + s^2). Differences see a star only on its steep core, so a crowded field's
    /// confusion, which the filtered plane's own spread counts as noise, barely reaches it. NaN when the two spreads do
    /// not fit the model.
    /// </summary>
    internal static double PredictedFilteredNoise(ReadOnlySpan<float> plane, int width, int height, BitMatrix? absent, double filterSigma)
    {
        var (pixelSigma, correlationWidth) = DifferenceNoise(plane, width, height, absent);
        return pixelSigma * correlationWidth / Math.Sqrt(filterSigma * filterSigma + correlationWidth * correlationWidth);
    }

    /// <summary>
    /// The pixel noise of <paramref name="plane"/> and its correlation width, from the spreads of its lag-1 and lag-2
    /// differences under the model of <see cref="PredictedFilteredNoise"/>; NaN for both when they do not fit it.
    /// </summary>
    internal static (double PixelSigma, double CorrelationWidth) DifferenceNoise(ReadOnlySpan<float> plane, int width, int height, BitMatrix? absent)
    {
        var lag1 = new List<float>();
        var lag2 = new List<float>();
        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x + 2 < width; x++)
            {
                var i = y * width + x;
                float a = plane[i], b = plane[i + 1], c = plane[i + 2];
                if (!float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(c)
                    || IsAbsent(absent, i, width) || IsAbsent(absent, i + 1, width) || IsAbsent(absent, i + 2, width))
                {
                    continue;
                }
                lag1.Add(b - a);
                lag2.Add(c - a);
            }
        }
        if (lag1.Count < 1000)
        {
            return (double.NaN, double.NaN);
        }
        var d1 = RobustSigma(lag1.ToArray(), null, 1);
        var d2 = RobustSigma(lag2.ToArray(), null, 1);
        if (!(d1 > 0f) || !(d2 > 0f))
        {
            return (double.NaN, double.NaN);
        }
        var q = (double)d2 * d2 / ((double)d1 * d1);
        static double Rho(double k, double s) => Math.Exp(-k * k / (4.0 * s * s));
        static double Ratio(double s) => (1.0 - Rho(2, s)) / (1.0 - Rho(1, s));
        double lo = 0.05, hi = 20.0;
        if (!(q > Ratio(lo)) || !(q < Ratio(hi)))
        {
            return (double.NaN, double.NaN);
        }
        for (var it = 0; it < 60; it++)
        {
            var mid = 0.5 * (lo + hi);
            if (Ratio(mid) < q)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        var width0 = 0.5 * (lo + hi);
        return (Math.Sqrt((double)d1 * d1 / (2.0 * (1.0 - Rho(1, width0)))), width0);
    }

    /// <summary>
    /// Caps <paramref name="rms"/> (a background map's spread) pixel by pixel at <see cref="ConfusionSafety"/> times the noise
    /// <paramref name="plane"/>'s own differences give (<see cref="DifferenceNoiseMap"/>), in place: a map's spread counts a
    /// crowded field's faint stars and a bright nebula's texture as noise (over the Orion master's M42 core 1.6 times the
    /// pixels' own), and a test against it is blind there by as much. One rule for the fill's grain and margin, the hole
    /// tests and the speckle measure. Left as it is where the differences do not fit the model.
    /// </summary>
    internal static void CapByDifferenceNoise(float[] rms, ReadOnlySpan<float> plane, int width, int height, BitMatrix? absent, int block)
    {
        if (DifferenceNoiseMap(plane, width, height, absent, block) is not { } local)
        {
            return;
        }
        for (var i = 0; i < rms.Length; i++)
        {
            var cap = (float)(ConfusionSafety * local[i]);
            if (cap > 0f && cap < rms[i])
            {
                rms[i] = cap;
            }
        }
    }

    /// <summary>
    /// The pixel noise of <paramref name="plane"/> per cell of <paramref name="block"/> pixels, interpolated between
    /// cell centres: each cell's lag-1 difference spread, scaled by the correlation width the whole plane's differences
    /// give (<see cref="DifferenceNoise"/>). Blind, as differences are, to the structure and the confusion a cell's own
    /// spread counts as noise. Null when the plane's differences do not fit the model.
    /// </summary>
    internal static float[]? DifferenceNoiseMap(ReadOnlySpan<float> plane, int width, int height, BitMatrix? absent, int block)
    {
        var (pixelSigma, correlationWidth) = DifferenceNoise(plane, width, height, absent);
        if (!(pixelSigma > 0) || !(correlationWidth > 0))
        {
            return null;
        }
        var scale = 1.0 / Math.Sqrt(2.0 * (1.0 - Math.Exp(-1.0 / (4.0 * correlationWidth * correlationWidth))));
        var cellsX = (width + block - 1) / block;
        var cellsY = (height + block - 1) / block;
        var cells = new float[cellsX * cellsY];
        var diffs = new List<float>(2 * block * block);
        for (var cy = 0; cy < cellsY; cy++)
        {
            for (var cx = 0; cx < cellsX; cx++)
            {
                diffs.Clear();
                for (var y = cy * block; y < Math.Min(height, (cy + 1) * block); y++)
                {
                    for (var x = cx * block; x < Math.Min(width, (cx + 1) * block); x++)
                    {
                        var i = y * width + x;
                        if (IsAbsent(absent, i, width) || !float.IsFinite(plane[i]))
                        {
                            continue;
                        }
                        if (x + 1 < width && !IsAbsent(absent, i + 1, width) && float.IsFinite(plane[i + 1]))
                        {
                            diffs.Add(plane[i + 1] - plane[i]);
                        }
                        if (y + 1 < height && !IsAbsent(absent, i + width, width) && float.IsFinite(plane[i + width]))
                        {
                            diffs.Add(plane[i + width] - plane[i]);
                        }
                    }
                }
                cells[cy * cellsX + cx] = diffs.Count >= 32 ? (float)(scale * RobustSigmaOf(diffs)) : float.NaN;
            }
        }
        // A cell with too few present pixels takes the whole plane's value.
        for (var k = 0; k < cells.Length; k++)
        {
            if (!(cells[k] > 0f))
            {
                cells[k] = (float)pixelSigma;
            }
        }
        var map = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            var fy = Math.Clamp((y + 0.5) / block - 0.5, 0.0, cellsY - 1.0);
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, cellsY - 1);
            var ty = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp((x + 0.5) / block - 0.5, 0.0, cellsX - 1.0);
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, cellsX - 1);
                var tx = fx - x0;
                map[y * width + x] = (float)(
                    (1 - ty) * ((1 - tx) * cells[y0 * cellsX + x0] + tx * cells[y0 * cellsX + x1])
                    + ty * ((1 - tx) * cells[y1 * cellsX + x0] + tx * cells[y1 * cellsX + x1]));
            }
        }
        return map;
    }

    // 1.4826 x the MAD about the median of every value (RobustSigma's estimator on a short list).
    private static double RobustSigmaOf(List<float> values)
    {
        return 1.4826 * StatisticsHelper.UpperMedianAndMad(CollectionsMarshal.AsSpan(values)).Mad;
    }

    // The median of the rms map over present pixels, every fourth.
    internal static float TypicalRms(float[] rms, BitMatrix? absent, int width)
    {
        var sample = new List<float>(rms.Length / 4 + 1);
        for (var i = 0; i < rms.Length; i += 4)
        {
            if (!IsAbsent(absent, i, width) && rms[i] > 0f)
            {
                sample.Add(rms[i]);
            }
        }
        if (sample.Count == 0)
        {
            return float.NaN;
        }
        return StatisticsHelper.NthSmallest(CollectionsMarshal.AsSpan(sample), sample.Count / 2);
    }

    // A maximum over its eight neighbours; ties are broken by raster order, so a plateau yields exactly one.
    private static bool IsLocalMaximum(float[] z, int width, int height, int x, int y, float value)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            var yy = y + dy;
            if (yy < 0 || yy >= height)
            {
                continue;
            }
            for (var dx = -1; dx <= 1; dx++)
            {
                var xx = x + dx;
                if ((dx == 0 && dy == 0) || xx < 0 || xx >= width)
                {
                    continue;
                }
                var other = z[yy * width + xx];
                var earlier = dy < 0 || (dy == 0 && dx < 0);
                if (other > value || (earlier && other == value))
                {
                    return false;
                }
            }
        }
        return true;
    }

    // A parabola through the maximum and its two neighbours on each axis.
    private static (float Dx, float Dy) SubPixel(float[] z, int width, int height, int x, int y)
    {
        static float Vertex(float left, float centre, float right)
        {
            var curvature = left - 2f * centre + right;
            return curvature < 0f ? Math.Clamp(0.5f * (left - right) / curvature, -0.5f, 0.5f) : 0f;
        }
        var c = z[y * width + x];
        var dx = x > 0 && x < width - 1 ? Vertex(z[y * width + x - 1], c, z[y * width + x + 1]) : 0f;
        var dy = y > 0 && y < height - 1 ? Vertex(z[(y - 1) * width + x], c, z[(y + 1) * width + x]) : 0f;
        return (dx, dy);
    }

    // 1.4826 x the MAD about the median, over every fourth present pixel.
    internal static float RobustSigma(float[] values, BitMatrix? absent, int width)
    {
        var sample = new List<float>(values.Length / 4 + 1);
        for (var i = 0; i < values.Length; i += 4)
        {
            if (!IsAbsent(absent, i, width) && float.IsFinite(values[i]))
            {
                sample.Add(values[i]);
            }
        }
        if (sample.Count < 16)
        {
            return float.NaN;
        }
        return 1.4826f * StatisticsHelper.UpperMedianAndMad(CollectionsMarshal.AsSpan(sample)).Mad;
    }

    // Brightest first; a source within the radius of one already kept is the same source.
    private static PointSource[] Merge(List<PointSource> sorted, double radius, int width, int height)
    {
        var cell = Math.Max(1, (int)Math.Ceiling(radius));
        var cellsX = width / cell + 1;
        var grid = new Dictionary<int, List<int>>();
        var kept = new List<PointSource>(sorted.Count);
        var r2 = radius * radius;
        foreach (var s in sorted)
        {
            var cx = (int)(s.X / cell);
            var cy = (int)(s.Y / cell);
            var duplicate = false;
            for (var gy = cy - 1; gy <= cy + 1 && !duplicate; gy++)
            {
                for (var gx = cx - 1; gx <= cx + 1 && !duplicate; gx++)
                {
                    if (!grid.TryGetValue(gy * cellsX + gx, out var members))
                    {
                        continue;
                    }
                    foreach (var m in members)
                    {
                        var dx = kept[m].X - s.X;
                        var dy = kept[m].Y - s.Y;
                        if (dx * dx + dy * dy < r2)
                        {
                            duplicate = true;
                            break;
                        }
                    }
                }
            }
            if (duplicate)
            {
                continue;
            }
            var key = cy * cellsX + cx;
            if (!grid.TryGetValue(key, out var list))
            {
                list = new List<int>();
                grid[key] = list;
            }
            list.Add(kept.Count);
            kept.Add(s);
        }
        return kept.ToArray();
    }
}
