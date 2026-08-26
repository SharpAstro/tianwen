using System;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Per-output-pixel outlier rejector. The integrator calls this once for each
/// output tile pixel, passing the N-frame value column and a per-frame keep
/// mask. After return, mask entries of 0.0 mark rejected frames; the
/// integrator's combine step multiplies by the mask to exclude them.
/// </summary>
/// <remarks>
/// <para>
/// The mask is a <see cref="Span{Single}"/> of 1.0 (kept) / 0.0 (rejected)
/// rather than a bool span so the kernel can use it as a multiplicative
/// weight in the masked-stats inner loop: <c>sum += v * keep</c> /
/// <c>count += keep</c>. A bool mask would force a branch per lane and
/// defeat <see cref="System.Numerics.Vector{T}"/> vectorisation.
/// </para>
/// <para>
/// The caller is responsible for buffer lifetime: typically the integrator
/// rents a single <c>float[N]</c> from <see cref="System.Buffers.ArrayPool{T}"/>
/// per tile row and reuses it across the row's output pixels.
/// </para>
/// </remarks>
public interface IPixelRejector
{
    /// <summary>
    /// Inspects <paramref name="column"/> (one value per stacked frame at a
    /// single output pixel) and writes a keep mask to <paramref name="keepMask"/>:
    /// 1.0 means the frame's value will contribute to the combine; 0.0 means
    /// it will be excluded.
    /// </summary>
    /// <param name="column">Per-frame pixel values, length N. Read-only; the
    /// implementer must not mutate.</param>
    /// <param name="keepMask">Per-frame keep mask, length N. The implementer
    /// initialises and writes; pre-existing values are overwritten.</param>
    /// <returns>Count of entries NOT rejected. An absent sample (NaN) counts as not rejected: it was
    /// never a candidate, so counting it would report a rejection that did not happen and inflate the
    /// rejection map wherever frames simply do not overlap.</returns>
    int Reject(ReadOnlySpan<float> column, Span<float> keepMask);
}

/// <summary>
/// Shared prelude for every <see cref="IPixelRejector"/>: mark the samples that are not there.
/// </summary>
/// <remarks>
/// <para><b>A NaN left in the column disables rejection entirely for that pixel, silently.</b> Every
/// comparison against NaN is false, so quickselect returns nonsense, the MAD comes out NaN, the
/// <c>mad &lt;= 0f</c> degenerate guard does NOT fire (that comparison is false for NaN too), both
/// bounds become NaN, and finally <c>v &lt; NaN</c> and <c>v &gt; NaN</c> are both false -- so no
/// sample is ever rejected and the iteration breaks on its first pass. Nothing throws and nothing
/// logs; the column just quietly gets no outlier rejection at all.</para>
///
/// <para>This was not introduced by any one feature. Warped frames carry NaN borders, so canvas
/// edges have always had rejection switched off wherever frames do not all overlap. It became
/// visible when <c>CometMask</c> started putting NaN in the MIDDLE of the frame: hot pixels and
/// cosmic rays that are clipped everywhere else survived inside the masked band and read as
/// bad-pixel clumps. Measured on C/2025 R2: rejection rate 0.0000 inside the band against 0.026-0.034
/// outside it.</para>
/// </remarks>
internal static class PixelRejection
{
    /// <summary>
    /// Marks every NaN entry as not-kept so it cannot reach a median, a sort or a fit, and returns
    /// how many there were. Call immediately after <c>keepMask.Fill(1f)</c>; the rejectors' own loops
    /// already skip zeroed entries, so nothing else has to change.
    /// </summary>
    public static int MarkAbsent(ReadOnlySpan<float> column, Span<float> keepMask)
    {
        var absent = 0;
        for (var i = 0; i < column.Length; i++)
        {
            if (float.IsNaN(column[i]))
            {
                keepMask[i] = 0f;
                absent++;
            }
        }
        return absent;
    }
}
