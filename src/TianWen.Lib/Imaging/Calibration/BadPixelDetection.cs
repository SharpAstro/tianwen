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
    /// Runaway guard for the noise-scale loop: an iteration that would flag
    /// more than this fraction of the channel has put its threshold inside the
    /// BULK of the distribution rather than in the defect tail, so its
    /// median/MAD are degenerate and the previous iteration's estimate is kept.
    ///
    /// <para>This is not hypothetical. Traced on a real ASI533 master dark at
    /// gain 252, sigma 1: iteration 0 flagged 330,021 px, which shifted the
    /// sample median 780 -> 778 and HALVED the MAD 4.0 -> 2.0, dropping the
    /// threshold and letting iteration 1 flag a further 1,522,096 -- 20.5% of
    /// the frame, 59% of it pixels that fifteen independent Astro Pixel
    /// Processor runs never once flagged. <see cref="DefaultConvergenceFraction"/>
    /// cannot catch this: "stop once an iteration adds under 0.01%" only fires
    /// after the run has finished consuming the distribution.</para>
    ///
    /// <para>1% is far above any plausible defect population (the measured
    /// consensus defect set for that sensor is 0.203% of the frame) and far
    /// below a runaway, so a sane threshold can never trip it.</para>
    /// </summary>
    public const float DefaultMaxMaskedFraction = 0.01f;

    /// <summary>
    /// Defect budget: the fraction of a channel that may plausibly be bad. The
    /// threshold walks DOWN from the caller's sigma while the flagged count
    /// stays within this, which is what makes the result comparable across
    /// darks. Pass 0 to disable and use the caller's sigma verbatim.
    ///
    /// <para><b>Why sigma alone does not work.</b> It is not a portable unit
    /// here. On a bias-dominated cooled-CMOS dark the MAD is quantized (values
    /// of exactly 4.0 and 2.0 ADU were observed) and collapses to 0 often
    /// enough that the non-zero-tail fallback below is the live path, so the
    /// scale sigma multiplies differs between two darks from the SAME sensor at
    /// different gain. Measured against a consensus defect set of 18,393 px,
    /// a fixed sigma 8 recovers 32.95% of it on one dark and 74.77% on
    /// another; walking down to a 0.3% budget recovers 85.99% and 88.69%
    /// respectively, at unchanged contamination (~1.9% either way).</para>
    /// </summary>
    public const float DefaultTargetMaskedFraction = 0.003f;

    /// <summary>Multiplicative step for the budget walk. 0.75 costs at most a
    /// handful of extra passes between a typical sigma 8 and the floor while
    /// landing close enough to the budget that a finer step buys nothing.</summary>
    private const float SigmaStepDown = 0.75f;

    /// <summary>Floor for the budget walk. A backstop only: the walk normally
    /// stops because the next step would exceed the budget. It matters solely
    /// for a pathological dark whose defect tail never reaches the budget at
    /// any threshold, where continuing would eventually flag the whole frame.</summary>
    private const float MinSigma = 0.25f;

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
        float convergenceFraction = DefaultConvergenceFraction,
        float maxMaskedFraction = DefaultMaxMaskedFraction,
        float targetMaskedFraction = DefaultTargetMaskedFraction)
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
                sigmaThreshold, maxIterations, convergenceFraction,
                maxMaskedFraction, targetMaskedFraction, logger);
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
    /// Builds one channel's mask in two SEPARATE phases, which is the whole
    /// point of the shape:
    ///
    /// <list type="number">
    /// <item>Converge a noise scale (median + MAD) by iterative kappa-sigma
    /// over the strided sample, guarded by <paramref name="maxMaskedFraction"/>.</item>
    /// <item>Choose the final threshold by walking sigma DOWN from the caller's
    /// value while the flagged count stays inside
    /// <paramref name="targetMaskedFraction"/>, then mask once.</item>
    /// </list>
    ///
    /// <para><b>Why they must be separate.</b> The original single loop grew
    /// the mask AND re-derived the noise scale from the surviving sample on
    /// every iteration. Since the mask only ever grows, the sample only ever
    /// shrinks, so the estimate could only tighten and the threshold could only
    /// fall -- positive feedback the moment the threshold reached the bulk of
    /// the distribution. Estimating the scale ONCE, from a sample the final
    /// threshold has not touched, removes that coupling structurally rather
    /// than bounding it. The guard in phase 1 remains as a backstop for a
    /// pathological dark.</para>
    /// </summary>
    private static BitMatrix BuildMaskForChannel(
        float[,] data, int channelIndex,
        float sigmaThreshold, int maxIterations, float convergenceFraction,
        float maxMaskedFraction, float targetMaskedFraction,
        ILogger? logger)
    {
        var h = data.GetLength(0);
        var w = data.GetLength(1);
        var totalPx = (long)h * w;
        var convergenceFloor = (long)(totalPx * convergenceFraction);
        var runawayCeiling = maxMaskedFraction > 0f
            ? (long)(totalPx * maxMaskedFraction)
            : long.MaxValue;

        // Strided sample collected once; reused (with exclusion filtering)
        // across the estimation iterations. Positions are no longer needed:
        // phase 1 works purely on the sample, and the single full-channel pass
        // happens in phase 2 once the threshold is settled.
        var sampleCount = ((h + StatStride - 1) / StatStride) * ((w + StatStride - 1) / StatStride);
        var sampleValues = new float[sampleCount];
        var idx = 0;
        for (var y = 0; y < h; y += StatStride)
        {
            for (var x = 0; x < w; x += StatStride)
            {
                sampleValues[idx++] = data[y, x];
            }
        }
        var totalSample = idx;

        // One strided sample stands for this many real pixels, which is how a
        // sample-side count is compared against the whole-frame guard and
        // convergence floor without a full-channel pass per iteration.
        var pixelsPerSample = totalSample > 0 ? totalPx / (double)totalSample : 1.0;

        var excluded = new bool[totalSample];
        var workBuf = new float[totalSample];
        var acceptedMedian = 0f;
        var acceptedMad = 0f;
        var haveEstimate = false;
        var iterRan = 0;

        // PHASE 1: converge a noise scale. Nothing here builds the mask.
        for (var iter = 0; iter < maxIterations; iter++)
        {
            iterRan = iter + 1;

            // Filter the strided sample to positions not yet excluded. After
            // iter 0 these are guaranteed non-hot (we just excluded the hot
            // ones), so the median + MAD anchor to the inlier distribution.
            var liveCount = 0;
            for (var i = 0; i < totalSample; i++)
            {
                if (!excluded[i])
                {
                    workBuf[liveCount++] = sampleValues[i];
                }
            }
            // Degenerate: every strided sample has been excluded. Stop -- the
            // channel's distribution is so contaminated that one more iteration
            // would have no signal to anchor against.
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

            // MAD = 0 on cooled-CMOS bias-dominated darks: at -5C on an
            // IMX571, 60s of dark current is sub-ADU, so the master dark
            // is essentially uniform bias with a long hot-pixel tail.
            // 50%+ of pixels collapse to a single quantized value, the
            // median absolute deviation is 0 by construction, and the
            // textbook threshold (median + sigma * MAD) degenerates to
            // the median itself -- which would mark half the channel as
            // "hot". Fall back to the median of the NON-ZERO absolute
            // deviations: this anchors the noise scale to the typical
            // step between the bias floor and the next-quantized level
            // (a few ADU on this sensor), ignoring the degenerate
            // delta-function at exactly the bias level.
            if (mad <= 0f)
            {
                // Find the first non-zero deviation. workBuf is sorted
                // ascending, so a linear scan from the start lands on the
                // first non-zero entry quickly (most zeros are clustered
                // at the bottom).
                var firstNonZero = 0;
                while (firstNonZero < liveCount && workBuf[firstNonZero] <= 0f) firstNonZero++;
                if (firstNonZero >= liveCount)
                {
                    // Truly uniform channel (every strided sample
                    // identical). Can't recover a noise scale; bail.
                    logger?.LogDebug("  hot-pixel ch={Ch} iter={Iter}: every strided sample identical to median; stopping",
                        channelIndex, iter);
                    break;
                }
                // Median of the non-zero deviation tail.
                mad = workBuf[firstNonZero + (liveCount - firstNonZero) / 2];
                logger?.LogDebug("  hot-pixel ch={Ch} iter={Iter}: MAD=0 (bias-dominated dark); using non-zero-tail MAD={Mad:F4}",
                    channelIndex, iter, mad);
            }

            var threshold = median + sigmaThreshold * GaussianFactor * mad;

            // Exclude, on the SAMPLE only. Exclusions grow monotonically: a
            // sample excluded in iter K stays excluded, because it was
            // identified with the cleanest statistics available at the time and
            // re-admitting it would contaminate the very estimate that
            // excluded it.
            long newlyExcluded = 0;
            for (var i = 0; i < totalSample; i++)
            {
                if (!excluded[i] && sampleValues[i] > threshold)
                {
                    excluded[i] = true;
                    newlyExcluded++;
                }
            }
            var estimatedFullFrame = (long)(newlyExcluded * pixelsPerSample);

            logger?.LogDebug(
                "  hot-pixel ch={Ch} iter={Iter}: median={Med:F4} mad={Mad:F4} threshold={T:F4} sample-excluded={Excluded} (~{Est} px)",
                channelIndex, iter, median, mad, threshold, newlyExcluded, estimatedFullFrame);

            // RUNAWAY GUARD. Iteration 0 is always accepted: its median + MAD
            // come from the complete strided sample with nothing excluded, so
            // it is the most trustworthy estimate available and there is
            // nothing earlier to fall back to. From iteration 1 on, a pass that
            // would flag more than the ceiling has put its threshold inside the
            // bulk rather than the defect tail, which means its MAD has already
            // collapsed; keep the previous estimate and stop.
            if (iter > 0 && estimatedFullFrame > runawayCeiling)
            {
                logger?.LogWarning(
                    "  hot-pixel ch={Ch} iter={Iter}: refining the noise scale would flag ~{Est} px (over the {Ceiling} px guard); keeping the iter-{Prev} estimate median={Med:F4} mad={Mad:F4}",
                    channelIndex, iter, estimatedFullFrame, runawayCeiling, iter - 1, acceptedMedian, acceptedMad);
                break;
            }

            acceptedMedian = median;
            acceptedMad = mad;
            haveEstimate = true;

            // Convergence: newly-excluded below the absolute floor, or exactly
            // zero (full convergence). Iter 0 always excludes something in
            // practice; subsequent iters tail off.
            if (newlyExcluded == 0 || estimatedFullFrame < convergenceFloor)
            {
                break;
            }
        }

        var mask = new BitMatrix(h, w);
        if (!haveEstimate)
        {
            logger?.LogWarning(
                "  hot-pixel ch={Ch}: no usable noise scale (uniform or fully contaminated channel); masking nothing",
                channelIndex);
            return mask;
        }

        // PHASE 2: choose the threshold against the defect budget, then mask
        // once. The noise scale is now FIXED, so lowering sigma can only lower
        // the threshold and can only raise the count -- monotone, with no path
        // back into the estimate. That is what makes walking down safe here
        // when it was catastrophic inside the old combined loop.
        var chosenSigma = sigmaThreshold;
        var chosenThreshold = acceptedMedian + chosenSigma * GaussianFactor * acceptedMad;
        var chosenCount = CountAbove(data, chosenThreshold);

        if (targetMaskedFraction > 0f)
        {
            var budget = (long)(totalPx * targetMaskedFraction);
            if (chosenCount > budget)
            {
                // The caller's own sigma already exceeds the budget. Left alone
                // deliberately: the budget exists to let the threshold descend
                // safely, not to discard detections the caller asked for. A
                // dark that does this is worth looking at.
                logger?.LogWarning(
                    "  hot-pixel ch={Ch}: sigma={Sigma:F2} already flags {Count} px, over the {Budget} px budget; not lowering further",
                    channelIndex, chosenSigma, chosenCount, budget);
            }
            else
            {
                while (chosenSigma * SigmaStepDown >= MinSigma)
                {
                    var nextSigma = chosenSigma * SigmaStepDown;
                    var nextThreshold = acceptedMedian + nextSigma * GaussianFactor * acceptedMad;
                    var nextCount = CountAbove(data, nextThreshold);
                    if (nextCount > budget)
                    {
                        break;
                    }
                    chosenSigma = nextSigma;
                    chosenThreshold = nextThreshold;
                    chosenCount = nextCount;
                }
            }
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (data[y, x] > chosenThreshold)
                {
                    mask[y, x] = true;
                }
            }
        }

        logger?.LogInformation(
            "  hot-pixel ch={Ch}: {Count} px ({Pct:F3}% of channel) at sigma={Sigma:F2} threshold={T:F4} (median={Med:F4} mad={Mad:F4}, {Iters} estimation iter(s))",
            channelIndex, chosenCount, chosenCount * 100.0 / totalPx, chosenSigma, chosenThreshold,
            acceptedMedian, acceptedMad, iterRan);

        return mask;
    }

    /// <summary>Pixels strictly above <paramref name="threshold"/>. Kept separate
    /// so the budget walk reads as "how many would this threshold flag" without
    /// allocating or mutating a mask per candidate.</summary>
    private static long CountAbove(float[,] data, float threshold)
    {
        var h = data.GetLength(0);
        var w = data.GetLength(1);
        long count = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (data[y, x] > threshold)
                {
                    count++;
                }
            }
        }
        return count;
    }
}
