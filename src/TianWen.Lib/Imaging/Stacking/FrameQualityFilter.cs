using System;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Per-frame PSF metrics extracted during registration. Median over the
/// stars detected on the frame, computed once and carried through the
/// pipeline so the post-registration quality filter has session-wide
/// statistics without re-running star detection.
/// </summary>
/// <param name="MedianHfd">Median half-flux diameter, pixels.</param>
/// <param name="MedianFwhm">Median full-width-half-maximum, pixels.</param>
/// <param name="MedianEllipticity">Median moment-based ellipticity, in
/// [0, 1]. 0 = round, → 1 = elongated.</param>
/// <param name="StarCount">Number of stars the detector found on the
/// frame. Used by <see cref="FrameQualityFilter"/> as a left-tail
/// reject metric: a session-wide drop in star count catches haze,
/// clouds, or dew which reduce transparency without necessarily
/// widening HFD on the few stars that do detect. Independent of the
/// <c>MinStarsForMatch</c> registration gate (that's an absolute
/// floor; this is a relative-to-session-median outlier check).</param>
public readonly record struct FrameMetrics(
    float MedianHfd,
    float MedianFwhm,
    float MedianEllipticity,
    int StarCount);

/// <summary>
/// Reason(s) a frame was kept or dropped by <see cref="FrameQualityFilter"/>.
/// Flags so a frame can fail multiple criteria simultaneously (HFD broad
/// AND star count low, for instance) without losing detail.
/// </summary>
[System.Flags]
public enum FrameRejectReason : byte
{
    Kept = 0,
    /// <summary>Median HFD above session-wide threshold (right tail).</summary>
    HfdTooBroad = 1 << 0,
    /// <summary>Median ellipticity above session-wide threshold (right tail).</summary>
    EllipticityTooHigh = 1 << 1,
    /// <summary>Star count below session-wide threshold (left tail).
    /// Catches haze / clouds / dew which reduce transparency without
    /// necessarily widening HFD on the few stars that do detect.</summary>
    StarCountTooLow = 1 << 2,
}

/// <summary>
/// Outcome of one filter pass over a session's matched frames.
/// </summary>
/// <param name="Reasons">Per-frame keep/drop decision, indexed the same
/// as the input metrics. <see cref="FrameRejectReason.Kept"/> means the
/// frame contributes to the integration; anything else means rejected.</param>
/// <param name="KeptCount">Number of kept frames -- redundant with
/// counting <see cref="FrameRejectReason.Kept"/> in <see cref="Reasons"/>
/// but cheaper than re-scanning at log time.</param>
/// <param name="FloorTriggered">True when the MAD threshold flagged
/// more than the 20% keep-floor would allow and the filter degraded to
/// severity-ranked quantile rejection. Surfaced so callers can log
/// "threshold mis-calibrated for this session" rather than the simpler
/// per-frame line.</param>
public readonly record struct FrameQualityFilterResult(
    FrameRejectReason[] Reasons,
    int KeptCount,
    bool FloorTriggered);

/// <summary>
/// Per-frame quality filter: MAD-based outlier rejection on HFD +
/// ellipticity, with an 80% keep floor (the worst 20% by severity get
/// dropped at most, even when the MAD threshold would cut more).
///
/// <para>The filter is pure: in metrics + sigma, out keep/drop array. No
/// I/O, no image access. The pipeline computes <see cref="FrameMetrics"/>
/// once per frame during registration and passes the full session to
/// this filter after the loop completes, so the threshold is anchored
/// to session-wide median + MAD rather than any single frame.</para>
/// </summary>
internal static class FrameQualityFilter
{
    /// <summary>
    /// MAD → standard-deviation scale factor (so the sigma knob reads
    /// like "N standard deviations" for users familiar with kappa-sigma
    /// rejection literature). Exact for Gaussian noise; the right rule
    /// of thumb for real PSF distributions which are right-skewed.
    /// </summary>
    private const float MadToStdDev = 1.4826f;

    /// <summary>
    /// Maximum fraction of frames we will mark as rejected in one filter
    /// pass. If the MAD threshold flags more than this, we fall back to
    /// severity-ranked quantile rejection.
    /// </summary>
    private const float MaxRejectFraction = 0.20f;

    /// <summary>
    /// Minimum input length where MAD-based thresholding is statistically
    /// meaningful. Below this we skip the filter entirely and keep all
    /// frames -- a 3-frame session's "outlier" is just noise on a noise
    /// distribution.
    /// </summary>
    private const int MinFramesForFilter = 4;

    /// <summary>
    /// Filter the input <paramref name="metrics"/> at the given
    /// <paramref name="sigma"/> threshold. See class doc for the
    /// algorithm.
    /// </summary>
    public static FrameQualityFilterResult Filter(ReadOnlySpan<FrameMetrics> metrics, float sigma)
    {
        var n = metrics.Length;
        var reasons = new FrameRejectReason[n];

        if (n < MinFramesForFilter || sigma <= 0f)
        {
            // Below 4 frames the MAD estimate is dominated by noise;
            // sigma=0 is the documented "off" path. Either way: keep all.
            return new FrameQualityFilterResult(reasons, n, FloorTriggered: false);
        }

        // Robust median + MAD on each metric. We sort copies (n is
        // typically <= a few hundred so the allocation cost is
        // negligible compared to the registration pass that produced
        // these). Three metrics: HFD + ellipticity are right-tail
        // (high is bad), star count is left-tail (low is bad: haze /
        // clouds / dew reduce transparency).
        var hfdSorted = new float[n];
        var eccSorted = new float[n];
        var starSorted = new float[n];
        for (var i = 0; i < n; i++)
        {
            hfdSorted[i] = metrics[i].MedianHfd;
            eccSorted[i] = metrics[i].MedianEllipticity;
            starSorted[i] = metrics[i].StarCount;
        }
        Array.Sort(hfdSorted);
        Array.Sort(eccSorted);
        Array.Sort(starSorted);
        var hfdMedian = hfdSorted[n / 2];
        var eccMedian = eccSorted[n / 2];
        var starMedian = starSorted[n / 2];

        // MAD = median absolute deviation from the median.
        for (var i = 0; i < n; i++)
        {
            hfdSorted[i] = MathF.Abs(metrics[i].MedianHfd - hfdMedian);
            eccSorted[i] = MathF.Abs(metrics[i].MedianEllipticity - eccMedian);
            starSorted[i] = MathF.Abs(metrics[i].StarCount - starMedian);
        }
        Array.Sort(hfdSorted);
        Array.Sort(eccSorted);
        Array.Sort(starSorted);
        var hfdMad = hfdSorted[n / 2];
        var eccMad = eccSorted[n / 2];
        var starMad = starSorted[n / 2];

        // Per-metric reject threshold. MadToStdDev makes sigma read
        // like a standard-deviation cutoff. HFD and ecc are right-tail
        // (reject when metric exceeds threshold); star count is
        // left-tail (reject when metric falls below).
        var hfdThreshold = hfdMedian + sigma * MadToStdDev * hfdMad;
        var eccThreshold = eccMedian + sigma * MadToStdDev * eccMad;
        var starThreshold = starMedian - sigma * MadToStdDev * starMad;

        // First pass: mark all frames that fail any metric. severity[i]
        // is the worst per-metric breach in σ units so the floor
        // fallback can rank without a second pass.
        var severity = new float[n];
        var flaggedCount = 0;
        for (var i = 0; i < n; i++)
        {
            var reason = FrameRejectReason.Kept;
            if (metrics[i].MedianHfd > hfdThreshold) reason |= FrameRejectReason.HfdTooBroad;
            if (metrics[i].MedianEllipticity > eccThreshold) reason |= FrameRejectReason.EllipticityTooHigh;
            if (metrics[i].StarCount < starThreshold) reason |= FrameRejectReason.StarCountTooLow;
            reasons[i] = reason;
            if (reason != FrameRejectReason.Kept) flaggedCount++;

            // Severity = worst per-metric breach in σ units. MAD = 0
            // (all frames identical on that metric) → severity 0; the
            // metric won't trip the threshold anyway so the floor
            // ranking can ignore it.
            var hfdSigma = hfdMad > 1e-6f ? (metrics[i].MedianHfd - hfdMedian) / (MadToStdDev * hfdMad) : 0f;
            var eccSigma = eccMad > 1e-6f ? (metrics[i].MedianEllipticity - eccMedian) / (MadToStdDev * eccMad) : 0f;
            // Star count breach is in the OTHER direction: severity is
            // how many σ BELOW the median we are.
            var starSigma = starMad > 1e-6f ? (starMedian - metrics[i].StarCount) / (MadToStdDev * starMad) : 0f;
            severity[i] = MathF.Max(MathF.Max(hfdSigma, eccSigma), starSigma);
        }

        // Apply the 80% keep floor: at most 20% of frames may end up
        // rejected. If the MAD threshold flagged more, rank the flagged
        // frames by severity and keep only the worst N as rejected.
        var maxReject = (int)MathF.Floor(MaxRejectFraction * n);
        var floorTriggered = false;
        if (flaggedCount > maxReject)
        {
            floorTriggered = true;
            // Threshold for "is this severity in the worst maxReject?":
            // find the (n - maxReject)-th smallest severity, anything
            // above it stays rejected. Simple sort; n is small.
            var sevSorted = (float[])severity.Clone();
            Array.Sort(sevSorted);
            // The maxReject-th-from-the-top severity value.
            var cutoff = sevSorted[n - maxReject];
            for (var i = 0; i < n; i++)
            {
                if (reasons[i] != FrameRejectReason.Kept && severity[i] < cutoff)
                {
                    reasons[i] = FrameRejectReason.Kept; // floor reprieve
                }
            }
        }

        var keptCount = 0;
        for (var i = 0; i < n; i++)
        {
            if (reasons[i] == FrameRejectReason.Kept) keptCount++;
        }
        return new FrameQualityFilterResult(reasons, keptCount, floorTriggered);
    }
}
