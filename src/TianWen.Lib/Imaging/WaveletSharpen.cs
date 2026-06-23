using System;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Per-scale wavelet sharpening parameters (finest scale first). <see cref="Gains"/> length is the number
/// of a-trous detail layers; a gain &gt; 1 boosts that scale, &lt; 1 suppresses it. <see cref="DenoiseThresholds"/>
/// optionally soft-thresholds each layer before the gain to keep amplified noise down. Mirrors the
/// Registax/AstroSurface "wavelet layer" sliders.
/// </summary>
public sealed record WaveletSharpenOptions
{
    /// <summary>Per-layer gains, finest scale at index 0. Length defines the number of detail scales.</summary>
    public required ImmutableArray<float> Gains { get; init; }

    /// <summary>
    /// Optional per-layer soft-threshold denoise (same units as the [0,1] linear master). Empty = no denoise;
    /// otherwise must match <see cref="Gains"/> length. Most useful on the one or two finest layers, where
    /// sensor noise lives.
    /// </summary>
    public ImmutableArray<float> DenoiseThresholds { get; init; } = ImmutableArray<float>.Empty;

    /// <summary>
    /// Clamp the sharpened output to <c>[0, source.MaxValue]</c>. Sharpening overshoots edges (ringing) and
    /// can drive values below zero or past the white point, so clamping is on by default.
    /// </summary>
    public bool Clamp { get; init; } = true;

    /// <summary>Number of detail scales (= <see cref="Gains"/> length).</summary>
    public int ScaleCount => Gains.Length;

    /// <summary>A uniform <paramref name="gain"/> across <paramref name="scales"/> layers.</summary>
    public static WaveletSharpenOptions Uniform(int scales, float gain)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scales);
        var builder = ImmutableArray.CreateBuilder<float>(scales);
        for (var i = 0; i < scales; i++)
        {
            builder.Add(gain);
        }

        return new WaveletSharpenOptions { Gains = builder.MoveToImmutable() };
    }

    /// <summary>
    /// A planetary default: 6 a-trous layers (the Registax / AstroSurface convention), boosting fine and
    /// mid detail to pull out belt structure while leaving the coarsest scale near unity. The two finest
    /// layers carry a soft-threshold denoise so the gain does not amplify limb / sensor grain. Tuned on a
    /// real Jupiter SER -- 6 levels with these gains recover visibly more detail than a mild 5-level pass,
    /// and the denoise keeps the limb clean.
    /// </summary>
    public static WaveletSharpenOptions PlanetaryDefault { get; } = new()
    {
        Gains = [3.5f, 3.0f, 2.2f, 1.6f, 1.2f, 1.0f],
        DenoiseThresholds = [0.005f, 0.0025f, 0f, 0f, 0f, 0f],
    };
}

/// <summary>
/// Multi-scale (a-trous / starlet) wavelet sharpening applied to a linear image -- the final detail-recovery
/// step of the planetary lucky-imaging pipeline (Phase 7). Each channel is decomposed independently, the
/// detail layers are recombined with the per-scale gains/thresholds in <see cref="WaveletSharpenOptions"/>,
/// and a new image is returned (the source is left unchanged). CPU is the source of truth; the math is kept
/// separable so it can later be mirrored onto the Vulkan compute pipeline (CLAUDE.md CPU/GPU-mirror rule).
/// </summary>
public static class WaveletSharpen
{
    /// <summary>
    /// Sharpens every channel of <paramref name="source"/> and returns a new image with the same shape,
    /// bit depth, and metadata. With <see cref="WaveletSharpenOptions.Clamp"/> set, output is clamped to
    /// <c>[0, source.MaxValue]</c>.
    /// </summary>
    public static Image Sharpen(Image source, WaveletSharpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        int w = source.Width, h = source.Height, channels = source.ChannelCount;
        var gains = options.Gains.AsSpan();
        var thresholds = options.DenoiseThresholds.IsDefaultOrEmpty ? default : options.DenoiseThresholds.AsSpan();
        var max = source.MaxValue;
        var clampMax = float.IsFinite(max) && max > 0f ? max : 1f;

        var data = Image.CreateChannelData(channels, h, w);
        for (var c = 0; c < channels; c++)
        {
            var decomposition = ATrousWaveletTransform.Decompose(source.GetChannelSpan(c), w, h, options.ScaleCount);
            var reconstructed = decomposition.Reconstruct(gains, thresholds);

            var dst = MemoryMarshal.CreateSpan(ref data[c][0, 0], data[c].Length);
            if (options.Clamp)
            {
                for (var i = 0; i < dst.Length; i++)
                {
                    dst[i] = Math.Clamp(reconstructed[i], 0f, clampMax);
                }
            }
            else
            {
                reconstructed.CopyTo(dst);
            }
        }

        return new Image(data, source.BitDepth, source.MaxValue, source.MinValue, source.Pedestal, source.ImageMeta);
    }
}
