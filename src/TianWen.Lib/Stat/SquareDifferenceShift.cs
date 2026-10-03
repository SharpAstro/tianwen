using System;

namespace TianWen.Lib.Stat;

/// <summary>
/// A patch's shift against a reference by square difference (Loefdahl 2010, "Evaluation of image-shift measurement algorithms for
/// solar Shack-Hartmann wavefront sensors", A&amp;A 524, A90): the moving patch is compared, unwindowed, with every window of a
/// reference larger than it by a margin each side, both mean- and plane-subtracted, and the minimum of the square difference is placed
/// between the pixels by a 2-D quadratic fit through its 3 by 3 neighbourhood. No window sits still while the content moves, so the
/// shift is not pulled toward zero as a windowed correlation's is (#1082; docs/plans/planetary-restoration.md, "#1082: an estimator
/// for the points, against its bound").
/// </summary>
public static class SquareDifferenceShift
{
    /// <summary>
    /// The shift (<c>Dx</c>, <c>Dy</c>) of <paramref name="moving"/> (<paramref name="size"/> square, row-major) against
    /// <paramref name="reference"/> (<paramref name="size"/> plus twice <paramref name="margin"/> square, centred on the same point):
    /// <c>moving(x)</c> is <c>reference(x - shift)</c>, the convention <see cref="PhaseCorrelation"/> reads. The search is whole pixels
    /// within the margin; a minimum on its edge is returned there, unrefined.
    /// </summary>
    public static (double Dx, double Dy) Estimate(ReadOnlySpan<float> reference, ReadOnlySpan<float> moving, int size, int margin, Span<double> scratch)
    {
        var big = size + (2 * margin);
        if (reference.Length < big * big || moving.Length < size * size)
        {
            throw new ArgumentException($"reference must be {big} square and moving {size} square");
        }
        var span = (2 * margin) + 1;
        if (scratch.Length < (size * size) + (span * span))
        {
            throw new ArgumentException($"scratch must hold {(size * size) + (span * span)} values", nameof(scratch));
        }
        var levelled = scratch[..(size * size)];
        var difference = scratch.Slice(size * size, span * span);

        // The moving patch, less its plane.
        LessPlane(moving, size, 0, 0, size, levelled);

        // For a shift t the moving patch at x holds the reference at x - t, the reference window starting at margin - t.
        Span<double> window = size * size <= 1024 ? stackalloc double[size * size] : new double[size * size];
        for (var ty = -margin; ty <= margin; ty++)
        {
            for (var tx = -margin; tx <= margin; tx++)
            {
                LessPlane(reference, big, margin - tx, margin - ty, size, window);
                double sum = 0;
                for (var i = 0; i < levelled.Length; i++)
                {
                    var d = levelled[i] - window[i];
                    sum += d * d;
                }
                difference[((ty + margin) * span) + tx + margin] = sum;
            }
        }

        var best = 0;
        for (var i = 1; i < difference.Length; i++)
        {
            if (difference[i] < difference[best])
            {
                best = i;
            }
        }
        var (bx, by) = (best % span, best / span);
        if (bx == 0 || by == 0 || bx == span - 1 || by == span - 1)
        {
            return (bx - margin, by - margin);
        }

        // The quadratic z = a0 + a1 x + a2 y + a3 x^2 + a4 x y + a5 y^2 through the 3 by 3 neighbourhood, by least squares.
        // s[row][column]: row the y offset, column the x offset, each -1, 0, +1.
        var (up, mid, down) = ((by - 1) * span, by * span, (by + 1) * span);
        var (smm, s0m, spm) = (difference[up + bx - 1], difference[up + bx], difference[up + bx + 1]);
        var (sm0, s00, sp0) = (difference[mid + bx - 1], difference[mid + bx], difference[mid + bx + 1]);
        var (smp, s0p, spp) = (difference[down + bx - 1], difference[down + bx], difference[down + bx + 1]);
        var a1 = (spm + sp0 + spp - smm - sm0 - smp) / 6;
        var a2 = (smp + s0p + spp - smm - s0m - spm) / 6;
        var a3 = ((spm + sp0 + spp + smm + sm0 + smp) / 6) - ((s0m + s00 + s0p) / 3);
        var a5 = ((smp + s0p + spp + smm + s0m + spm) / 6) - ((sm0 + s00 + sp0) / 3);
        var a4 = (spp - spm - smp + smm) / 4;
        var det = (4 * a3 * a5) - (a4 * a4);
        if (!(det > 0) || !(a3 > 0))
        {
            return (bx - margin, by - margin);
        }
        var x = ((a4 * a2) - (2 * a5 * a1)) / det;
        var y = ((a4 * a1) - (2 * a3 * a2)) / det;
        return (bx - margin + Math.Clamp(x, -1, 1), by - margin + Math.Clamp(y, -1, 1));
    }

    // The size by size window of `source` (stride `stride`) at (x0, y0), less its least-squares plane, into `destination`.
    private static void LessPlane(ReadOnlySpan<float> source, int stride, int x0, int y0, int size, Span<double> destination)
    {
        var centre = (size - 1) / 2.0;
        double sum = 0, sumU = 0, sumV = 0;
        for (var y = 0; y < size; y++)
        {
            var row = ((y0 + y) * stride) + x0;
            for (var x = 0; x < size; x++)
            {
                double value = source[row + x];
                sum += value;
                sumU += (x - centre) * value;
                sumV += (y - centre) * value;
            }
        }
        // The coordinates are centred and the grid square, so the plane's terms are orthogonal: each is its own projection.
        double uu = 0;
        for (var x = 0; x < size; x++)
        {
            uu += (x - centre) * (x - centre);
        }
        uu *= size;
        var (mean, slopeU, slopeV) = (sum / (size * size), sumU / uu, sumV / uu);
        for (var y = 0; y < size; y++)
        {
            var row = ((y0 + y) * stride) + x0;
            for (var x = 0; x < size; x++)
            {
                destination[(y * size) + x] = source[row + x] - mean - (slopeU * (x - centre)) - (slopeV * (y - centre));
            }
        }
    }
}
