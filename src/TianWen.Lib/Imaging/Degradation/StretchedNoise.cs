using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using TianWen.Lib.Stat;

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
    /// <param name="calibration">The linear noise model, one for every channel: the injected noise's, which the
    /// degrade export adds to each channel from this one calibration.</param>
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
        var each = new LinearDegradation.NoiseCalibration[stretchedChannels.Count];
        Array.Fill(each, calibration);
        return Plane(stretchedChannels, width, height, stretches, each, depthScale, levelSigmaPx, planeSigmaPx);
    }

    /// <summary>
    /// <see cref="Plane(IReadOnlyList{float[]}, int, int, IReadOnlyList{ChannelStretch}, in LinearDegradation.NoiseCalibration, double, float, float)"/>
    /// with each channel's OWN calibration, as a real frame needs (<see cref="TryEstimateCalibration"/>).
    /// </summary>
    /// <param name="stretchedChannels">The tile's stretched channels.</param>
    /// <param name="width">Tile width.</param>
    /// <param name="height">Tile height.</param>
    /// <param name="stretches">Each channel's stretch; one per channel.</param>
    /// <param name="calibrations">Each channel's linear noise model; one per channel.</param>
    /// <param name="depthScale">The noise's depth as a multiple of one sub's.</param>
    /// <param name="levelSigmaPx">Low-pass of the level (<see cref="DefaultLevelSigmaPx"/>).</param>
    /// <param name="planeSigmaPx">Low-pass of the plane (<see cref="DefaultPlaneSigmaPx"/>).</param>
    public static float[] Plane(
        IReadOnlyList<float[]> stretchedChannels,
        int width,
        int height,
        IReadOnlyList<ChannelStretch> stretches,
        IReadOnlyList<LinearDegradation.NoiseCalibration> calibrations,
        double depthScale,
        float levelSigmaPx = DefaultLevelSigmaPx,
        float planeSigmaPx = DefaultPlaneSigmaPx)
    {
        var channels = stretchedChannels.Count;
        ArgumentOutOfRangeException.ThrowIfZero(channels);
        ArgumentOutOfRangeException.ThrowIfNotEqual(stretches.Count, channels);
        ArgumentOutOfRangeException.ThrowIfNotEqual(calibrations.Count, channels);
        var n = width * height;
        var sumSq = new double[n];
        for (var c = 0; c < channels; c++)
        {
            var src = stretchedChannels[c];
            ArgumentOutOfRangeException.ThrowIfNotEqual(src.Length, n);
            var level = levelSigmaPx > 0 ? Image.SeparableGaussianBlur(WithoutNaN(src), width, height, levelSigmaPx) : WithoutNaN(src);
            var stretch = stretches[c];
            var calibration = calibrations[c];
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

    /// <summary>Block side, in pixels, of <see cref="EstimateCalibration"/>'s local noise readings.</summary>
    public const int EstimateBlockPx = 32;

    /// <summary>The high-pass <see cref="EstimateCalibration"/> reads noise through: the frame minus its own
    /// low-pass at this sigma, which removes gradients and large structure and keeps nearly all of a stack's
    /// noise (correlated over a pixel or two, far inside this scale).</summary>
    public const float EstimateHighPassSigmaPx = 4f;

    /// <summary>Which quantile of the per-block anchors <see cref="EstimateCalibration"/> answers with: a low
    /// one, because texture only ever ADDS to a block's reading, so the quietest blocks are the honest ones.</summary>
    public const double EstimateQuantile = 0.25;

    /// <summary>
    /// A frame's OWN noise calibration, estimated from the frame alone, one per CHANNEL: what a runner has at
    /// inference, and what an eval plane has for a half-master. Each is in the units of
    /// <see cref="LinearDegradation.NoiseCalibration"/> with <c>StackedFrames</c> 1, so a depth of 1 is this
    /// frame's own noise.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not the darkest half of a tile.</b> That is the scalar plane's estimator, and on a frame
    /// filled with nebulosity its darkest half is texture: it read eta Car's noise at twice the truth. Here the
    /// frame is split into blocks, each block's noise is read through a high-pass, and each reading is divided by
    /// what the noise MODEL says a block at that level carries (the same <see cref="SigmaAt"/> the plane uses),
    /// so every block estimates the same anchor. Texture only adds to a block's reading, so a low quantile of the
    /// anchors is the honest one, and the model's level dependence is what lets bright smooth blocks vote at all
    /// instead of reading as quiet sky.</para>
    /// <para><b>Why each channel on its own.</b> The model's shot-noise ramp (variance linear in signal) holds
    /// WITHIN a channel, not across them: a colour sensor's channels are built from different numbers of
    /// photosites (a Bayer drizzle gives green twice red's or blue's) and sit at different sky levels, so one
    /// channel's anchor carried to another at its own level misreads it. Anchoring every channel on channel 0 did
    /// exactly that, and against the half-pair truth of the eleven eval fields it over-read green by up to 1.6x
    /// and the luminance plane by 1.15x typical; each channel anchored on its own cut the typical error from
    /// x1.22 to x1.13 (docs/plans/denoiser-training.md, "E16's machinery").</para>
    /// </remarks>
    /// <param name="unitLinear">The linear frame, in the units the stretch was measured in.</param>
    /// <param name="stretches">The frame's stretch, one per channel.</param>
    /// <param name="absent">The canvas ring (<see cref="Image.AbsentPixels"/>): a block touching it is skipped.</param>
    public static LinearDegradation.NoiseCalibration[] EstimateCalibration(
        Image unitLinear, IReadOnlyList<ChannelStretch> stretches, BitMatrix? absent = null)
        => TryEstimateCalibration(unitLinear, stretches, absent, out var calibrations)
            ? calibrations
            : throw new ArgumentException($"no {EstimateBlockPx} px block of the frame is free of the canvas ring and NaN", nameof(unitLinear));

    /// <summary>
    /// <see cref="EstimateCalibration"/> for a caller that has a use for the frame without it: false when no
    /// <see cref="EstimateBlockPx"/> px block is free of the canvas ring and NaN in every channel (a frame smaller
    /// than one block, or one that is all ring), which leaves nothing to estimate from.
    /// </summary>
    public static bool TryEstimateCalibration(
        Image unitLinear, IReadOnlyList<ChannelStretch> stretches, BitMatrix? absent,
        [NotNullWhen(true)] out LinearDegradation.NoiseCalibration[]? calibrations)
    {
        var (channels, width, height) = unitLinear.Shape;
        ArgumentOutOfRangeException.ThrowIfNotEqual(stretches.Count, channels);

        // One block set for every channel: a block is read only where no channel is absent or NaN there.
        var skip = new bool[width * height];
        for (var c = 0; c < channels; c++)
        {
            var linear = unitLinear.GetChannelSpan(c);
            for (var i = 0; i < skip.Length; i++)
            {
                skip[i] |= float.IsNaN(linear[i]);
            }
        }
        if (absent is { } ring)
        {
            for (var row = 0; row < height; row++)
            {
                for (var x = ring.NextSetBit(row, 0); x >= 0; x = ring.NextSetBit(row, x + 1))
                {
                    skip[(row * width) + x] = true;
                }
            }
        }

        var result = new LinearDegradation.NoiseCalibration[channels];
        for (var c = 0; c < channels; c++)
        {
            if (!TryEstimateChannel(unitLinear, c, stretches[c], skip, out result[c]))
            {
                calibrations = null;
                return false;
            }
        }
        calibrations = result;
        return true;
    }

    /// <summary>One channel of <see cref="TryEstimateCalibration"/>: its own background and its own anchor.</summary>
    private static bool TryEstimateChannel(Image unitLinear, int channel, in ChannelStretch stretch, bool[] skip, out LinearDegradation.NoiseCalibration calibration)
    {
        var (_, width, height) = unitLinear.Shape;
        var linear = unitLinear.GetChannelSpan(channel);
        var y = new float[width * height];
        for (var i = 0; i < y.Length; i++)
        {
            var v = linear[i];
            y[i] = float.IsNaN(v) ? float.NaN : (float)Image.MidtonesTransferFunction(stretch.MidtonesBalance, Math.Max(0.0, v - stretch.OrigMin));
        }
        var low = Image.SeparableGaussianBlur(WithoutNaN(y), width, height, EstimateHighPassSigmaPx);

        var levels = new List<double>();
        var mads = new List<double>();
        var block = new float[EstimateBlockPx * EstimateBlockPx];
        var hp = new float[EstimateBlockPx * EstimateBlockPx];
        for (var by = 0; by + EstimateBlockPx <= height; by += EstimateBlockPx)
        {
            for (var bx = 0; bx + EstimateBlockPx <= width; bx += EstimateBlockPx)
            {
                var ok = true;
                var k = 0;
                for (var yy = by; yy < by + EstimateBlockPx && ok; yy++)
                {
                    for (var xx = bx; xx < bx + EstimateBlockPx; xx++)
                    {
                        var i = (yy * width) + xx;
                        if (skip[i])
                        {
                            ok = false;
                            break;
                        }
                        block[k] = y[i];
                        hp[k] = y[i] - low[i];
                        k++;
                    }
                }
                if (!ok)
                {
                    continue;
                }
                var (median, _) = StatisticsHelper.MedianAndMad(block.AsSpan());
                var (_, mad) = StatisticsHelper.MedianAndMad(hp.AsSpan());
                levels.Add(median);
                mads.Add(1.4826 * mad);
            }
        }
        if (levels.Count == 0)
        {
            calibration = default;
            return false;
        }

        // The sky: the darkest 30 percent of blocks, whose median level fixes the background the ramp anchors at.
        var sorted = new List<double>(levels);
        sorted.Sort();
        var darkest = sorted.GetRange(0, Math.Max(1, (int)(sorted.Count * 0.3)));
        var skyLevel = darkest[darkest.Count / 2];
        var background = Image.MidtonesTransferFunction(1.0 - stretch.MidtonesBalance, skyLevel) + stretch.OrigMin;
        var unit = new LinearDegradation.NoiseCalibration(unitLinear.Pedestal, background, 1.0, 1);

        var anchors = new List<double>(levels.Count);
        for (var b = 0; b < levels.Count; b++)
        {
            var perUnit = SigmaAt(levels[b], stretch, unit, 1.0);
            if (perUnit > 0 && double.IsFinite(perUnit) && mads[b] > 0)
            {
                anchors.Add(mads[b] / perUnit);
            }
        }
        anchors.Sort();
        var anchor = anchors.Count > 0 ? anchors[(int)Math.Clamp(anchors.Count * EstimateQuantile, 0, anchors.Count - 1)] : 0.0;
        calibration = new LinearDegradation.NoiseCalibration(unitLinear.Pedestal, background, anchor, 1);
        return true;
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
