using System;
using System.Buffers;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Linear-fit clipping: sorts the per-pixel value column ascending, fits a
/// least-squares line <c>value = a * rank + b</c>, computes the MAD of
/// residuals, and rejects any frame whose residual exceeds the kappa-scaled
/// sigma estimate. Iterates until convergence or <see cref="MaxIterations"/>.
/// </summary>
/// <remarks>
/// <para>
/// PixInsight's recommended rejector for stacks of 15+ dithered subs. The
/// key insight: sorted-value vs rank for a real per-pixel column with
/// transparency drift is approximately linear -- not perfectly so, but
/// linear enough that outliers (cosmic rays, satellite trails, dither-induced
/// star-pixel-vs-background ambiguity) stand out as residuals well above the
/// MAD-scaled threshold while the natural bright tail of star pixels does
/// not. Plain sigma clipping fails here because the bright tail pulls the
/// kept-set sigma down each iteration, eventually rejecting the very signal
/// you wanted to keep ("rejection halos" around stars).
/// </para>
/// <para>
/// Implementation: closed-form sums for <c>SUM(x)</c> and <c>SUM(x^2)</c>
/// over <c>x in [0, n-1]</c> (no inner loop), single LSQ slope/intercept,
/// MAD of residuals via <see cref="StatisticsHelper.MedianFast(System.Span{float})"/>.
/// Per-iteration cost is roughly 2x plain sigma clip but still O(n) -- on
/// 244-frame columns the integrator hot path measures ~150 ns/iteration vs
/// ~80 ns/iteration for sigma clip.
/// </para>
/// <para>
/// <c>MinSamples = 5</c>: the LSQ fit needs at least 5 well-spread samples
/// before the slope/intercept have meaningful confidence; below that the
/// rejector returns all-kept rather than over-rejecting.
/// </para>
/// </remarks>
/// <param name="LowSigma">Reject threshold below the fit line. Default 3 sigma.</param>
/// <param name="HighSigma">Reject threshold above the fit line. Default 3 sigma.</param>
/// <param name="MaxIterations">Hard cap on iterations.</param>
public sealed record LinearFitClipRejector(
    float LowSigma = 3f,
    float HighSigma = 3f,
    int MaxIterations = 5) : IPixelRejector
{
    /// <summary>Minimum kept samples before the LSQ fit is meaningful.</summary>
    public const int MinSamples = 5;

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
        if (kept < MinSamples) return kept;

        var floatPool = ArrayPool<float>.Shared;
        var intPool = ArrayPool<int>.Shared;
        var valsBuf = floatPool.Rent(column.Length);
        var idxBuf = intPool.Rent(column.Length);
        var residBuf = floatPool.Rent(column.Length);
        try
        {
            for (var iter = 0; iter < MaxIterations; iter++)
            {
                // 1. Snapshot kept values + original indices, then sort
                // ascending. Span<float>.Sort + parallel int span keeps the
                // index map in lockstep so we can write rejections back to
                // keepMask via the original index.
                var keptCount = 0;
                for (var i = 0; i < column.Length; i++)
                {
                    if (keepMask[i] != 0f)
                    {
                        valsBuf[keptCount] = column[i];
                        idxBuf[keptCount] = i;
                        keptCount++;
                    }
                }
                if (keptCount < MinSamples) break;

                var vals = valsBuf.AsSpan(0, keptCount);
                var idxs = idxBuf.AsSpan(0, keptCount);
                MemoryExtensions.Sort(vals, idxs);

                // 2. LSQ fit value = a*rank + b. Closed-form sums for
                // SUM(rank), SUM(rank^2) over rank in [0, n-1]:
                //   SUM(x)  = n*(n-1)/2
                //   SUM(x2) = n*(n-1)*(2n-1)/6
                var n = keptCount;
                double sumY = 0.0, sumXY = 0.0;
                for (var i = 0; i < n; i++)
                {
                    sumY += vals[i];
                    sumXY += (double)i * vals[i];
                }
                var sumX = (double)n * (n - 1) / 2.0;
                var sumX2 = (double)n * (n - 1) * (2 * n - 1) / 6.0;
                var denom = n * sumX2 - sumX * sumX;
                if (denom == 0.0) break;
                var a = (n * sumXY - sumX * sumY) / denom;
                var b = (sumY - a * sumX) / n;

                // 3. Residuals = value - (a*rank + b). MAD of |residuals|
                // gives a robust sigma estimate that ignores the outliers
                // we're about to reject.
                for (var i = 0; i < n; i++)
                {
                    residBuf[i] = vals[i] - (float)(a * i + b);
                }
                // Copy abs(residuals) into the front of valsBuf for the
                // MedianFast call -- valsBuf is no longer needed at its
                // sorted-by-value role, and residBuf still holds the signed
                // residuals we need for the bound check below.
                for (var i = 0; i < n; i++)
                {
                    valsBuf[i] = MathF.Abs(residBuf[i]);
                }
                var madRes = MedianFast(valsBuf.AsSpan(0, n));
                if (madRes <= 0f) break;
                var sigmaRes = MAD_TO_SD * madRes;

                // 4. Reject by signed residual against asymmetric sigma
                // bounds. Write back through idxs[i] so the mask uses the
                // original frame indices, not the sorted ones.
                var lowBound = -LowSigma * sigmaRes;
                var highBound = HighSigma * sigmaRes;
                var changed = false;
                for (var i = 0; i < n; i++)
                {
                    var resid = residBuf[i];
                    if (resid < lowBound || resid > highBound)
                    {
                        keepMask[idxs[i]] = 0f;
                        kept--;
                        changed = true;
                    }
                }
                if (!changed) break;
            }
        }
        finally
        {
            floatPool.Return(valsBuf);
            intPool.Return(idxBuf);
            floatPool.Return(residBuf);
        }

        return kept;
    }
}
