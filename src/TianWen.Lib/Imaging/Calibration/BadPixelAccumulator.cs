using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Imaging.Calibration
{
    /// <summary>
    /// Session-derived bad-pixel map: accumulates per-pixel outlier PERSISTENCE across a group's
    /// own calibrated lights, in SENSOR space, and flags the pixels that are outliers in nearly
    /// every frame. The registration transforms are consulted only to prove the session moved.
    ///
    /// <para><b>Why the dark is not enough.</b> A dark-derived mask
    /// (<see cref="BadPixelDetection.BuildMaskFromDark"/>) can only flag pixels hot IN THAT DARK,
    /// and the measured A/B said that is not the population that matters: at sigma 8 with the
    /// converged iterative loop, residual hot-pixel clusters in a drizzled master went 52 to 35 and
    /// six survived byte-identically, meaning those pixels were never flagged at all. The dark was
    /// 120s/-10C against 60s/-5C lights, and many defects are non-linear or telegraph-unstable, so
    /// no dark at any settings describes them. The lights themselves do.</para>
    ///
    /// <para><b>The separation mechanism, which registration hands over for free:</b> a STAR is
    /// fixed in sky space, so under dither (or drift) it lands on a DIFFERENT sensor pixel each
    /// frame; a DEFECT is fixed in sensor space, so it is an outlier at the SAME sensor pixel in
    /// nearly all of them. Count per-frame local outliers per sensor pixel and threshold on the
    /// fraction of frames (<see cref="MinOutlierFraction"/>): a star contaminates a given pixel in
    /// only the minority of frames where it lands there, a defect in essentially all.</para>
    ///
    /// <para><b>The per-frame outlier test</b> compares each pixel against the median of its 8
    /// same-CFA-subplane neighbours (offset <c>step</c> = 2 on an RGGB mosaic, 1 on mono -- a
    /// cross-plane neighbourhood would make every pixel of a colour-separated scene an "outlier"),
    /// at a threshold of sigma * 1.4826 * the subplane's own strided MAD. The scene's structure
    /// inflates that MAD relative to pure pixel noise, which errs in the safe direction: the
    /// per-frame test only needs to be strong enough that random noise cannot repeat at the same
    /// pixel in 80% of frames, because PERSISTENCE is what carries the specificity here.</para>
    ///
    /// <para><b>Degenerate case:</b> an unmoved session (no dither, no drift) cannot separate the
    /// two populations by this method, so <see cref="BuildMask"/> refuses (returns null) below
    /// <see cref="MinTranslationSpreadPx"/> of registered translation spread and the caller falls
    /// back to the dark-derived mask. The precondition is self-consistent: dither is also what
    /// makes a defect visible as a drizzled cluster in the first place.</para>
    /// </summary>
    public sealed class BadPixelAccumulator(float outlierSigma = BadPixelAccumulator.DefaultOutlierSigma)
    {
        /// <summary>Per-frame outlier threshold in Gaussian sigmas of the subplane MAD, matching
        /// the dark detector's <c>sigmaThreshold</c> unit so both producers share the caller's one
        /// <c>HotPixelSigma</c> knob.</summary>
        public const float DefaultOutlierSigma = 8f;

        /// <summary>
        /// Fraction of accumulated frames a pixel must be an outlier in to be flagged. 0.8 leaves
        /// slack for the frames where a dithered star happens to sit on the pixel, a cloud flattens
        /// the scene, or clipping hides the defect, while staying far above what dithered stars can
        /// reach: with the translation spread <see cref="BuildMask"/> demands, a star's core covers
        /// any one sensor pixel in well under half the frames.
        /// </summary>
        public const float MinOutlierFraction = 0.8f;

        /// <summary>Below this many accumulated frames a persistence fraction is too coarse to
        /// mean anything (at 8 frames the 0.8 bar is 7 of 8, one noise hit from either verdict), so
        /// <see cref="BuildMask"/> refuses and the caller falls back to the dark-derived mask.</summary>
        public const int MinFramesForMask = 10;

        /// <summary>
        /// Minimum RMS radial spread of the registered translations, in sensor px, for the
        /// star/defect separation to hold. Derivation rather than taste: a bright star keeps a
        /// pixel above the 8-neighbour test while its centre is within ~2 px, so with a Gaussian
        /// dither of combined sigma 3 px the star occupies any one pixel in about a third of the
        /// frames -- safely under <see cref="MinOutlierFraction"/> -- while at combined sigma 1.5
        /// that fraction crosses 0.8 and star cores would be flagged as defects. Commanded dither
        /// is typically 5-30 px and unguided drift tens of px, so real moved sessions clear this
        /// easily; what it refuses is the genuinely parked session.
        /// </summary>
        public const float MinTranslationSpreadPx = 3f;

        /// <summary>Stride (in subplane units) of the sample the per-subplane MAD is estimated
        /// from, mirroring <see cref="BadPixelDetection"/>'s strided estimation.</summary>
        private const int StatStride = 8;

        /// <summary>Conventional MAD-to-Gaussian-sigma factor (see <see cref="BadPixelDetection"/>).</summary>
        private const float GaussianFactor = 1.4826f;

        private readonly float _outlierSigma = outlierSigma;
        private ushort[]? _counts;
        private int _width;
        private int _height;
        private int _step;
        private int _skippedFrames;

        /// <summary>Frames whose outliers were accumulated. The denominator of the persistence
        /// fraction.</summary>
        public int FramesAccumulated { get; private set; }

        /// <summary>
        /// Accumulates one calibrated frame's local outliers, in sensor space. Call once per light,
        /// with the SAME calibration the integration will use, before any debayer or warp. Frames
        /// that cannot participate (multi-channel, mismatched geometry, or a degenerate noise
        /// estimate) are skipped and counted; they neither accumulate nor inflate the denominator.
        /// </summary>
        public void Accumulate(Image calibrated)
        {
            if (calibrated.ChannelCount != 1)
            {
                _skippedFrames++;
                return;
            }
            var data = calibrated.GetChannelArray(0);
            var h = data.GetLength(0);
            var w = data.GetLength(1);
            if (_counts is null)
            {
                if ((long)w * h > int.MaxValue)
                {
                    _skippedFrames++;
                    return;
                }
                _width = w;
                _height = h;
                // Same-subplane neighbours sit 2 px apart on an RGGB mosaic; anything else is a
                // single plane. (SensorType.Color frames are 3-channel and never reach here.)
                _step = calibrated.ImageMeta.SensorType == SensorType.RGGB ? 2 : 1;
                _counts = new ushort[w * h];
            }
            else if (w != _width || h != _height)
            {
                _skippedFrames++;
                return;
            }

            // Per-subplane outlier threshold DELTA (sigma * 1.4826 * strided MAD) for this frame.
            // Per subplane because the CFA planes sit at different levels (QE, filter bandpass, WB
            // of the scene) and share nothing but the sensor.
            var step = _step;
            var deltas = new float[step * step];
            for (var sy = 0; sy < step; sy++)
            {
                for (var sx = 0; sx < step; sx++)
                {
                    var mad = SubplaneStridedMad(data, sx, sy, step, w, h);
                    if (!(mad > 0f))
                    {
                        // No usable noise scale on some subplane (uniform synthetic input); a
                        // frame we cannot judge must not vote.
                        _skippedFrames++;
                        return;
                    }
                    deltas[sy * step + sx] = _outlierSigma * GaussianFactor * mad;
                }
            }

            var counts = _counts;
            // Rows partition the counts array, so the per-row increments never race. The border of
            // `step` px has no full neighbourhood and never accumulates (and so is never flagged).
            Parallel.For(step, h - step, y =>
            {
                var rowBase = y * w;
                var rowSub = (y % step) * step;
                Span<float> n = stackalloc float[8];
                for (var x = step; x < w - step; x++)
                {
                    var v = data[y, x];
                    n[0] = data[y - step, x - step];
                    n[1] = data[y - step, x];
                    n[2] = data[y - step, x + step];
                    n[3] = data[y, x - step];
                    n[4] = data[y, x + step];
                    n[5] = data[y + step, x - step];
                    n[6] = data[y + step, x];
                    n[7] = data[y + step, x + step];
                    for (var i = 1; i < 8; i++)
                    {
                        var key = n[i];
                        var j = i - 1;
                        while (j >= 0 && n[j] > key)
                        {
                            n[j + 1] = n[j];
                            j--;
                        }
                        n[j + 1] = key;
                    }
                    var median = 0.5f * (n[3] + n[4]);
                    // A non-finite pixel is unusable by definition and counts as an outlier; a
                    // non-finite NEIGHBOUR makes the median NaN and every comparison false, so
                    // pixels bordering a NaN region simply never accumulate.
                    if (!float.IsFinite(v) || MathF.Abs(v - median) > deltas[rowSub + x % step])
                    {
                        counts[rowBase + x]++;
                    }
                }
            });
            FramesAccumulated++;
        }

        /// <summary>
        /// RMS radial spread of the transforms' translation components about their mean -- the
        /// "did this session move" statistic <see cref="BuildMask"/> gates on. Translation only:
        /// rotation moves field corners further still, so ignoring it under-states the spread and
        /// can only make the gate MORE conservative.
        /// </summary>
        public static float TranslationSpreadPx(IReadOnlyList<Matrix3x2> transforms)
        {
            if (transforms.Count == 0)
            {
                return 0f;
            }
            var meanX = 0.0;
            var meanY = 0.0;
            foreach (var t in transforms)
            {
                meanX += t.M31;
                meanY += t.M32;
            }
            meanX /= transforms.Count;
            meanY /= transforms.Count;
            var sumSq = 0.0;
            foreach (var t in transforms)
            {
                var dx = t.M31 - meanX;
                var dy = t.M32 - meanY;
                sumSq += dx * dx + dy * dy;
            }
            return (float)Math.Sqrt(sumSq / transforms.Count);
        }

        /// <summary>
        /// Builds the sensor-space mask from the accumulated counts, or refuses with a logged
        /// reason (returns null) when the result would not be trustworthy: too few frames, a
        /// session that never moved (<paramref name="transforms"/> spread under
        /// <see cref="MinTranslationSpreadPx"/>), or a flagged fraction past
        /// <see cref="BadPixelDetection.DefaultMaxMaskedFraction"/> (no plausible defect population
        /// is that large, so something else -- pattern noise, an ultra-dense star field on a barely
        /// moved session -- is being flagged). On refusal the caller falls back to the dark mask.
        /// </summary>
        /// <param name="transforms">The registered sensor-to-reference transforms of the matched
        /// frames; only their translation spread is read.</param>
        /// <param name="logger">Receives one line stating the verdict either way.</param>
        /// <param name="context">Optional log prefix (the dataset path tags its session id).</param>
        /// <returns>A single-element array (the mask is sensor-space, matching the one-channel raw
        /// CFA the drizzle kernel consumes) or null.</returns>
        public BitMatrix[]? BuildMask(IReadOnlyList<Matrix3x2> transforms, ILogger? logger = null, string? context = null)
        {
            var tag = context is null ? "" : $"[{context}] ";
            if (_counts is null || FramesAccumulated < MinFramesForMask)
            {
                logger?.LogInformation(
                    "  {Tag}registration bad-pixel map: only {Frames} usable frame(s) (< {Min}); falling back to the dark-derived mask",
                    tag, FramesAccumulated, MinFramesForMask);
                return null;
            }
            var spread = TranslationSpreadPx(transforms);
            if (spread < MinTranslationSpreadPx)
            {
                logger?.LogInformation(
                    "  {Tag}registration bad-pixel map: translation spread {Spread:F2} px (< {Min}) -- an unmoved session cannot separate stars from defects by persistence; falling back to the dark-derived mask",
                    tag, spread, MinTranslationSpreadPx);
                return null;
            }

            var required = (int)MathF.Ceiling(MinOutlierFraction * FramesAccumulated);
            var counts = _counts;
            var mask = new BitMatrix(_height, _width);
            var flagged = 0L;
            for (var y = 0; y < _height; y++)
            {
                var rowBase = y * _width;
                for (var x = 0; x < _width; x++)
                {
                    if (counts[rowBase + x] >= required)
                    {
                        mask[y, x] = true;
                        flagged++;
                    }
                }
            }

            var totalPx = (long)_width * _height;
            if (flagged > totalPx * BadPixelDetection.DefaultMaxMaskedFraction)
            {
                logger?.LogWarning(
                    "  {Tag}registration bad-pixel map: {Count} px ({Pct:F2}%) persist as outliers, far past any plausible defect population; refusing the map and falling back to the dark-derived mask",
                    tag, flagged, flagged * 100.0 / totalPx);
                return null;
            }

            logger?.LogInformation(
                "  {Tag}registration bad-pixel map: {Count} px ({Pct:F4}% of frame) outliers in >= {Required}/{Frames} frames (sigma={Sigma:F1}, spread {Spread:F1} px{Skipped})",
                tag, flagged, flagged * 100.0 / totalPx, required, FramesAccumulated, _outlierSigma, spread,
                _skippedFrames > 0 ? $", {_skippedFrames} frame(s) skipped" : "");
            return [mask];
        }

        /// <summary>
        /// ORs <paramref name="other"/> into <paramref name="target"/> in place. The two mask
        /// producers flag nearly DISJOINT real populations -- measured on the ASI533 against 15
        /// distinct archived APP maps, only 3 of the session map's 138 px sat in the 18,393-px
        /// unanimous defect core that the dark map covers at 72% -- so the shipped mask is their
        /// UNION: the dark map carries the stable dark-current defects (whose variance survives
        /// mean-correcting dark subtraction), the session map carries the pixels actively
        /// misbehaving in these lights at their own exposure/gain/temperature. Preferring either
        /// alone discards a measured good.
        /// </summary>
        public static void UnionInto(BitMatrix target, BitMatrix other, int width, int height)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (other[y, x])
                    {
                        target[y, x] = true;
                    }
                }
            }
        }

        /// <summary>
        /// Overlap between two same-geometry masks, for the side-by-side log line when both a
        /// registration-derived and a dark-derived mask exist: strong agreement is evidence for
        /// both, and each side's exclusive count localises what the other method misses.
        /// </summary>
        public static (long Shared, long OnlyA, long OnlyB) Overlap(BitMatrix a, BitMatrix b, int width, int height)
        {
            long shared = 0, onlyA = 0, onlyB = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var inA = a[y, x];
                    var inB = b[y, x];
                    if (inA && inB)
                    {
                        shared++;
                    }
                    else if (inA)
                    {
                        onlyA++;
                    }
                    else if (inB)
                    {
                        onlyB++;
                    }
                }
            }
            return (shared, onlyA, onlyB);
        }

        /// <summary>
        /// Median absolute deviation of a strided, finite sample of one CFA subplane, with the
        /// dark detector's non-zero-tail fallback for quantized data. Returns 0 when no scale is
        /// recoverable (the caller skips the frame).
        /// </summary>
        private static float SubplaneStridedMad(float[,] data, int sx, int sy, int step, int w, int h)
        {
            var strideY = step * StatStride;
            var strideX = step * StatStride;
            var capacity = ((h - sy + strideY - 1) / strideY) * ((w - sx + strideX - 1) / strideX);
            var sample = new float[capacity];
            var n = 0;
            for (var y = sy; y < h; y += strideY)
            {
                for (var x = sx; x < w; x += strideX)
                {
                    var v = data[y, x];
                    if (float.IsFinite(v))
                    {
                        sample[n++] = v;
                    }
                }
            }
            if (n == 0)
            {
                return 0f;
            }
            Array.Sort(sample, 0, n);
            var median = sample[n / 2];
            for (var i = 0; i < n; i++)
            {
                sample[i] = MathF.Abs(sample[i] - median);
            }
            Array.Sort(sample, 0, n);
            var mad = sample[n / 2];
            if (mad > 0f)
            {
                return mad;
            }
            // Quantized data can collapse the MAD to exactly 0 (see BadPixelDetection's identical
            // fallback); anchor to the median of the non-zero deviation tail instead.
            var firstNonZero = 0;
            while (firstNonZero < n && sample[firstNonZero] <= 0f)
            {
                firstNonZero++;
            }
            return firstNonZero >= n ? 0f : sample[firstNonZero + (n - firstNonZero) / 2];
        }
    }
}
