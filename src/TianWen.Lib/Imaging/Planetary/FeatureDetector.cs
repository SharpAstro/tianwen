using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Geometry;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Places alignment points on the highest-contrast surface features (AutoStakkert-style), not on a
/// regular grid -- APs land where there is signal to track. Per the plan's resolved open question this
/// uses the cheap "strongest gradient per cell" detector rather than a Harris corner response: the disk
/// region is divided into cells of <c>spacing</c>, and the pixel of maximum Sobel gradient magnitude in
/// each cell (above a threshold) becomes an AP centre. Detection runs on the luminance proxy, so the same
/// AP set drives all CFA sub-planes. Two thresholds (<see cref="PlanetaryPointPlacement"/>): a fraction of the
/// region's strongest gradient (<see cref="DetectAlignmentPoints"/>), or a multiple of the frame's own noise over
/// the whole planet (<see cref="DetectOverPlanet"/>, #1253).
/// </summary>
public static class FeatureDetector
{
    // A Sobel component is six taps weighted 1, 2, 1 on each side, so white noise of sigma gives it sqrt(12) sigma.
    private static readonly float SobelNoiseGain = MathF.Sqrt(12f);

    /// <summary>
    /// Returns AP centres (in frame coordinates) on the strongest features within <paramref name="region"/>,
    /// at most one per <paramref name="spacing"/> x <paramref name="spacing"/> cell, capped at
    /// <paramref name="maxPoints"/> (strongest first). A cell with no gradient above
    /// <paramref name="minGradientFraction"/> of the region's peak contributes none.
    /// </summary>
    public static ImmutableArray<PixelPoint> DetectAlignmentPoints(Image frame, PixelRect region, int spacing = 24, int maxPoints = 64, double minGradientFraction = 0.2)
        => DetectByPeakFraction(frame, region, spacing, maxPoints, minGradientFraction).Points;

    /// <summary>
    /// <see cref="DetectAlignmentPoints"/> with how many cells qualified before <paramref name="maxPoints"/> capped them.
    /// </summary>
    public static (ImmutableArray<PixelPoint> Points, int Candidates) DetectByPeakFraction(Image frame, PixelRect region, int spacing = 24,
        int maxPoints = 64, double minGradientFraction = 0.2)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfLessThan(spacing, 4);
        if (region.IsEmpty)
        {
            region = LumaProxy.FullFrame(frame);
        }

        var rw = region.Width;
        var rh = region.Height;
        if (rw < 3 || rh < 3)
        {
            return ([], 0);
        }

        using var lumaBuf = ArrayPoolHelper.Rent<float>(rw * rh);
        using var gradBuf = ArrayPoolHelper.Rent<float>(rw * rh);
        var luma = lumaBuf.AsSpan(0, rw * rh);
        var grad = gradBuf.AsSpan(0, rw * rh);
        LumaProxy.Fill(frame, region, luma);
        var maxGrad = Sobel(luma, rw, rh, grad);
        if (maxGrad <= 0f)
        {
            return ([], 0);
        }
        return Cells(grad, rw, rh, region.Left, region.Top, spacing, maxPoints, (float)(minGradientFraction * maxGrad), inside: null);
    }

    /// <summary>
    /// Alignment points over the whole planet (#1253, docs/plans/planetary-restoration.md, "#1195: dense points on the real captures"):
    /// <list type="bullet">
    /// <item>the planet is every pixel above its sky by <paramref name="planetFraction"/> of its peak (the frame's 10th and 99.9th
    /// percentiles), rings included, and a point may sit only where the square <see cref="EdgeMargin"/> px about it lies on the planet, so
    /// none sits on the outer limb, where a patch locks onto the edge and floats along it (a cell's whole square on the planet was too
    /// strict: a split-CFA plane's Saturn, 25 px in radius, kept one point);</item>
    /// <item>a cell keeps its strongest gradient when that stands <paramref name="noiseMultiple"/> times above what the frame's own noise
    /// gives a Sobel gradient (the noise read off the sky's pixel-to-pixel differences), never a fraction of the strongest edge, which on
    /// Saturn is a ring's and left the globe's belts with no point at all (3 to 13 points on two captures).</item>
    /// </list>
    /// Strongest first, capped at <paramref name="maxPoints"/>; <c>Candidates</c> is how many cells qualified before the cap.
    /// </summary>
    public static (ImmutableArray<PixelPoint> Points, int Candidates) DetectOverPlanet(Image frame, int spacing = 24, int maxPoints = 64,
        double noiseMultiple = 8, double planetFraction = 0.08)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfLessThan(spacing, 4);
        var (w, h) = (frame.Width, frame.Height);
        if (w < 3 || h < 3)
        {
            return ([], 0);
        }
        var n = w * h;
        using var lumaBuf = ArrayPoolHelper.Rent<float>(n);
        using var gradBuf = ArrayPoolHelper.Rent<float>(n);
        var luma = lumaBuf.AsSpan(0, n);
        var grad = gradBuf.AsSpan(0, n);
        LumaProxy.Fill(frame, LumaProxy.FullFrame(frame), luma);

        var ranked = luma.ToArray();
        var sky = StatisticsHelper.PercentileFast(ranked, 0.10);
        var peak = StatisticsHelper.PercentileFast(ranked, 0.999);
        if (!(peak > sky))
        {
            return ([], 0);
        }
        var level = sky + (float)(planetFraction * (peak - sky));

        // The planet's pixels, and an integral image of them, so a point is clear of the edge when the square about it is on the planet.
        var onPlanet = new int[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            var rowSum = 0;
            for (var x = 0; x < w; x++)
            {
                rowSum += luma[(y * w) + x] > level ? 1 : 0;
                onPlanet[((y + 1) * (w + 1)) + x + 1] = onPlanet[(y * (w + 1)) + x + 1] + rowSum;
            }
        }

        var noise = SkyNoise(luma, w, h, level);
        var maxGrad = Sobel(luma, w, h, grad);
        if (maxGrad <= 0f)
        {
            return ([], 0);
        }
        // A noiseless frame (a render) has no noise to stand above; a hundredth of the strongest edge stands in.
        var threshold = Math.Max((float)(noiseMultiple * SobelNoiseGain * noise), 0.01f * maxGrad);
        return Cells(grad, w, h, 0, 0, spacing, maxPoints, threshold, inside: (x, y) =>
        {
            var (x0, y0, x1, y1) = (x - EdgeMargin, y - EdgeMargin, x + EdgeMargin + 1, y + EdgeMargin + 1);
            if (x0 < 0 || y0 < 0 || x1 > w || y1 > h)
            {
                return false;
            }
            var covered = onPlanet[(y1 * (w + 1)) + x1] - onPlanet[(y0 * (w + 1)) + x1] - onPlanet[(y1 * (w + 1)) + x0] + onPlanet[(y0 * (w + 1)) + x0];
            return covered == (x1 - x0) * (y1 - y0);
        });
    }

    /// <summary>How far, px, a point placed over the planet stays inside its outer edge (<see cref="DetectOverPlanet"/>).</summary>
    public const int EdgeMargin = 4;

    // The frame's noise, sigma: the spread of neighbouring sky pixels' differences (the robust MAD, over the square root of two), so a
    // slope across the sky adds nothing. Without sky to read, every pair stands in.
    private static double SkyNoise(ReadOnlySpan<float> luma, int w, int h, float level)
    {
        var differences = new List<float>();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x + 1 < w; x++)
            {
                var (a, b) = (luma[(y * w) + x], luma[(y * w) + x + 1]);
                if (a < level && b < level)
                {
                    differences.Add(a - b);
                }
            }
        }
        if (differences.Count < 500)
        {
            differences.Clear();
            for (var i = 0; i + 1 < luma.Length; i++)
            {
                differences.Add(luma[i] - luma[i + 1]);
            }
        }
        var absolute = new float[differences.Count];
        for (var i = 0; i < absolute.Length; i++)
        {
            absolute[i] = MathF.Abs(differences[i]);
        }
        return absolute.Length == 0 ? 0 : 1.4826 * StatisticsHelper.MedianFast(absolute) / Math.Sqrt(2);
    }

    // The Sobel gradient magnitude of each interior pixel into `grad` (the border left zero); its largest.
    private static float Sobel(ReadOnlySpan<float> luma, int w, int h, Span<float> grad)
    {
        grad.Clear();
        var maxGrad = 0f;
        for (var y = 1; y < h - 1; y++)
        {
            var row = y * w;
            var up = row - w;
            var dn = row + w;
            for (var x = 1; x < w - 1; x++)
            {
                var gx = (luma[up + x + 1] + (2f * luma[row + x + 1]) + luma[dn + x + 1])
                       - (luma[up + x - 1] + (2f * luma[row + x - 1]) + luma[dn + x - 1]);
                var gy = (luma[dn + x - 1] + (2f * luma[dn + x]) + luma[dn + x + 1])
                       - (luma[up + x - 1] + (2f * luma[up + x]) + luma[up + x + 1]);
                var g = MathF.Sqrt((gx * gx) + (gy * gy));
                grad[row + x] = g;
                if (g > maxGrad)
                {
                    maxGrad = g;
                }
            }
        }
        return maxGrad;
    }

    // One point a cell at its strongest gradient above `threshold` among the pixels `inside` admits (all without it), strongest first,
    // capped; with how many cells qualified. `left`/`top` place the gradient plane's origin in the frame.
    private static (ImmutableArray<PixelPoint> Points, int Candidates) Cells(ReadOnlySpan<float> grad, int rw, int rh, int left, int top,
        int spacing, int maxPoints, float threshold, Func<int, int, bool>? inside)
    {
        var candidates = ImmutableArray.CreateBuilder<(float Score, PixelPoint P)>();
        for (var cy = 0; cy < rh; cy += spacing)
        {
            for (var cx = 0; cx < rw; cx += spacing)
            {
                var yEnd = Math.Min(cy + spacing, rh);
                var xEnd = Math.Min(cx + spacing, rw);
                var bestScore = threshold;
                var bestX = -1;
                var bestY = -1;
                for (var y = cy; y < yEnd; y++)
                {
                    var row = y * rw;
                    for (var x = cx; x < xEnd; x++)
                    {
                        var g = grad[row + x];
                        if (g > bestScore && (inside is null || inside(x, y)))
                        {
                            bestScore = g;
                            bestX = x;
                            bestY = y;
                        }
                    }
                }

                if (bestX >= 0)
                {
                    candidates.Add((bestScore, new PixelPoint(left + bestX, top + bestY)));
                }
            }
        }

        candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        var keep = Math.Min(maxPoints, candidates.Count);
        var points = ImmutableArray.CreateBuilder<PixelPoint>(keep);
        for (var i = 0; i < keep; i++)
        {
            points.Add(candidates[i].P);
        }

        return (points.MoveToImmutable(), candidates.Count);
    }
}
