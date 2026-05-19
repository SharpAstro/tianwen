using System;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Hot-pixel detection from a master dark frame. A hot pixel is one whose
/// dark current is so far above the median that no realistic dark-subtraction
/// (which removes the AVERAGE per-pixel offset) can eliminate the per-frame
/// shot-noise variance the pixel contributes. The standard fix is to drop
/// these pixels entirely -- mark them NaN in the calibrated light frames so
/// downstream integrators / drizzle accumulators skip them.
/// </summary>
public static class BadPixelDetection
{
    /// <summary>Subsample stride used when computing the per-channel median +
    /// MAD of the dark master. Denser than the original stride=32 because the
    /// iterative loop (<see cref="BuildMaskFromDark"/>) leans on the sample
    /// containing actual hot pixels: each iteration excludes the flagged
    /// strided positions and re-estimates noise stats from the inlier
    /// remainder, which only helps if a meaningful fraction of the sample
    /// IS contaminated. At stride=8 a 6224x4168 IMX455 dark yields ~405k
    /// samples per channel -- big enough to contain dozens of hot pixels at
    /// typical 0.01-0.1 % hot-pixel rates, so excluding them measurably
    /// tightens the MAD between iterations. Sort cost stays under 10 ms per
    /// channel per iteration.</summary>
    private const int StatStride = 8;

    /// <summary>Conventional MAD-to-Gaussian-sigma factor. Median +
    /// <see cref="GaussianFactor"/> * MAD approximates median + 1*sigma
    /// for a Gaussian distribution, which is what the
    /// <c>sigmaThreshold</c> parameter effectively maps to.</summary>
    private const float GaussianFactor = 1.4826f;

    /// <summary>Default iteration cap for kappa-sigma convergence in
    /// <see cref="BuildMaskFromDark"/>. Typical real-data convergence
    /// happens in 2-4 iterations; the cap is a runaway guard for
    /// pathological inputs (uniform dark, single-bin distribution, etc.).</summary>
    private const int DefaultMaxIterations = 10;

    /// <summary>Default convergence floor: stop iterating when an iteration
    /// adds fewer than this fraction of the channel's total pixel count to
    /// the mask. 0.0001 = 0.01 % (~2600 pixels on a 26 MP frame) -- small
    /// enough that "we've found nearly everything" but big enough that we
    /// don't iterate forever chasing noise-floor flicker.</summary>
    private const float DefaultConvergenceFraction = 0.0001f;

    /// <summary>
    /// Per-channel hot-pixel mask: <c>true</c> bit = pixel exceeds the
    /// converged threshold (median + sigma * 1.4826 * MAD), masked from
    /// downstream integration. One <see cref="BitMatrix"/> per channel keeps
    /// the memory footprint at 1 bit/pixel (8x denser than <c>bool[,]</c>
    /// -- ~3 MB per 6k frame channel vs 26 MB).
    ///
    /// <para>The kappa-sigma loop iterates until convergence: each pass
    /// recomputes the median + MAD over the strided sample positions that
    /// are NOT YET masked. Outliers (hot pixels) in the sample inflate the
    /// initial MAD; excluding them on the next iteration tightens MAD,
    /// drops the threshold (in absolute ADU), and catches more borderline
    /// pixels. The mask grows monotonically -- never un-mask -- and the
    /// loop terminates when an iteration adds fewer than
    /// <paramref name="convergenceFraction"/> of total pixels, or after
    /// <paramref name="maxIterations"/> as a safety cap. This is the
    /// standard astro pipeline approach (PixInsight CosmeticCorrection,
    /// Astro Pixel Processor, DeepSkyStacker bad-pixel rejection all do
    /// equivalent iterative kappa-sigma) and catches the warm-borderline
    /// pixels a one-shot threshold misses.</para>
    /// </summary>
    /// <param name="darkMaster">Master dark frame.</param>
    /// <param name="sigmaThreshold">Threshold in Gaussian sigmas. Typical
    /// good value is 8 -- once the iterative loop has converged, anything
    /// 8 sigma above the noise floor is a hot pixel by definition (no
    /// legitimate dark current spread reaches 8 sigma above the cleaned
    /// median). Pass 0 or negative to return <c>null</c> (disable masking).</param>
    /// <param name="logger">Optional logger -- receives one
    /// <c>Information</c> line per channel summarising the converged mask
    /// (count + iterations + final threshold) and per-iteration
    /// <c>Debug</c> lines for forensics.</param>
    /// <param name="maxIterations">Hard cap on iterations. Defaults to
    /// <see cref="DefaultMaxIterations"/>; tighter values (3-5) for
    /// runtime-sensitive paths.</param>
    /// <param name="convergenceFraction">Stop when newly-masked pixels in
    /// one iteration drop below <c>fraction * totalChannelPx</c>. Defaults
    /// to <see cref="DefaultConvergenceFraction"/> (0.01 % of channel
    /// pixels).</param>
    /// <returns>A per-channel mask <c>BitMatrix[ChannelCount]</c> or
    /// <c>null</c> when masking is disabled.</returns>
    public static BitMatrix[]? BuildMaskFromDark(
        Image darkMaster,
        float sigmaThreshold,
        ILogger? logger = null,
        int maxIterations = DefaultMaxIterations,
        float convergenceFraction = DefaultConvergenceFraction)
    {
        if (sigmaThreshold <= 0f)
        {
            return null;
        }
        var channelCount = darkMaster.ChannelCount;
        var masks = new BitMatrix[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            masks[c] = BuildMaskForChannel(darkMaster.GetChannelArray(c), c,
                sigmaThreshold, maxIterations, convergenceFraction, logger);
        }
        return masks;
    }

    /// <summary>
    /// Counts the masked pixels (true bits) across all channels. Useful
    /// for the pipeline log -- a typical CMOS sensor reports a few hundred
    /// to several thousand hot pixels after iterative convergence; very
    /// different orders of magnitude hint at a bad sigma choice or a
    /// corrupted dark.
    /// </summary>
    public static int CountMaskedPixels(BitMatrix[]? mask, int width, int height)
    {
        if (mask is null) return 0;
        var total = 0;
        for (var c = 0; c < mask.Length; c++)
        {
            var m = mask[c];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (m[y, x]) total++;
                }
            }
        }
        return total;
    }

    /// <summary>
    /// Run the iterative kappa-sigma loop for one channel of the dark master.
    /// Strided positions + values are captured once; each iteration filters
    /// the sample to currently un-masked positions, recomputes median + MAD,
    /// applies the new threshold to the FULL channel, and grows the mask.
    /// </summary>
    private static BitMatrix BuildMaskForChannel(
        float[,] data, int channelIndex,
        float sigmaThreshold, int maxIterations, float convergenceFraction,
        ILogger? logger)
    {
        var h = data.GetLength(0);
        var w = data.GetLength(1);
        var totalPx = (long)h * w;
        var convergenceFloor = (long)(totalPx * convergenceFraction);

        // Strided sample collected once; reused (with position-aware
        // mask filtering) across iterations.
        var sampleCount = ((h + StatStride - 1) / StatStride) * ((w + StatStride - 1) / StatStride);
        var sampleValues = new float[sampleCount];
        var sampleY = new int[sampleCount];
        var sampleX = new int[sampleCount];
        var idx = 0;
        for (var y = 0; y < h; y += StatStride)
        {
            for (var x = 0; x < w; x += StatStride)
            {
                sampleValues[idx] = data[y, x];
                sampleY[idx] = y;
                sampleX[idx] = x;
                idx++;
            }
        }
        var totalSample = idx;

        var mask = new BitMatrix(h, w);
        var workBuf = new float[totalSample];
        long maskedTotal = 0;
        var lastThreshold = 0f;
        var iterRan = 0;

        for (var iter = 0; iter < maxIterations; iter++)
        {
            iterRan = iter + 1;

            // Filter the strided sample to positions NOT in the current
            // mask. After iter 0 these are guaranteed non-hot (we just
            // flagged the hot ones), so the median + MAD anchor to the
            // inlier distribution.
            var liveCount = 0;
            for (var i = 0; i < totalSample; i++)
            {
                if (!mask[sampleY[i], sampleX[i]])
                {
                    workBuf[liveCount++] = sampleValues[i];
                }
            }
            // Degenerate: every strided sample has been masked. Stop --
            // the channel's distribution is so contaminated that one more
            // iteration would have no signal to anchor against.
            if (liveCount == 0)
            {
                break;
            }

            Array.Sort(workBuf, 0, liveCount);
            var median = workBuf[liveCount / 2];

            // Reuse the buffer for the absolute-deviation array. We've
            // already consumed the sorted samples to read the median.
            for (var i = 0; i < liveCount; i++)
            {
                workBuf[i] = MathF.Abs(workBuf[i] - median);
            }
            Array.Sort(workBuf, 0, liveCount);
            var mad = workBuf[liveCount / 2];

            // MAD = 0 can happen when the inlier subset is uniform (rare
            // but possible on a clipped / over-corrected dark). The
            // threshold would collapse to median + 0, which is the median
            // itself; counting "pixels exceeding the median" would mark
            // ~half the channel. Bail with whatever mask we already have.
            if (mad <= 0f)
            {
                logger?.LogDebug("  hot-pixel ch={Ch} iter={Iter}: MAD=0 (inlier subset uniform); stopping",
                    channelIndex, iter);
                break;
            }

            var threshold = median + sigmaThreshold * GaussianFactor * mad;
            lastThreshold = threshold;

            // Walk the FULL channel; flag any un-masked pixel exceeding
            // threshold. Mask grows monotonically -- a pixel marked in
            // iter K stays marked through convergence even if a later
            // iteration's threshold would un-mark it. This is correct:
            // once we've identified a hot pixel using cleaner statistics,
            // re-introducing it would contaminate the very loop that just
            // excluded it.
            long newlyMasked = 0;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (!mask[y, x] && data[y, x] > threshold)
                    {
                        mask[y, x] = true;
                        newlyMasked++;
                    }
                }
            }
            maskedTotal += newlyMasked;

            logger?.LogDebug(
                "  hot-pixel ch={Ch} iter={Iter}: median={Med:F4} mad={Mad:F4} threshold={T:F4} added={Added} total={Total}",
                channelIndex, iter, median, mad, threshold, newlyMasked, maskedTotal);

            // Convergence criterion: newly-added below the absolute floor,
            // OR exactly zero (full convergence). Iter 0 always has
            // newlyMasked > 0 in practice; subsequent iters tail off.
            if (newlyMasked == 0 || newlyMasked < convergenceFloor)
            {
                break;
            }
        }

        logger?.LogInformation(
            "  hot-pixel ch={Ch}: {Count} px in {Iters} iter(s) (final threshold={T:F4})",
            channelIndex, maskedTotal, iterRan, lastThreshold);

        return mask;
    }
}
