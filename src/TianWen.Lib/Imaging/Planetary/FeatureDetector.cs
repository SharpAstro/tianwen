using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Drawing;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Places alignment points on the highest-contrast surface features (AutoStakkert-style), not on a
/// regular grid -- APs land where there is signal to track. Per the plan's resolved open question this
/// uses the cheap "strongest gradient per cell" detector rather than a Harris corner response: the disk
/// region is divided into cells of <c>spacing</c>, and the pixel of maximum Sobel gradient magnitude in
/// each cell (above a fraction of the region's peak gradient) becomes an AP centre. Detection runs on the
/// luminance proxy, so the same AP set drives all CFA sub-planes.
/// </summary>
public static class FeatureDetector
{
    /// <summary>
    /// Returns AP centres (in frame coordinates) on the strongest features within <paramref name="region"/>,
    /// at most one per <paramref name="spacing"/> x <paramref name="spacing"/> cell, capped at
    /// <paramref name="maxPoints"/> (strongest first). A cell with no gradient above
    /// <paramref name="minGradientFraction"/> of the region's peak contributes none.
    /// </summary>
    public static ImmutableArray<Point> DetectAlignmentPoints(Image frame, Rectangle region, int spacing = 24, int maxPoints = 64, double minGradientFraction = 0.2)
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
            return ImmutableArray<Point>.Empty;
        }

        var lumaBuf = ArrayPool<float>.Shared.Rent(rw * rh);
        var gradBuf = ArrayPool<float>.Shared.Rent(rw * rh);
        try
        {
            var luma = lumaBuf.AsSpan(0, rw * rh);
            var grad = gradBuf.AsSpan(0, rw * rh);
            grad.Clear();
            LumaProxy.Fill(frame, region, luma);

            var maxGrad = 0f;
            for (var y = 1; y < rh - 1; y++)
            {
                var row = y * rw;
                var up = row - rw;
                var dn = row + rw;
                for (var x = 1; x < rw - 1; x++)
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

            if (maxGrad <= 0f)
            {
                return ImmutableArray<Point>.Empty;
            }

            var threshold = (float)(minGradientFraction * maxGrad);
            var candidates = ImmutableArray.CreateBuilder<(float Score, Point P)>();
            for (var cy = 0; cy < rh; cy += spacing)
            {
                for (var cx = 0; cx < rw; cx += spacing)
                {
                    var bestScore = threshold;
                    var bestX = -1;
                    var bestY = -1;
                    var yEnd = Math.Min(cy + spacing, rh);
                    var xEnd = Math.Min(cx + spacing, rw);
                    for (var y = cy; y < yEnd; y++)
                    {
                        var row = y * rw;
                        for (var x = cx; x < xEnd; x++)
                        {
                            var g = grad[row + x];
                            if (g > bestScore)
                            {
                                bestScore = g;
                                bestX = x;
                                bestY = y;
                            }
                        }
                    }

                    if (bestX >= 0)
                    {
                        candidates.Add((bestScore, new Point(region.Left + bestX, region.Top + bestY)));
                    }
                }
            }

            candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            var keep = Math.Min(maxPoints, candidates.Count);
            var points = ImmutableArray.CreateBuilder<Point>(keep);
            for (var i = 0; i < keep; i++)
            {
                points.Add(candidates[i].P);
            }

            return points.MoveToImmutable();
        }
        finally
        {
            ArrayPool<float>.Shared.Return(lumaBuf);
            ArrayPool<float>.Shared.Return(gradBuf);
        }
    }
}
