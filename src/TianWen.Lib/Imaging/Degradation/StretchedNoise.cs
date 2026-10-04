using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
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

    /// <summary>How <c>TryEstimateCalibration</c> reads a frame's noise.</summary>
    public enum NoiseEstimator
    {
        /// <summary>Every <see cref="EstimateBlockPx"/> px block's high-pass MAD over all its pixels, a low quantile of
        /// the anchors: what every bake and runner has used.</summary>
        Blocks = 0,

        /// <summary>
        /// E16c's candidate (docs/plans/denoiser-training.md, "E16c"): the noise read in the FINEST B3 starlet scale
        /// alone, each coefficient divided by what the model says the noise is at its pixel's level, the robust spread of
        /// the whole frame's quotients over that scale's own ratio to the pixel noise (the caller's, by how the master was
        /// integrated, since a registered master's noise is correlated and the ratio moves with it). Texture puts least of
        /// itself into that scale: on a frame filled with filaments of a few sigma, where the blocks read 2.0 times the
        /// noise, it reads 1.11 (white) and 1.18 (warped) times (<c>NoiseEstimatorFineBandProbe</c>).
        /// </summary>
        FineScale = 1,
    }

    /// <summary>
    /// The finest B3 starlet scale's sigma for unit WHITE noise (Starck and Murtagh's 0.889, pinned against
    /// <see cref="ATrousWaveletTransform"/> by <c>TheFinestScalesWhiteRatioIsTheStarletsOwn</c>). A registered master's noise
    /// is correlated over a pixel or two, which lowers it (0.70 for the warped field at 0.5).
    /// </summary>
    public const double WhiteNoiseFineScaleRatio = 0.889;

    /// <summary>The reach of the finest starlet scale's kernel, in pixels: a coefficient this near a ring or NaN pixel
    /// reads the fill, not the frame, and is left out.</summary>
    private const int FineScaleReachPx = 2;

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
        => TryEstimateCalibration(unitLinear, stretches, absent, NoiseEstimator.Blocks, null, out calibrations);

    /// <summary>
    /// <c>TryEstimateCalibration</c> by a chosen <paramref name="estimator"/>. <see cref="NoiseEstimator.FineScale"/>
    /// takes <paramref name="fineScaleRatios"/>, one per channel: the finest scale's sigma over the pixel sigma of this
    /// master's noise (<see cref="WhiteNoiseFineScaleRatio"/> for uncorrelated noise).
    /// </summary>
    public static bool TryEstimateCalibration(
        Image unitLinear, IReadOnlyList<ChannelStretch> stretches, BitMatrix? absent, NoiseEstimator estimator,
        IReadOnlyList<double>? fineScaleRatios, [NotNullWhen(true)] out LinearDegradation.NoiseCalibration[]? calibrations)
    {
        var (channels, width, height) = unitLinear.Shape;
        ArgumentOutOfRangeException.ThrowIfNotEqual(stretches.Count, channels);
        if (estimator == NoiseEstimator.FineScale && (fineScaleRatios is null || fineScaleRatios.Count != channels))
        {
            throw new ArgumentException($"the fine-scale estimator needs one ratio per channel ({channels})", nameof(fineScaleRatios));
        }

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

        // The channels are independent estimates, so they run side by side; each answer is the same as serially.
        var result = new LinearDegradation.NoiseCalibration[channels];
        var found = new bool[channels];
        ParallelFor.Run(channels, c =>
        {
            var ratio = fineScaleRatios is { } r ? r[c] : WhiteNoiseFineScaleRatio;
            found[c] = TryEstimateChannel(unitLinear, c, stretches[c], skip, estimator, ratio, out var calibration);
            result[c] = calibration;
        });
        if (Array.IndexOf(found, false) >= 0)
        {
            calibrations = null;
            return false;
        }
        calibrations = result;
        return true;
    }

    /// <summary>One channel of <see cref="TryEstimateCalibration"/>: its own background and its own anchor.</summary>
    private static bool TryEstimateChannel(
        Image unitLinear, int channel, in ChannelStretch stretch, bool[] skip, NoiseEstimator estimator, double fineScaleRatio,
        out LinearDegradation.NoiseCalibration calibration)
    {
        var (_, width, height) = unitLinear.Shape;
        var y = new float[width * height];
        var stretchBalance = stretch.MidtonesBalance;
        var stretchFloor = stretch.OrigMin;
        var plane = unitLinear.GetChannelArray(channel); // resolved once for the whole operation, not per row
        ParallelFor.Run(height, row =>
        {
            var linear = MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length).Slice(row * width, width);
            var yRow = y.AsSpan(row * width, width);
            for (var x = 0; x < width; x++)
            {
                var v = linear[x];
                yRow[x] = float.IsNaN(v) ? float.NaN : (float)Image.MidtonesTransferFunction(stretchBalance, Math.Max(0.0, v - stretchFloor));
            }
        });
        var filled = WithoutNaN(y);
        var low = Image.SeparableGaussianBlur(filled, width, height, EstimateHighPassSigmaPx);

        var (levels, mads) = ReadBlocks(y, low, skip, width, height);
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

        // The sky above is the blocks' under either estimator; only the anchor's reading differs.
        var anchor = estimator == NoiseEstimator.FineScale
            ? FineScaleAnchor(filled, skip, width, height, stretch, unit, fineScaleRatio)
            : AnchorOf(levels, mads, stretch, unit);
        calibration = new LinearDegradation.NoiseCalibration(unitLinear.Pedestal, background, anchor, 1);
        return true;
    }

    /// <summary>
    /// Every <see cref="EstimateBlockPx"/> px block's level (the median of its stretched pixels) and noise (1.4826 times
    /// the MAD of its high-pass), in block order; a block touching a <paramref name="skip"/> pixel is left out.
    /// </summary>
    private static (List<double> Levels, List<double> Mads) ReadBlocks(float[] y, float[] low, bool[] skip, int width, int height)
    {
        // Each block is read on its own, so block rows run in parallel into fixed slots, which keeps the readings
        // in the serial loop's order (the answer sorts them anyway). A block's level is its median alone: the MAD
        // beside it was computed and never read.
        var blocksX = width / EstimateBlockPx;
        var blocksY = height / EstimateBlockPx;
        var blockLevel = new double[blocksX * blocksY];
        var blockMad = new double[blocksX * blocksY];
        var blockOk = new bool[blocksX * blocksY];
        ParallelFor.Run(blocksY, bRow =>
        {
            var block = new float[EstimateBlockPx * EstimateBlockPx];
            var hp = new float[EstimateBlockPx * EstimateBlockPx];
            var by = bRow * EstimateBlockPx;
            for (var bCol = 0; bCol < blocksX; bCol++)
            {
                var bx = bCol * EstimateBlockPx;
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
                var slot = (bRow * blocksX) + bCol;
                blockLevel[slot] = StatisticsHelper.MedianFast(block.AsSpan());
                blockMad[slot] = 1.4826 * StatisticsHelper.MedianAndMad(hp.AsSpan()).Mad;
                blockOk[slot] = true;
            }
        });
        var levels = new List<double>();
        var mads = new List<double>();
        for (var slot = 0; slot < blockOk.Length; slot++)
        {
            if (blockOk[slot])
            {
                levels.Add(blockLevel[slot]);
                mads.Add(blockMad[slot]);
            }
        }
        return (levels, mads);
    }

    /// <summary>The <see cref="EstimateQuantile"/> of the blocks' anchors: each block's noise over what the model says a
    /// block at its level carries per unit anchor. 0 when no block reads.</summary>
    private static double AnchorOf(List<double> levels, List<double> mads, in ChannelStretch stretch, in LinearDegradation.NoiseCalibration unit)
    {
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
        return anchors.Count > 0 ? anchors[(int)Math.Clamp(anchors.Count * EstimateQuantile, 0, anchors.Count - 1)] : 0.0;
    }

    /// <summary>
    /// <see cref="NoiseEstimator.FineScale"/>'s anchor: every pixel's finest B3 starlet coefficient over the model's noise
    /// per unit anchor at its low-passed level (as the plane reads it) times <paramref name="fineScaleRatio"/>, then 1.4826
    /// times the median absolute deviation of those quotients over the frame. A pixel within the scale's reach of a ring or
    /// NaN pixel is left out, as is one the model gives no noise. 0 when none is left.
    /// </summary>
    private static double FineScaleAnchor(
        float[] filled, bool[] skip, int width, int height, in ChannelStretch stretch, in LinearDegradation.NoiseCalibration unit, double fineScaleRatio)
    {
        var n = width * height;
        var level = Image.SeparableGaussianBlur(filled, width, height, DefaultLevelSigmaPx);
        var smoother = new float[n];
        ATrousWaveletTransform.ConvolveSeparable(filled, smoother, new float[n], width, height, 1);
        var near = NearSkip(skip, width, height, FineScaleReachPx);
        var quotients = new float[n];
        var kept = new int[height];
        var channelStretch = stretch;
        var unitCalibration = unit;
        ParallelFor.Run(height, row =>
        {
            var k = row * width;
            for (var i = row * width; i < (row + 1) * width; i++)
            {
                if (near[i])
                {
                    continue;
                }
                var perUnit = fineScaleRatio * SigmaAt(level[i], channelStretch, unitCalibration, 1.0);
                if (perUnit > 0 && double.IsFinite(perUnit))
                {
                    quotients[k++] = (float)((filled[i] - smoother[i]) / perUnit);
                }
            }
            kept[row] = k - (row * width);
        });
        // Each row's quotients were written from its own start; close the gaps so one span holds them all.
        var count = 0;
        for (var row = 0; row < height; row++)
        {
            Array.Copy(quotients, row * width, quotients, count, kept[row]);
            count += kept[row];
        }
        return count > 0 ? 1.4826 * StatisticsHelper.MedianAndMad(quotients.AsSpan(0, count)).Mad : 0.0;
    }

    /// <summary>The <paramref name="skip"/> pixels grown by <paramref name="reach"/> in each direction (a box).</summary>
    private static bool[] NearSkip(bool[] skip, int width, int height, int reach)
    {
        var across = new bool[skip.Length];
        ParallelFor.Run(height, row =>
        {
            var start = row * width;
            for (var x = 0; x < width; x++)
            {
                if (!skip[start + x])
                {
                    continue;
                }
                for (var dx = Math.Max(0, x - reach); dx <= Math.Min(width - 1, x + reach); dx++)
                {
                    across[start + dx] = true;
                }
            }
        });
        var grown = new bool[skip.Length];
        ParallelFor.Run(height, row =>
        {
            for (var dy = Math.Max(0, row - reach); dy <= Math.Min(height - 1, row + reach); dy++)
            {
                for (var x = 0; x < width; x++)
                {
                    grown[(row * width) + x] |= across[(dy * width) + x];
                }
            }
        });
        return grown;
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

        // The fill is the finite values' sorted[n / 2]; a selection finds that value without sorting the frame, which
        // on a warped sub (its NaN canvas edge makes every channel take this path) was a full sort of every pixel.
        var finite = ArrayPool<float>.Shared.Rent(src.Length);
        float fill;
        try
        {
            var n = StatisticsHelper.CompactFinite(src, finite);
            fill = n > 0 ? StatisticsHelper.NthSmallest(finite.AsSpan(0, n), n / 2) : 0f;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(finite);
        }
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
