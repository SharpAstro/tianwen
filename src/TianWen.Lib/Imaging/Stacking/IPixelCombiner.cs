using System;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Combines the kept entries of an N-frame column at a single output pixel
/// into the final output value. Runs immediately after
/// <see cref="IPixelRejector"/> so the mask is already populated.
/// </summary>
/// <remarks>
/// Separated from the rejector so users can mix and match. The default
/// pairing — <see cref="SigmaClipRejector"/> + <see cref="MeanCombiner"/> —
/// matches the SetiAstro "kappa-sigma" stack: reject outliers, average the
/// rest. Phase 12 adds median and exposure-weighted-mean combiners.
/// </remarks>
public interface IPixelCombiner
{
    /// <summary>
    /// Returns the combined value across kept entries. Entries with
    /// <paramref name="keepMask"/>[i] == 0 are excluded; entries with
    /// <paramref name="keepMask"/>[i] == 1 contribute. NaN values must
    /// also be excluded by the implementer (typically by treating them
    /// as if their mask were 0).
    /// </summary>
    float Combine(ReadOnlySpan<float> column, ReadOnlySpan<float> keepMask);
}
