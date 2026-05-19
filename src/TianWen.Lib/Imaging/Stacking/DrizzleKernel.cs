using System;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Shared forward-projection kernel for the drizzle family of strategies
/// (<see cref="DrizzleStrategy"/>, <see cref="TilePipelinedDrizzleStrategy"/>).
/// Per-frame, iterates a source-pixel rectangle and deposits each valid CFA
/// sample as a <c>pixfrac</c>-sized "drop" onto the per-channel
/// <c>flux</c> / <c>weight</c> planes the caller owns. The accumulator
/// arrays are not necessarily canvas-sized: the caller passes
/// <paramref name="xStart"/>/<paramref name="xEnd"/>/<paramref name="yStart"/>/<paramref name="yEnd"/>
/// to declare the canvas coordinate range the arrays cover. Drops landing
/// outside that range are clipped. This is the single mechanism that lets
/// <see cref="DrizzleStrategy"/> deposit into a full-canvas accumulator
/// and <see cref="TilePipelinedDrizzleStrategy"/> deposit into a strip-
/// local one without the kernel needing to know the difference.
/// </summary>
internal static class DrizzleKernel
{
    /// <summary>
    /// Iterate the <paramref name="sourceRect"/> sub-region of
    /// <paramref name="raw"/> (a 1-channel calibrated Bayer plane) and
    /// forward-project each valid sample onto <paramref name="flux"/> /
    /// <paramref name="weight"/>. Honours <paramref name="badPixelMask"/> via
    /// a per-64-pixel-chunk word fast-path: when the mask word is zero (which
    /// is the common case -- typical sensor hot-pixel rates are 0.02% so
    /// 99.98% of chunks are clean) the per-pixel mask check is skipped
    /// entirely.
    /// </summary>
    /// <param name="raw">Calibrated 1-channel Bayer source frame.</param>
    /// <param name="transform">Source -> canvas affine for this frame.</param>
    /// <param name="pattern">2x2 Bayer pattern in sensor coordinates;
    /// <c>pattern[ySrc &amp; 1, xSrc &amp; 1]</c> selects which output channel
    /// the source pixel feeds.</param>
    /// <param name="halfP">Half the pixfrac drop extent (i.e. <c>pixfrac/2</c>).
    /// At pixfrac=1.0 each drop is a unit cell; smaller pixfrac sharpens
    /// the output at the cost of needing more frames for coverage.</param>
    /// <param name="flux">Per-channel flux accumulator, indexed as
    /// <c>flux[ch][yCanvas - yStart, xCanvas - xStart]</c>. Must be sized
    /// at least <c>(yEnd - yStart) x (xEnd - xStart)</c> per channel.</param>
    /// <param name="weight">Per-channel coverage-weight accumulator, same
    /// shape as <paramref name="flux"/>.</param>
    /// <param name="xStart">Canvas X coordinate the first column of the
    /// accumulators corresponds to.</param>
    /// <param name="xEnd">Canvas X coordinate one past the last column of
    /// the accumulators (exclusive).</param>
    /// <param name="yStart">Canvas Y coordinate the first row of the
    /// accumulators corresponds to.</param>
    /// <param name="yEnd">Canvas Y coordinate one past the last row of the
    /// accumulators (exclusive).</param>
    /// <param name="sourceRect">Sub-rectangle of <paramref name="raw"/> to
    /// iterate. Pre-clamped to source bounds. Pass the whole frame for the
    /// full-canvas drizzle path.</param>
    /// <param name="badPixelMask">Optional hot-pixel mask. Default value
    /// (<c>!hasBadPixelMask</c>) skips the mask check entirely.</param>
    /// <param name="hasBadPixelMask">True iff <paramref name="badPixelMask"/>
    /// is populated. Hoisted out of the inner loop.</param>
    public static void IterateAndDeposit(
        Image raw,
        Matrix3x2 transform,
        int[,] pattern,
        float halfP,
        float[][,] flux,
        float[][,] weight,
        int xStart,
        int xEnd,
        int yStart,
        int yEnd,
        Rectangle sourceRect,
        BitMatrix badPixelMask,
        bool hasBadPixelMask)
    {
        var srcX0 = sourceRect.X;
        var srcX1 = sourceRect.X + sourceRect.Width;
        var srcY0 = sourceRect.Y;
        var srcY1 = sourceRect.Y + sourceRect.Height;

        for (var ySrc = srcY0; ySrc < srcY1; ySrc++)
        {
            var xSrc = srcX0;
            while (xSrc < srcX1)
            {
                // 64-wide horizontal chunk -- aligned to word boundary so a
                // single GetWord covers all 64 mask bits at once. ChunkEnd
                // is the next word boundary or the end of the rect,
                // whichever comes first.
                var chunkEnd = Math.Min(((xSrc >> 6) + 1) << 6, srcX1);
                var maskWord = hasBadPixelMask ? badPixelMask.GetWord(ySrc, xSrc >> 6) : 0UL;
                if (maskWord == 0UL)
                {
                    // Fast path: no per-pixel mask check needed.
                    for (; xSrc < chunkEnd; xSrc++)
                    {
                        DepositOne(xSrc, ySrc, raw, transform, pattern, halfP, flux, weight, xStart, xEnd, yStart, yEnd);
                    }
                }
                else
                {
                    // Slow path: bit-test each pixel against the already-
                    // loaded mask word.
                    for (; xSrc < chunkEnd; xSrc++)
                    {
                        if ((maskWord & (1UL << (xSrc & 63))) != 0UL) continue;
                        DepositOne(xSrc, ySrc, raw, transform, pattern, halfP, flux, weight, xStart, xEnd, yStart, yEnd);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Forward-project a single source pixel and deposit its area-weighted
    /// share into each canvas cell its drop covers. Coordinate convention
    /// matches <c>Image.SubpixelValue</c> / <c>WarpToReferenceGridAsync</c>:
    /// canvas cell at index <c>(xc, yc)</c> is centered on position
    /// <c>(xc, yc)</c> and occupies <c>[xc-0.5, xc+0.5] x [yc-0.5, yc+0.5]</c>.
    /// A previous version of this kernel mixed half-pixel-shift conventions
    /// and produced dumbbell-shaped stars in combined meridian-flip drizzle
    /// output -- keep this path strictly consistent with the warp path or
    /// the per-frame rotation residuals will reappear as visible artifacts.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DepositOne(
        int xSrc, int ySrc,
        Image raw, Matrix3x2 transform, int[,] pattern, float halfP,
        float[][,] flux, float[][,] weight,
        int xStart, int xEnd, int yStart, int yEnd)
    {
        var v = raw[0, ySrc, xSrc];
        if (float.IsNaN(v)) return;

        var p = Vector2.Transform(new Vector2(xSrc, ySrc), transform);
        var xLo = p.X - halfP;
        var xHi = p.X + halfP;
        var yLo = p.Y - halfP;
        var yHi = p.Y + halfP;

        // Loop bounds: cell index i overlaps the drop iff
        //   i - 0.5 < xHi and i + 0.5 > xLo,
        // i.e. floor(xLo + 0.5) <= i <= ceil(xHi - 0.5).
        // Clamp to the [xStart, xEnd) x [yStart, yEnd) accumulator range
        // (full canvas for the streaming drizzle, strip-local for tiled).
        var x0 = Math.Max(xStart, (int)MathF.Floor(xLo + 0.5f));
        var x1 = Math.Min(xEnd - 1, (int)MathF.Ceiling(xHi - 0.5f));
        var y0 = Math.Max(yStart, (int)MathF.Floor(yLo + 0.5f));
        var y1 = Math.Min(yEnd - 1, (int)MathF.Ceiling(yHi - 0.5f));
        if (x1 < x0 || y1 < y0) return;

        var ch = pattern[ySrc & 1, xSrc & 1];
        var fluxCh = flux[ch];
        var weightCh = weight[ch];

        for (var yc = y0; yc <= y1; yc++)
        {
            var localY = yc - yStart;
            var cellYLo = yc - 0.5f;
            var cellYHi = yc + 0.5f;
            var dy = MathF.Min(yHi, cellYHi) - MathF.Max(yLo, cellYLo);
            if (dy <= 0f) continue;
            for (var xc = x0; xc <= x1; xc++)
            {
                var localX = xc - xStart;
                var cellXLo = xc - 0.5f;
                var cellXHi = xc + 0.5f;
                var dx = MathF.Min(xHi, cellXHi) - MathF.Max(xLo, cellXLo);
                if (dx <= 0f) continue;
                var area = dx * dy;
                fluxCh[localY, localX] += v * area;
                weightCh[localY, localX] += area;
            }
        }
    }

    /// <summary>
    /// Final pass: <c>master = (flux / weight) * invMaxValue</c>, with NaN
    /// where coverage is zero. Mutates <paramref name="flux"/> in place
    /// (the caller hands the buffer over and treats the result as the
    /// master). <paramref name="weight"/> stays as the coverage map.
    /// Returns the number of cells that had non-zero coverage so the
    /// caller can build the rejection-rate diagnostic.
    /// </summary>
    /// <param name="flux">Per-channel flux accumulator -- on return, holds
    /// the normalised master pixels.</param>
    /// <param name="weight">Per-channel coverage weight accumulator. Read-
    /// only here.</param>
    /// <param name="invMaxValue"><c>1 / sourceMaxValue</c> so the master
    /// lands in <c>[0, 1]</c> -- matches the Integrator's per-frame
    /// normalization output so <c>MasterPostProcessor</c>'s MaxValue=1.0
    /// fix-up and <c>Image.Histogram</c>'s MaxValue&lt;=1.0 branch both see
    /// a self-consistent (range, label) pair.</param>
    /// <param name="height">Accumulator rows.</param>
    /// <param name="width">Accumulator columns.</param>
    public static long FinaliseDivide(float[][,] flux, float[][,] weight, float invMaxValue, int height, int width)
    {
        long coveredCells = 0;
        for (var c = 0; c < flux.Length; c++)
        {
            var f = flux[c];
            var w = weight[c];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var wv = w[y, x];
                    if (wv > 0f)
                    {
                        f[y, x] = f[y, x] / wv * invMaxValue;
                        coveredCells++;
                    }
                    else
                    {
                        f[y, x] = float.NaN;
                    }
                }
            }
        }
        return coveredCells;
    }
}
