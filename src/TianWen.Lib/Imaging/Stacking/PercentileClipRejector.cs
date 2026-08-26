using System;
using System.Buffers;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Drops a fixed fraction of the most-extreme values from each tail of the
/// per-pixel value column. Sorts ascending and rejects the first
/// <c>floor(LowFraction * N)</c> entries plus the last
/// <c>floor(HighFraction * N)</c> entries; the remaining middle <c>N - L - H</c>
/// entries are kept.
/// </summary>
/// <remarks>
/// <para>
/// The simplest robust rejector. Doesn't depend on a sigma estimate, so it
/// behaves predictably on small stacks (N=3..7) where the MAD-derived sigma
/// from <see cref="SigmaClipRejector"/> is noisy and the LSQ fit from
/// <see cref="LinearFitClipRejector"/> doesn't have enough samples for a
/// trustworthy slope. Also useful as a quick first pass when the data is
/// known to contain a predictable contaminant rate (e.g., satellite trails
/// estimated at ~5% of frames).
/// </para>
/// <para>
/// Note this is the "fraction" variant -- <c>LowFraction=0.1</c> drops the
/// lowest 10% of frames at every output pixel. PixInsight's "Percentile
/// Clipping" with the same name uses a normalised-deviation threshold
/// instead (reject values whose |v - median| / range exceeds a threshold);
/// the fraction form is what Siril and DSS expose and matches what users
/// typically mean by "drop the lowest / highest N%".
/// </para>
/// <para>
/// Cost: one sort per column, no iteration. <see cref="MemoryExtensions.Sort{T}(Span{T})"/>
/// on a 244-float span is well under a microsecond.
/// </para>
/// </remarks>
/// <param name="LowFraction">Fraction of lowest values to reject. Range [0, 0.5).</param>
/// <param name="HighFraction">Fraction of highest values to reject. Range [0, 0.5).</param>
public sealed record PercentileClipRejector(
    float LowFraction = 0.1f,
    float HighFraction = 0.1f) : IPixelRejector
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
        if (LowFraction < 0f || HighFraction < 0f || LowFraction >= 0.5f || HighFraction >= 0.5f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LowFraction),
                $"LowFraction / HighFraction must be in [0, 0.5); got Low={LowFraction}, High={HighFraction}.");
        }

        keepMask.Fill(1f);
        var total = column.Length;
        // Absent samples first: a NaN sorts to an unspecified end, so the drop budget could be spent
        // on samples that were never there. The percentiles are then taken over the REAL count --
        // where half the frames do not overlap, LowFraction * length would cut a far larger share of
        // the data that IS present. See PixelRejection.MarkAbsent.
        var absent = PixelRejection.MarkAbsent(column, keepMask);
        var n = total - absent;
        if (n < 3) return total;

        var lowDrop = (int)MathF.Floor(LowFraction * n);
        var highDrop = (int)MathF.Floor(HighFraction * n);
        var totalDrop = lowDrop + highDrop;
        if (totalDrop == 0) return total;
        if (totalDrop >= n) return total; // would reject everything -- caller mistake, keep all

        var floatPool = ArrayPool<float>.Shared;
        var intPool = ArrayPool<int>.Shared;
        var valsBuf = floatPool.Rent(n);
        var idxBuf = intPool.Rent(n);
        try
        {
            var m = 0;
            for (var i = 0; i < total; i++)
            {
                if (keepMask[i] == 0f) continue;
                valsBuf[m] = column[i];
                idxBuf[m] = i;
                m++;
            }
            MemoryExtensions.Sort(valsBuf.AsSpan(0, m), idxBuf.AsSpan(0, m));

            // Reject the lowest lowDrop and highest highDrop entries by
            // their original index.
            for (var i = 0; i < lowDrop; i++)
            {
                keepMask[idxBuf[i]] = 0f;
            }
            for (var i = 0; i < highDrop; i++)
            {
                keepMask[idxBuf[m - 1 - i]] = 0f;
            }
            return total - totalDrop;
        }
        finally
        {
            floatPool.Return(valsBuf);
            intPool.Return(idxBuf);
        }
    }
}
