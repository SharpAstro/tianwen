using System;

namespace TianWen.Lib.Imaging;

/// <summary>
/// The result of an <see cref="ATrousWaveletTransform.Decompose"/>: <see cref="ScaleCount"/> detail layers
/// (finest first) plus a smooth <see cref="Residual"/>. Each layer is a flat row-major plane of
/// <see cref="Width"/> x <see cref="Height"/>. <see cref="Reconstruct"/> recomposes the plane, applying a
/// per-layer gain (and an optional per-layer soft-threshold denoise) -- the multi-scale sharpener.
/// </summary>
public sealed class WaveletDecomposition
{
    private readonly float[][] _detail;
    private readonly float[] _residual;

    internal WaveletDecomposition(float[][] detail, float[] residual, int width, int height)
    {
        _detail = detail;
        _residual = residual;
        Width = width;
        Height = height;
    }

    /// <summary>Number of detail layers.</summary>
    public int ScaleCount => _detail.Length;

    public int Width { get; }

    public int Height { get; }

    /// <summary>The detail (wavelet) plane at <paramref name="scale"/> (0 = finest).</summary>
    public ReadOnlySpan<float> Detail(int scale) => _detail[scale];

    /// <summary>The smoothest approximation left after every detail layer was peeled off.</summary>
    public ReadOnlySpan<float> Residual => _residual;

    /// <summary>
    /// Recomposes the plane as <c>residual + sum_j gain[j] * softThreshold(detail[j], threshold[j])</c>.
    /// Identity gains (all 1) with no thresholds return the original plane to float precision -- the
    /// exact-reconstruction property of the a-trous transform.
    /// </summary>
    /// <param name="gains">Per-layer gains, finest first. Must have length <see cref="ScaleCount"/>.</param>
    /// <param name="thresholds">
    /// Optional per-layer soft-threshold (same units as the pixel values); coefficients with magnitude below
    /// the threshold are zeroed and the rest shrunk toward zero, suppressing per-scale noise. Empty = none;
    /// otherwise must have length <see cref="ScaleCount"/>.
    /// </param>
    public float[] Reconstruct(ReadOnlySpan<float> gains, ReadOnlySpan<float> thresholds = default)
    {
        if (gains.Length != ScaleCount)
        {
            throw new ArgumentException($"Expected {ScaleCount} gains, got {gains.Length}.", nameof(gains));
        }

        if (!thresholds.IsEmpty && thresholds.Length != ScaleCount)
        {
            throw new ArgumentException($"Expected 0 or {ScaleCount} thresholds, got {thresholds.Length}.", nameof(thresholds));
        }

        var result = (float[])_residual.Clone();
        for (var j = 0; j < ScaleCount; j++)
        {
            var g = gains[j];
            var t = thresholds.IsEmpty ? 0f : thresholds[j];
            var d = _detail[j];

            if (t > 0f)
            {
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] += g * SoftThreshold(d[i], t);
                }
            }
            else if (g != 0f)
            {
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] += g * d[i];
                }
            }
        }

        return result;
    }

    // Soft threshold: zero below t, shrink the remainder toward zero by t (continuous, no step artefacts).
    private static float SoftThreshold(float v, float t)
    {
        var a = MathF.Abs(v);
        return a <= t ? 0f : MathF.CopySign(a - t, v);
    }
}
