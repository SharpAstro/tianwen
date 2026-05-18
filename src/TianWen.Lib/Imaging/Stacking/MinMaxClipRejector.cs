using System;
using System.Buffers;

namespace TianWen.Lib.Imaging.Calibration;

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
        var n = column.Length;
        var totalDrop = DropLowest + DropHighest;
        if (totalDrop == 0) return n;
        // Too few samples to honour the request -- keep everything rather
        // than reject all. Matches the small-N behaviour of the other rejectors.
        if (totalDrop >= n) return n;

        var floatPool = ArrayPool<float>.Shared;
        var intPool = ArrayPool<int>.Shared;
        var valsBuf = floatPool.Rent(n);
        var idxBuf = intPool.Rent(n);
        try
        {
            for (var i = 0; i < n; i++)
            {
                valsBuf[i] = column[i];
                idxBuf[i] = i;
            }
            MemoryExtensions.Sort(valsBuf.AsSpan(0, n), idxBuf.AsSpan(0, n));

            for (var i = 0; i < DropLowest; i++)
            {
                keepMask[idxBuf[i]] = 0f;
            }
            for (var i = 0; i < DropHighest; i++)
            {
                keepMask[idxBuf[n - 1 - i]] = 0f;
            }
            return n - totalDrop;
        }
        finally
        {
            floatPool.Return(valsBuf);
            intPool.Return(idxBuf);
        }
    }
}
