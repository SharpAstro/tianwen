using System;
using System.Buffers;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Drops a fixed integer count of the most-extreme values from each tail of
/// the per-pixel value column: <see cref="DropLowest"/> lowest and
/// <see cref="DropHighest"/> highest. Equivalent to the classic "min/max
/// clip" combiner used by DSS and Maxim DL.
/// </summary>
/// <remarks>
/// <para>
/// The crudest of the rejectors: no statistics, no iteration, just sort and
/// chop off the tails. Useful as a quick baseline, for very small stacks
/// where percentile fractions don't quantise meaningfully (e.g.
/// <c>0.1 * 5 = 0.5</c> rounds to 0 in
/// <see cref="PercentileClipRejector"/>), or when the contaminant count is
/// known a priori (e.g. exactly two satellite trails per session).
/// </para>
/// <para>
/// Compared to <see cref="PercentileClipRejector"/>, this expresses the same
/// idea in absolute frame counts instead of fractions -- handy when you've
/// stacked exactly 30 subs and want to drop "the worst 2" rather than
/// "6.67%" (which rounds to 1).
/// </para>
/// </remarks>
/// <param name="DropLowest">Number of lowest values to drop. Must be non-negative.</param>
/// <param name="DropHighest">Number of highest values to drop. Must be non-negative.</param>
public sealed record MinMaxClipRejector(
    int DropLowest = 1,
    int DropHighest = 1) : IPixelRejector
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
        if (DropLowest < 0 || DropHighest < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DropLowest),
                $"DropLowest / DropHighest must be non-negative; got Low={DropLowest}, High={DropHighest}.");
        }

        keepMask.Fill(1f);
        var total = column.Length;
        // Absent samples first: a NaN sorts to an unspecified end, so "drop the highest k" could
        // spend the whole budget on samples that were never there. See PixelRejection.MarkAbsent.
        var absent = PixelRejection.MarkAbsent(column, keepMask);
        var n = total - absent;
        var totalDrop = DropLowest + DropHighest;
        if (totalDrop == 0) return total;
        // Too few REAL samples to honour the request -- keep everything rather
        // than reject all. Matches the small-N behaviour of the other rejectors.
        if (totalDrop >= n) return total;

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

            for (var i = 0; i < DropLowest; i++)
            {
                keepMask[idxBuf[i]] = 0f;
            }
            for (var i = 0; i < DropHighest; i++)
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
