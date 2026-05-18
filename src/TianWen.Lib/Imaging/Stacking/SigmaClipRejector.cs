using System;
using System.Buffers;
using System.Numerics;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Iterative outlier rejection using median + MAD-derived sigma estimate.
/// Each iteration computes the median of currently-kept values and the
/// median absolute deviation (MAD), then rejects values outside
/// <c>[median - LowSigma * sigma_est, median + HighSigma * sigma_est]</c>
/// where <c>sigma_est = 1.4826 * MAD</c> (the Gaussian-consistent scale
/// factor). Converges when no new rejections happen or after
/// <see cref="MaxIterations"/>.
/// </summary>
/// <remarks>
/// Median + MAD rather than mean + std because a single far outlier inflates
/// both the mean AND the std, so its z-score against the polluted
/// distribution falls within bounds and the naive rejector keeps it. The
/// median is robust to up to N/2 contaminated samples and MAD is robust to
/// the same; together they detect a cosmic-ray hit at row index 0 with
/// 99/100 clean samples as readily as one in the middle of a 50/50 split.
/// PixInsight and SetiAstro's kappa-sigma variant both use this construction.
/// </remarks>
/// <param name="LowSigma">Reject threshold below median. Default 3 sigma.</param>
/// <param name="HighSigma">Reject threshold above median. Default 3 sigma.</param>
/// <param name="MaxIterations">Hard cap on iterations. Three is typical for
/// well-behaved data; five handles edge cases without running away.</param>
public sealed record SigmaClipRejector(
    float LowSigma = 3f,
    float HighSigma = 3f,
    int MaxIterations = 5) : IPixelRejector
{
    /// <inheritdoc/>
    public int Reject(ReadOnlySpan<float> column, Span<float> keepMask)
    {
        if (column.Length != keepMask.Length)
        {
            throw new ArgumentException(
                $"column / keepMask length mismatch: {column.Length} vs {keepMask.Length}.",
                nameof(keepMask));
        }

        keepMask.Fill(1f);
        var kept = column.Length;
        if (kept < 3) return kept; // not enough samples for meaningful sigma

        var pool = ArrayPool<float>.Shared;
        var valuesBuf = pool.Rent(column.Length);
        var madBuf = pool.Rent(column.Length);
        try
        {
            for (var iter = 0; iter < MaxIterations; iter++)
            {
                // 1. Collect kept values, find median.
                var keptCount = 0;
                for (var i = 0; i < column.Length; i++)
                {
                    if (keepMask[i] != 0f)
                    {
                        valuesBuf[keptCount++] = column[i];
                    }
                }
                if (keptCount < 3) break;

                // MedianFast (quickselect) instead of full sort: this rejector
                // runs per-pixel across the full integrated frame (3008x3008 on
                // IMX533, up to 9M columns per group), 5 iterations max, with
                // 2 median calls per iteration. O(n) per call vs O(n log n)
                // saves a measurable chunk of the integration step.
                var values = valuesBuf.AsSpan(0, keptCount);
                var median = MedianFast(values);

                // 2. Compute MAD = median(|v - median|). We can read valuesBuf
                // in iteration order regardless of MedianFast's permutation --
                // the absolute-deviation is value-only, position-agnostic.
                for (var i = 0; i < keptCount; i++)
                {
                    madBuf[i] = MathF.Abs(valuesBuf[i] - median);
                }
                var mad = MedianFast(madBuf.AsSpan(0, keptCount));
                if (mad <= 0f)
                {
                    // Degenerate: > half the kept values are exactly equal to
                    // the median. The distribution has no measurable spread
                    // around the median, so this iteration can't tell signal
                    // from noise. Stop -- accepting the current keep set is
                    // the only sensible response.
                    break;
                }

                // 3. Reject anything outside median +/- (sigma) * MAD-scaled-sigma.
                // 1.4826 is the Gaussian-consistent factor: for normally
                // distributed data, sigma_true = 1.4826 * MAD.
                var sigmaEst = 1.4826f * mad;
                var lowBound = median - LowSigma * sigmaEst;
                var highBound = median + HighSigma * sigmaEst;
                var changed = false;
                for (var i = 0; i < column.Length; i++)
                {
                    if (keepMask[i] == 0f) continue;
                    var v = column[i];
                    if (v < lowBound || v > highBound)
                    {
                        keepMask[i] = 0f;
                        kept--;
                        changed = true;
                    }
                }
                if (!changed) break;
            }
        }
        finally
        {
            pool.Return(valuesBuf);
            pool.Return(madBuf);
        }

        return kept;
    }

    /// <summary>
    /// Vector&lt;float&gt; masked-stats kernel — kept for diagnostics + the
    /// future Phase 8 path where the integrator may want a quick mean/std
    /// check before deciding whether to invoke the more expensive median+MAD
    /// rejector on a column. The mask doubles as a multiplicative weight in
    /// the sum accumulators; kept-count emerges as the sum of the mask.
    /// </summary>
    internal static (float Mean, float Std, int Count) ComputeMaskedStats(ReadOnlySpan<float> column, ReadOnlySpan<float> keepMask)
    {
        var width = Vector<float>.Count;
        var sumVec = Vector<float>.Zero;
        var sqVec = Vector<float>.Zero;
        var cntVec = Vector<float>.Zero;
        var i = 0;
        for (; i <= column.Length - width; i += width)
        {
            var v = new Vector<float>(column[i..]);
            var k = new Vector<float>(keepMask[i..]);
            sumVec += v * k;
            sqVec += v * v * k;
            cntVec += k;
        }
        var sum = Vector.Sum(sumVec);
        var sq = Vector.Sum(sqVec);
        var cnt = Vector.Sum(cntVec);
        for (; i < column.Length; i++)
        {
            var v = column[i];
            var k = keepMask[i];
            sum += v * k;
            sq += v * v * k;
            cnt += k;
        }
        if (cnt < 1f) return (0f, 0f, 0);
        var mean = sum / cnt;
        var variance = sq / cnt - mean * mean;
        if (variance < 0f) variance = 0f;
        return (mean, MathF.Sqrt(variance), (int)cnt);
    }
}
