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
    /// <returns>Count of kept entries (where <paramref name="keepMask"/>[i] == 1.0).</returns>
    int Reject(ReadOnlySpan<float> column, Span<float> keepMask);
}
