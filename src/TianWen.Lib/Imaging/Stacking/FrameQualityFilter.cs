using System;
using TianWen.Lib.Stat;

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
/// <param name="FitFwhmGreen">The bright-star profile fit's FWHM on the debayered GREEN plane
/// (channel 0 on a mono frame), in pixels, or NaN when the fit refused or was not taken. This is
/// the per-frame width that reads SEEING: <paramref name="MedianFwhm"/> is the registration
/// detector's statistic on the pre-debayer mosaic, and on an OSC frame it reads a floor of about
/// 1.7 px whatever the sky did (a whole night at 1.70 while the green plane ran 1.7 to 2.5;
/// docs/known-limitations.md). Not used by <see cref="FrameQualityFilter"/>, whose gate is
/// relative within a session and stays on the detector's numbers.</param>
public readonly record struct FrameMetrics(
    float MedianHfd,
    float MedianFwhm,
    float MedianEllipticity,
    int StarCount,
    float FitFwhmGreen = float.NaN);

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
    /// Default sigma for the gate, shared by every caller: `tianwen stack` through
    /// <c>StackingOptions.QualityRejectSigma</c> and the dataset bake through
    /// <c>DatasetBuildOptions.QualityRejectSigma</c>.
    ///
    /// <para><b>One number in one place, because the two paths answer the same question.</b> They
    /// used to answer it differently: the bake ran at 3 while stacking left the gate off entirely,
    /// a null default carried over from this feature's own introduction to "preserve the
    /// pre-this-feature behaviour" and never revisited. So the same session stacked both ways
    /// produced two different masters and nothing in either output said which rule built it.
    /// Measured on the archive's 2022-08-31 Helix session, that was six powerline-obstructed frames
    /// integrated by one path and dropped by the other.</para>
    /// </summary>
    public const float DefaultSigma = 3.0f;

    /// <summary>
    /// Default maximum fraction of frames marked rejected in one pass. If the MAD threshold flags
    /// more than this, we fall back to severity-ranked quantile rejection.
    ///
    /// <para>One value for both callers, and it is the dataset bake's, which favours purity over
    /// yield: every master under <c>Astro-Dataset</c> was built at it, the bake invocations
    /// carrying no override, so it is the setting with real output behind it.</para>
    ///
    /// <para><b>A frame with no stars at all does not count against this fraction.</b> It is
    /// rejected as invalid rather than as an outlier, so the cap bounds only the statistical
    /// rejection.</para>
    /// </summary>
    public const float DefaultMaxRejectFraction = 0.50f;

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
    /// algorithm. <paramref name="maxRejectFraction"/> caps how many frames
    /// one pass may reject before the severity-ranked keep-floor engages
    /// (default <see cref="DefaultMaxRejectFraction"/>, stacking's
    /// yield-preserving value; the dataset builder passes a higher fraction
    /// because it favours purity over yield).
    /// </summary>
    public static FrameQualityFilterResult Filter(ReadOnlySpan<FrameMetrics> metrics, float sigma, float maxRejectFraction = DefaultMaxRejectFraction)
    {
        var n = metrics.Length;
        var reasons = new FrameRejectReason[n];

        // A frame the detector found NO stars in is rejected whatever the gate is set to. That is a
        // VALIDITY check rather than a tuning choice -- such a frame cannot be registered and cannot
        // contribute -- so it also takes no part in the statistics below (a zero would drag the star
        // median toward it) and does not count against the keep floor, which bounds the STATISTICAL
        // rejection only. This rule used to live in the dataset bake's SessionFrameAnalyzer alone,
        // so `tianwen stack` kept such frames unconditionally.
        var indices = new int[n];
        var measurable = 0;
        for (var i = 0; i < n; i++)
        {
            if (metrics[i].StarCount <= 0)
            {
                reasons[i] = FrameRejectReason.StarCountTooLow;
            }
            else
            {
                indices[measurable++] = i;
            }
        }

        if (measurable < MinFramesForFilter || sigma <= 0f)
        {
            // Below 4 measurable frames the MAD estimate is dominated by noise; sigma <= 0 is the
            // documented "off" path. Either way: keep every frame that has stars.
            return new FrameQualityFilterResult(reasons, measurable, FloorTriggered: false);
        }

        // Robust median + MAD per metric, over the measurable frames. Three metrics: HFD and
        // ellipticity are right-tail (high is bad), star count is left-tail (low is bad, since
        // haze, cloud or dew reduce transparency).
        //
        // UpperMedianAndMad, NOT MedianAndMad: this gate has always taken sorted[n / 2], and the
        // two conventions differ on an even count, so the named pair is what keeps every threshold
        // where it was. The scratch buffer is reused because the helper leaves it holding
        // deviations rather than inputs, and each metric refills it.
        Span<float> scratch = measurable <= 256 ? stackalloc float[measurable] : new float[measurable];
        for (var j = 0; j < measurable; j++) scratch[j] = metrics[indices[j]].MedianHfd;
        var (hfdMedian, hfdMad) = StatisticsHelper.UpperMedianAndMad(scratch);
        for (var j = 0; j < measurable; j++) scratch[j] = metrics[indices[j]].MedianEllipticity;
        var (eccMedian, eccMad) = StatisticsHelper.UpperMedianAndMad(scratch);
        for (var j = 0; j < measurable; j++) scratch[j] = metrics[indices[j]].StarCount;
        var (starMedian, starMad) = StatisticsHelper.UpperMedianAndMad(scratch);

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
        for (var j = 0; j < measurable; j++)
        {
            var i = indices[j];
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

        // The keep floor: at most maxRejectFraction of the MEASURABLE frames may end up rejected by
        // the statistics. If the MAD threshold flagged more, rank the flagged frames worst-first
        // and reprieve everything past the cap.
        //
        // Ranking the flagged SET is what makes the cap a bound. The previous form sorted a clone
        // of every severity, took a cutoff VALUE, and reprieved `severity < cutoff`, which has two
        // faults: it reprieves nobody sitting exactly ON the cutoff, so tied severities (identical
        // metrics all score 0) could leave more than the cap rejected; and with a cap of zero it
        // indexed one past the end, which a 4-frame session at the old 0.20 fraction reached
        // exactly (floor(0.2 * 4) == 0).
        var maxReject = (int)MathF.Floor(Math.Clamp(maxRejectFraction, 0f, 1f) * measurable);
        var floorTriggered = flaggedCount > maxReject;
        if (floorTriggered)
        {
            var flagged = new int[flaggedCount];
            var f = 0;
            for (var j = 0; j < measurable; j++)
            {
                var i = indices[j];
                if (reasons[i] != FrameRejectReason.Kept) flagged[f++] = i;
            }

            // Worst first, frame order breaking a tie so the outcome is deterministic.
            Array.Sort(flagged, (a, b) =>
            {
                var bySeverity = severity[b].CompareTo(severity[a]);
                return bySeverity != 0 ? bySeverity : a.CompareTo(b);
            });
            for (var k = maxReject; k < flagged.Length; k++)
            {
                reasons[flagged[k]] = FrameRejectReason.Kept; // floor reprieve
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
