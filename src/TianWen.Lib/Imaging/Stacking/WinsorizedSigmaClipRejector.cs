using System;
using System.Buffers;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Winsorized variant of <see cref="SigmaClipRejector"/>. The rejection
/// criterion is the same -- values outside <c>[median - LowSigma * sigma_est,
/// median + HighSigma * sigma_est]</c> -- but the sigma estimate is computed
/// from a winsorized version of the column rather than the raw kept-set.
/// </summary>
/// <remarks>
/// <para>
/// In plain sigma clipping each iteration removes outliers entirely before
/// estimating sigma for the next round, which shrinks the kept set's spread
/// monotonically. On dithered subs where star pixels naturally have high
/// per-frame variance (each frame's PSF lands on a slightly different output
/// pixel) this tears chunks out of the bright tail -- the rejection map shows
/// "rejection halos" centred on every star.
/// </para>
/// <para>
/// Winsorization clamps out-of-bound values to the bound rather than removing
/// them when computing sigma. The bound-clamped distribution has a less
/// optimistically-tight sigma, so the subsequent iteration's threshold is
/// wider and bright-tail values survive. The actual reject decision still
/// operates on the original (unclamped) values against the winsorized-derived
/// threshold, so cosmic rays and hot pixels are still caught. SetiAstro ships
/// this as its default rejector for stacks of 10+ frames for exactly this
/// reason.
/// </para>
/// <para>
/// Cost: one extra median + MAD pass per iteration (~10% over plain sigma
/// clip on the integrator's hot path).
/// </para>
/// </remarks>
/// <param name="LowSigma">Reject threshold below median. Default 3 sigma.</param>
/// <param name="HighSigma">Reject threshold above median. Default 3 sigma.</param>
/// <param name="MaxIterations">Hard cap on iterations.</param>
public sealed record WinsorizedSigmaClipRejector(
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
        if (kept < 3) return kept;

        var pool = ArrayPool<float>.Shared;
        var valuesBuf = pool.Rent(column.Length);
        var madBuf = pool.Rent(column.Length);
        try
        {
            for (var iter = 0; iter < MaxIterations; iter++)
            {
                // 1. Collect currently-kept values and find the median.
                var keptCount = 0;
                for (var i = 0; i < column.Length; i++)
                {
                    if (keepMask[i] != 0f)
                    {
                        valuesBuf[keptCount++] = column[i];
                    }
                }
                if (keptCount < 3) break;

                var median = MedianFast(valuesBuf.AsSpan(0, keptCount));

                // 2. MAD of kept values (cheap, used to compute the *initial*
                // winsorization bounds).
                for (var i = 0; i < keptCount; i++)
                {
                    madBuf[i] = MathF.Abs(valuesBuf[i] - median);
                }
                var mad = MedianFast(madBuf.AsSpan(0, keptCount));
                if (mad <= 0f) break;

                var sigmaEst = MAD_TO_SD * mad;
                var lowBound = median - LowSigma * sigmaEst;
                var highBound = median + HighSigma * sigmaEst;

                // 3. Build the winsorized distribution: clamp kept values to
                // [lowBound, highBound]. Recompute median + MAD on the
                // clamped set so the new threshold isn't biased low by the
                // outliers we'd otherwise have removed entirely.
                keptCount = 0;
                for (var i = 0; i < column.Length; i++)
                {
                    if (keepMask[i] == 0f) continue;
                    var v = column[i];
                    if (v < lowBound) v = lowBound;
                    else if (v > highBound) v = highBound;
                    valuesBuf[keptCount++] = v;
                }
                var winMedian = MedianFast(valuesBuf.AsSpan(0, keptCount));
                for (var i = 0; i < keptCount; i++)
                {
                    madBuf[i] = MathF.Abs(valuesBuf[i] - winMedian);
                }
                var winMad = MedianFast(madBuf.AsSpan(0, keptCount));
                if (winMad <= 0f) break;

                var winSigma = MAD_TO_SD * winMad;
                var winLow = winMedian - LowSigma * winSigma;
                var winHigh = winMedian + HighSigma * winSigma;

                // 4. Apply rejection to the original (unclamped) values
                // against the winsorized-derived threshold. This catches
                // cosmic rays / hot pixels while being less aggressive on
                // the bright tail than plain sigma clip would be at the
                // same nominal kappa.
                var changed = false;
                for (var i = 0; i < column.Length; i++)
                {
                    if (keepMask[i] == 0f) continue;
                    var v = column[i];
                    if (v < winLow || v > winHigh)
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
}
