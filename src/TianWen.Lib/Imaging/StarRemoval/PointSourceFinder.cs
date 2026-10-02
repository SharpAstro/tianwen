using System;
using System.Collections.Generic;
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
/// filtered plane to the sky's rms map is scaled by its own MAD.</para>
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

    private static bool IsAbsent(BitMatrix? absent, int index, int width)
        => absent is { } mask && mask[index / width, index % width];

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
        var arr = sample.ToArray();
        Array.Sort(arr);
        var median = arr[arr.Length / 2];
        for (var i = 0; i < arr.Length; i++)
        {
            arr[i] = Math.Abs(arr[i] - median);
        }
        Array.Sort(arr);
        return 1.4826f * arr[arr.Length / 2];
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
