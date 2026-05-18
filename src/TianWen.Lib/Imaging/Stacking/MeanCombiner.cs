using System;
using System.Numerics;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Plain mean of kept entries. The default combiner for kappa-sigma stacking:
/// reject outliers, average the rest. Mean is unbiased + optimal for Gaussian
/// noise, which is what we have after rejection has trimmed the non-Gaussian
/// tail.
/// </summary>
/// <remarks>
/// SIMD inner loop: keepMask doubles as a multiplicative weight, so the
/// accumulator picks up <c>v * keep</c> per lane (kept entries contribute
/// <c>v</c>, rejected contribute 0). NaN is treated as rejected — we
/// recompute the effective count from <c>!isNaN(v) * keep</c> rather than
/// from the mask alone so a frame with NaN borders (post-warp) doesn't
/// inflate the divisor.
/// </remarks>
public sealed class MeanCombiner : IPixelCombiner
{
    /// <inheritdoc/>
    public float Combine(ReadOnlySpan<float> column, ReadOnlySpan<float> keepMask)
    {
        if (column.Length != keepMask.Length)
        {
            throw new ArgumentException(
                $"column / keepMask length mismatch: {column.Length} vs {keepMask.Length}.",
                nameof(keepMask));
        }

        var width = Vector<float>.Count;
        var sumVec = Vector<float>.Zero;
        var cntVec = Vector<float>.Zero;
        var i = 0;
        for (; i <= column.Length - width; i += width)
        {
            var v = new Vector<float>(column[i..]);
            var k = new Vector<float>(keepMask[i..]);
            // NaN check: Vector.Equals(v, v) is true for non-NaN, false for NaN.
            // ConditionalSelect(mask, ifTrue, ifFalse) on the comparison result
            // zeros out NaN lanes for both the value AND the count contribution.
            var notNaN = Vector.Equals(v, v); // -1 (true) for non-NaN, 0 for NaN
            var notNaNF = Vector.ConditionalSelect(notNaN, Vector<float>.One, Vector<float>.Zero);
            var effective = k * notNaNF;
            // For NaN lanes we also need to zero v (NaN * 0 = NaN). Use ConditionalSelect again.
            var safeV = Vector.ConditionalSelect(notNaN, v, Vector<float>.Zero);
            sumVec += safeV * effective;
            cntVec += effective;
        }
        var sum = Vector.Sum(sumVec);
        var cnt = Vector.Sum(cntVec);
        for (; i < column.Length; i++)
        {
            var v = column[i];
            if (float.IsNaN(v)) continue;
            var k = keepMask[i];
            sum += v * k;
            cnt += k;
        }
        return cnt > 0f ? sum / cnt : 0f;
    }
}
