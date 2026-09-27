using System;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging.Degradation;

/// <summary>
/// The per-pixel noise sigma of a frame AFTER the NAFNet input stretch, as the denoiser's conditioning plane
/// (docs/plans/denoiser-training.md, run log "Detail kept", E16).
///
/// <para><b>Why a plane and not a number.</b> Every model so far conditioned on ONE value per tile, the MAD of
/// the tile's darkest half. After the stretch the noise is not one value: measured from real half pairs it falls
/// to half the sky's at a stretched level of 0.65 and to a fifth near 0.85, because the stretch flattens the
/// highlights faster than shot noise grows, so a bright core was told the sky's noise and smoothed as noise. And
/// on a tile filled with nebulosity the darkest-half MAD reads texture as noise (twice the true value on eta
/// Car). This type answers the question the scalar could not: how noisy is THIS pixel.</para>
///
/// <para><b>The model.</b> Linear noise follows <see cref="LinearDegradation.NoiseCalibration.SigmaAt"/> (shot
/// noise, variance linear in signal above the pedestal, anchored at the background), which is exactly how the
/// exporter injects it; the stretch <c>y = MTF(m, x - origMin)</c> scales it by its local slope. Evaluated at a
/// stretched level <c>y</c>, the linear level is recovered by the stretch's own inverse (<c>MTF(1 - m, y)</c>),
/// so the plane can be computed from the STRETCHED tile a runner feeds the network, with the stretch parameters
/// it applied. One function for the exporter's training plane (the calibration known) and the runner's inference
/// plane (the calibration estimated), so the two cannot drift.</para>
///
/// <para><b>Smoothness is not optional.</b> The level is low-passed before the model is evaluated and the plane
/// after: a model that never saw a varying plane drew a stepped one's edges into the image as contour lines
/// (the first probe, 2026-09-28).</para>
/// </summary>
public static class StretchedNoise
{
    /// <summary>
    /// The scalar plane's units, kept so a per-pixel plane means what the scalar meant: the trainer's
    /// <c>bg_sigma</c> is the median minus the first quartile of the darkest half, which for pure Gaussian noise
    /// is 0.6745 sigma, times its <c>SIGMA_SCALE</c> of 100.
    /// </summary>
    public const double PlaneScale = 0.6745 * 100.0;

    /// <summary>Low-pass of the stretched level before the noise model is evaluated, in pixels. Reads the
    /// local signal, not the pixel's own noise.</summary>
    public const float DefaultLevelSigmaPx = 2f;

    /// <summary>Low-pass of the finished plane, in pixels.</summary>
    public const float DefaultPlaneSigmaPx = 4f;

    /// <summary>One channel's stretch, as <see cref="Image.MtfStretchParameters"/> measured it.</summary>
    /// <param name="MidtonesBalance">The channel's midtones balance.</param>
    /// <param name="OrigMin">The channel's floor, subtracted before the MTF, in the linear frame's units.</param>
    public readonly record struct ChannelStretch(double MidtonesBalance, double OrigMin);

    /// <summary>
    /// The noise sigma, in STRETCHED units, of one channel at stretched level <paramref name="stretched"/>.
    /// </summary>
    /// <param name="stretched">The stretched level, clamped to [0, 1].</param>
    /// <param name="stretch">The channel's stretch.</param>
    /// <param name="calibration">The linear noise model, in the linear frame's units.</param>
    /// <param name="depthScale">The noise's depth as a multiple of one sub's (see
    /// <see cref="LinearDegradation.NoiseCalibration.SigmaAt"/>).</param>
    public static double SigmaAt(double stretched, in ChannelStretch stretch, in LinearDegradation.NoiseCalibration calibration, double depthScale)
    {
        var y = Math.Clamp(stretched, 0.0, 1.0);
        var shifted = Image.MidtonesTransferFunction(1.0 - stretch.MidtonesBalance, y);
        var slope = Image.MidtonesTransferFunctionSlope(stretch.MidtonesBalance, shifted);
        return slope * calibration.SigmaAt(shifted + stretch.OrigMin, depthScale);
    }

    /// <summary>
    /// The conditioning plane of a stretched tile, in <see cref="PlaneScale"/> units: per channel the noise at the
    /// low-passed level, the channels combined as the trainer's luminance combines them (their mean, so the noise
    /// of the mean is the root sum of squares over the count), then low-passed.
    /// </summary>
    /// <param name="stretchedChannels">The tile's stretched channels, row-major, <paramref name="width"/> by
    /// <paramref name="height"/> each.</param>
    /// <param name="width">Tile width.</param>
    /// <param name="height">Tile height.</param>
    /// <param name="stretches">Each channel's stretch; one per channel.</param>
    /// <param name="calibration">The linear noise model.</param>
    /// <param name="depthScale">The noise's depth as a multiple of one sub's.</param>
    /// <param name="levelSigmaPx">Low-pass of the level (<see cref="DefaultLevelSigmaPx"/>).</param>
    /// <param name="planeSigmaPx">Low-pass of the plane (<see cref="DefaultPlaneSigmaPx"/>).</param>
    public static float[] Plane(
        IReadOnlyList<float[]> stretchedChannels,
        int width,
        int height,
        IReadOnlyList<ChannelStretch> stretches,
        in LinearDegradation.NoiseCalibration calibration,
        double depthScale,
        float levelSigmaPx = DefaultLevelSigmaPx,
        float planeSigmaPx = DefaultPlaneSigmaPx)
    {
        var channels = stretchedChannels.Count;
        ArgumentOutOfRangeException.ThrowIfZero(channels);
        ArgumentOutOfRangeException.ThrowIfNotEqual(stretches.Count, channels);
        var n = width * height;
        var sumSq = new double[n];
        for (var c = 0; c < channels; c++)
        {
            var src = stretchedChannels[c];
            ArgumentOutOfRangeException.ThrowIfNotEqual(src.Length, n);
            var level = levelSigmaPx > 0 ? Image.SeparableGaussianBlur(WithoutNaN(src), width, height, levelSigmaPx) : WithoutNaN(src);
            var stretch = stretches[c];
            for (var i = 0; i < n; i++)
            {
                var s = SigmaAt(level[i], stretch, calibration, depthScale);
                sumSq[i] += s * s;
            }
        }

        var plane = new float[n];
        for (var i = 0; i < n; i++)
        {
            plane[i] = (float)(PlaneScale * Math.Sqrt(sumSq[i]) / channels);
        }
        return planeSigmaPx > 0 ? Image.SeparableGaussianBlur(plane, width, height, planeSigmaPx) : plane;
    }

    /// <summary>A copy with every NaN replaced by the finite median, so a blur cannot spread one. A plane over a
    /// NaN pixel is not a measurement either way; the median keeps its neighbours' plane honest.</summary>
    private static float[] WithoutNaN(float[] src)
    {
        var hasNaN = false;
        foreach (var v in src)
        {
            if (float.IsNaN(v))
            {
                hasNaN = true;
                break;
            }
        }
        if (!hasNaN)
        {
            return src;
        }

        var finite = new List<float>(src.Length);
        foreach (var v in src)
        {
            if (!float.IsNaN(v))
            {
                finite.Add(v);
            }
        }
        finite.Sort();
        var fill = finite.Count > 0 ? finite[finite.Count / 2] : 0f;
        var copy = (float[])src.Clone();
        for (var i = 0; i < copy.Length; i++)
        {
            if (float.IsNaN(copy[i]))
            {
                copy[i] = fill;
            }
        }
        return copy;
    }
}
