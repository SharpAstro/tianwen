using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Degradation;

/// <summary>
/// A session's noise, measured per channel from its two half-masters, as the degrade export's anchor for the noise
/// it injects (docs/plans/denoiser-training.md, "E16b, pre-registered", and its amendment).
///
/// <para><b>Why the half pairs.</b> Half A and half B integrate disjoint halves of one session's subs onto the
/// master's canvas and through the master's own unit division, so their difference holds no structure (both carry
/// the same sky) and its scatter is the independent noise the master is made of, on the master's scale. It is the
/// truth the whole E16 estimator campaign was checked against. A sub's own calibration cannot stand in for it: the
/// stacker normalises every sub before integrating it and the manifest does not record by how much, so a sub's
/// sigma has no known way onto the master's scale. A MAD of a cell (the older anchor) reads a bright cell's
/// structure as noise.</para>
///
/// <para><b>What is measured.</b> The bake keeps only the stretched half tiles, each stretched by its OWN stretch,
/// which its manifest row records; each is taken back to linear through that stretch's inverse
/// (<see cref="Unstretch"/>) before the two are differenced. Only QUIET pixels count: those whose master level
/// (<see cref="Level"/>, the scorer's low-passed luminance) lies in [<see cref="QuietLevelMin"/>,
/// <see cref="QuietLevelMax"/>), inside the <see cref="RimPx"/> rim, which is the mask the per-channel anchor was
/// validated on. The calibration's background is the master's median over the same pixels, so the sigma and the
/// level it belongs to come from one set of pixels.</para>
/// </summary>
public static class HalfPairNoise
{
    /// <summary>Lower edge of the quiet level band, in stretched units.</summary>
    public const double QuietLevelMin = 0.15;

    /// <summary>Upper edge of the quiet level band, in stretched units.</summary>
    public const double QuietLevelMax = 0.30;

    /// <summary>The tile rim left out of every count, in pixels, as the scorer leaves it out.</summary>
    public const int RimPx = 16;

    /// <summary>The low-pass of the luminance a pixel's level is read from, in pixels: the scorer's
    /// <c>DETAIL_LEVEL_SIGMA</c>, so a level here is the level a score is binned by.</summary>
    public const float LevelSigmaPx = 3f;

    /// <summary>Fewest quiet pixels per channel a calibration is taken from.</summary>
    public const int MinQuietPixels = 4096;

    /// <summary>The linear value of a stretched sample: the input stretch <c>y = MTF(m, x - origMin)</c> inverted,
    /// <c>x = MTF(1 - m, y) + origMin</c>, the inverse <see cref="StretchedNoise.SigmaAt"/> uses.</summary>
    public static double Unstretch(double stretched, in StretchedNoise.ChannelStretch stretch)
        => Image.MidtonesTransferFunction(1.0 - stretch.MidtonesBalance, Math.Clamp(stretched, 0.0, 1.0)) + stretch.OrigMin;

    /// <summary>
    /// The level of every pixel of a stretched CHW tile: the channels' mean (the trainer's and the scorer's
    /// luminance), low-passed by <see cref="LevelSigmaPx"/>.
    /// </summary>
    public static float[] Level(ReadOnlySpan<float> chw, int channels, int size)
    {
        var n = size * size;
        ArgumentOutOfRangeException.ThrowIfNotEqual(chw.Length, channels * n);
        var lum = new float[n];
        for (var c = 0; c < channels; c++)
        {
            var plane = chw.Slice(c * n, n);
            for (var i = 0; i < n; i++)
            {
                lum[i] += plane[i];
            }
        }
        var inv = 1f / channels;
        for (var i = 0; i < n; i++)
        {
            lum[i] *= inv;
        }
        return Image.SeparableGaussianBlur(lum, size, size, LevelSigmaPx);
    }

    /// <summary>
    /// Collects one session's quiet half-pair differences, cell by cell, then turns them into a calibration per
    /// channel. Not thread-safe: one session's cells are added from one loop.
    /// </summary>
    public sealed class Accumulator
    {
        private readonly int _channels;
        private readonly List<float>[] _differences;
        private readonly List<float>[] _levels;

        /// <summary>An empty accumulator for a frame of <paramref name="channels"/> channels.</summary>
        public Accumulator(int channels)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
            _channels = channels;
            _differences = new List<float>[channels];
            _levels = new List<float>[channels];
            for (var c = 0; c < channels; c++)
            {
                _differences[c] = [];
                _levels[c] = [];
            }
        }

        /// <summary>Quiet pixels collected so far (the same count in every channel).</summary>
        public int QuietPixels => _differences[0].Count;

        /// <summary>
        /// Adds one cell: the master's stretched tile (for the level) and linear tile (for the background), and
        /// the two stretched half tiles with each half's own stretch. All CHW, <paramref name="size"/> square.
        /// </summary>
        public void Add(
            ReadOnlySpan<float> masterStretched,
            ReadOnlySpan<float> masterLinear,
            ReadOnlySpan<float> halfA,
            IReadOnlyList<StretchedNoise.ChannelStretch> halfAStretch,
            ReadOnlySpan<float> halfB,
            IReadOnlyList<StretchedNoise.ChannelStretch> halfBStretch,
            int size)
        {
            var n = size * size;
            ArgumentOutOfRangeException.ThrowIfNotEqual(masterLinear.Length, _channels * n);
            ArgumentOutOfRangeException.ThrowIfNotEqual(halfA.Length, _channels * n);
            ArgumentOutOfRangeException.ThrowIfNotEqual(halfB.Length, _channels * n);
            ArgumentOutOfRangeException.ThrowIfNotEqual(halfAStretch.Count, _channels);
            ArgumentOutOfRangeException.ThrowIfNotEqual(halfBStretch.Count, _channels);

            var level = Level(masterStretched, _channels, size);
            for (var y = RimPx; y < size - RimPx; y++)
            {
                for (var x = RimPx; x < size - RimPx; x++)
                {
                    var i = (y * size) + x;
                    if (level[i] is < (float)QuietLevelMin or >= (float)QuietLevelMax)
                    {
                        continue;
                    }
                    for (var c = 0; c < _channels; c++)
                    {
                        var j = (c * n) + i;
                        var a = Unstretch(halfA[j], halfAStretch[c]);
                        var b = Unstretch(halfB[j], halfBStretch[c]);
                        _differences[c].Add((float)(a - b));
                        _levels[c].Add(masterLinear[j]);
                    }
                }
            }
        }

        /// <summary>
        /// The calibration per channel: one sub's sigma at the quiet background, from the half pairs' scatter.
        /// A half integrates about half the master's <paramref name="stackedFrames"/>, so
        /// <c>sigma_half = 1.4826 MAD(A - B) / sqrt 2</c> and one sub's is <c>sigma_half sqrt(N / 2)</c>; at the
        /// master's depth (1/sqrt N) that is <c>sigma_half / sqrt 2</c>, the master's own noise, whatever N is.
        /// False when fewer than <see cref="MinQuietPixels"/> quiet pixels were collected.
        /// </summary>
        public bool TryCalibrate(double pedestal, int stackedFrames, [NotNullWhen(true)] out LinearDegradation.NoiseCalibration[]? calibrations)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stackedFrames);
            if (QuietPixels < MinQuietPixels)
            {
                calibrations = null;
                return false;
            }
            calibrations = new LinearDegradation.NoiseCalibration[_channels];
            for (var c = 0; c < _channels; c++)
            {
                var (_, mad) = StatisticsHelper.MedianAndMad(_differences[c].ToArray().AsSpan());
                var background = StatisticsHelper.MedianFast(_levels[c].ToArray().AsSpan());
                var sigmaHalf = 1.4826 * mad / Math.Sqrt(2.0);
                var oneSub = sigmaHalf * Math.Sqrt(stackedFrames / 2.0);
                calibrations[c] = new LinearDegradation.NoiseCalibration(pedestal, background, oneSub, stackedFrames);
            }
            return true;
        }
    }
}
