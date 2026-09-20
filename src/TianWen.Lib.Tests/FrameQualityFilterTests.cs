using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Coverage for <see cref="FrameQualityFilter"/>: MAD-based outlier
/// rejection on HFD + ellipticity with a hard 80% keep floor. Tests are
/// pure-function and run in &lt; 5 ms each (no images, no I/O).
/// </summary>
[Collection("Imaging")]
public class FrameQualityFilterTests
{
    /// <summary>
    /// Shared helper -- repeats a baseline metric N times and optionally
    /// appends a list of outliers. Cleaner than literal arrays for the
    /// "20 normal frames + 4 outliers" patterns used below.
    /// </summary>
    private static FrameMetrics[] Frames(int normalCount, float baseHfd, float baseEcc, params (float Hfd, float Ecc)[] outliers)
    {
        var arr = new FrameMetrics[normalCount + outliers.Length];
        for (var i = 0; i < normalCount; i++)
        {
            arr[i] = new FrameMetrics(baseHfd, baseHfd, baseEcc, StarCount: 1000);
        }
        for (var i = 0; i < outliers.Length; i++)
        {
            arr[normalCount + i] = new FrameMetrics(outliers[i].Hfd, outliers[i].Hfd, outliers[i].Ecc, StarCount: 1000);
        }
        return arr;
    }

    [Fact]
    public void Filter_Empty_KeepsAll()
    {
        var result = FrameQualityFilter.Filter(System.ReadOnlySpan<FrameMetrics>.Empty, sigma: 3f);
        result.KeptCount.ShouldBe(0);
        result.FloorTriggered.ShouldBeFalse();
        result.Reasons.Length.ShouldBe(0);
    }

    [Fact]
    public void Filter_FewerThan4_KeepsAllEvenWithObviousOutlier()
    {
        // MAD on 3 samples is statistical noise; the documented minimum
        // is 4. A 3-frame "session" with one obvious bloated frame still
        // returns all kept rather than gambling on a tiny-sample MAD.
        var metrics = Frames(2, baseHfd: 2.5f, baseEcc: 0.4f, (Hfd: 10f, Ecc: 0.9f));
        var result = FrameQualityFilter.Filter(metrics, sigma: 3f);
        result.KeptCount.ShouldBe(3);
        result.FloorTriggered.ShouldBeFalse();
        result.Reasons.ShouldAllBe(r => r == FrameRejectReason.Kept);
    }

    [Fact]
    public void Filter_AllIdentical_KeepsAll()
    {
        // MAD = 0 -> threshold = median + 0; no frame exceeds the
        // median (strict >), so nothing is rejected. Correct behaviour.
        var metrics = Frames(10, baseHfd: 2.8f, baseEcc: 0.45f);
        var result = FrameQualityFilter.Filter(metrics, sigma: 3f);
        result.KeptCount.ShouldBe(10);
        result.FloorTriggered.ShouldBeFalse();
    }

    [Fact]
    public void Filter_OneHfdOutlier_DropsIt()
    {
        // 10 frames at HFD 2.5 (ecc 0.5) plus one bloated at HFD 5.0.
        // The bloated frame is the only one above the threshold.
        var metrics = Frames(10, baseHfd: 2.5f, baseEcc: 0.5f, (Hfd: 5f, Ecc: 0.5f));
        var result = FrameQualityFilter.Filter(metrics, sigma: 3f);

        result.KeptCount.ShouldBe(10);
        result.FloorTriggered.ShouldBeFalse();
        result.Reasons[10].ShouldBe(FrameRejectReason.HfdTooBroad);
        // First 10 frames remain Kept.
        for (var i = 0; i < 10; i++)
        {
            result.Reasons[i].ShouldBe(FrameRejectReason.Kept);
        }
    }

    [Fact]
    public void Filter_OneEccOutlier_DropsIt()
    {
        // 10 frames at HFD 2.5, ecc 0.45; one outlier with HFD identical
        // but ecc 0.85 (badly elongated). HfdTooBroad must NOT fire --
        // HFD is fine; only EllipticityTooHigh is the right reason.
        var metrics = Frames(10, baseHfd: 2.5f, baseEcc: 0.45f, (Hfd: 2.5f, Ecc: 0.85f));
        var result = FrameQualityFilter.Filter(metrics, sigma: 3f);

        result.KeptCount.ShouldBe(10);
        result.FloorTriggered.ShouldBeFalse();
        result.Reasons[10].ShouldBe(FrameRejectReason.EllipticityTooHigh);
    }

    [Fact]
    public void Filter_BothHfdAndEccOutOfBounds_ReportsBothFlags()
    {
        // Single outlier fails HFD and ecc simultaneously. With flags
        // both bits are set; the enum literal `HfdTooBroad |
        // EllipticityTooHigh` is the combined reason.
        var metrics = Frames(10, baseHfd: 2.5f, baseEcc: 0.45f, (Hfd: 5f, Ecc: 0.85f));
        var result = FrameQualityFilter.Filter(metrics, sigma: 3f);

        result.KeptCount.ShouldBe(10);
        result.Reasons[10].ShouldBe(FrameRejectReason.HfdTooBroad | FrameRejectReason.EllipticityTooHigh);
    }

    [Fact]
    public void Filter_StarCountOutlier_DropsIt()
    {
        // 10 frames at ~1000 stars (small jitter so MAD > 0), one at
        // 100 stars (cloud / haze). Triggers StarCountTooLow.
        var arr = new FrameMetrics[11];
        for (var i = 0; i < 10; i++)
        {
            arr[i] = new FrameMetrics(MedianHfd: 2.5f, MedianFwhm: 2.5f,
                MedianEllipticity: 0.45f, StarCount: 1000 + i);
        }
        arr[10] = new FrameMetrics(MedianHfd: 2.5f, MedianFwhm: 2.5f,
            MedianEllipticity: 0.45f, StarCount: 100);

        var result = FrameQualityFilter.Filter(arr, sigma: 3f);

        result.KeptCount.ShouldBe(10);
        result.Reasons[10].ShouldBe(FrameRejectReason.StarCountTooLow);
    }

    [Fact]
    public void Filter_WouldRejectMoreThanTheCap_CapsBySeverity()
    {
        // 20 frames where 5 are clear HFD outliers. The cap is passed EXPLICITLY, because this
        // test is about the floor MECHANISM and not about which fraction is configured: the shared
        // default moved from 0.20 to the dataset bake's 0.50 when `tianwen stack` and the bake were
        // unified on one admission rule, and a mechanism test that silently re-tunes itself from a
        // default is a test of the default.
        //
        // At 0.20 the MAD threshold flags all 5 and the floor caps rejection at
        // floor(0.20 * 20) = 4 by severity, so the 5th (least-severe) outlier gets a reprieve.
        //
        // Baseline body has small HFD jitter (0.01 px) so MAD > 0 --
        // a degenerate MAD = 0 distribution can't rank rejects by
        // severity. Outliers are graduated 4.5, 4.8, 5.0, 5.2, 5.5 so
        // the floor reprieves the 4.5 one (smallest deviation from
        // median) and rejects the other four.
        //
        // Note: when >50% of frames are "outliers", the median itself
        // moves into the outliers and the MAD-based detector flips its
        // notion of "what's normal". That's a known limitation of
        // median-based filters and not what this test exercises -- the
        // 20% floor is a guard rail for sessions with a long right
        // tail (typical drift case), not for sessions where the
        // majority of frames are bad.
        var baseline = Enumerable.Range(0, 15)
            .Select(i => (Hfd: 2.50f + 0.005f * (i - 7), Ecc: 0.45f))
            .ToArray();
        var outliers = new[]
        {
            (Hfd: 4.5f, Ecc: 0.45f), (Hfd: 5.0f, Ecc: 0.45f),
            (Hfd: 5.5f, Ecc: 0.45f), (Hfd: 4.8f, Ecc: 0.45f),
            (Hfd: 5.2f, Ecc: 0.45f),
        };
        var metrics = baseline.Concat(outliers)
            .Select(t => new FrameMetrics(t.Hfd, t.Hfd, t.Ecc, 1000))
            .ToArray();
        var result = FrameQualityFilter.Filter(metrics, sigma: 3f, maxRejectFraction: 0.20f);

        result.KeptCount.ShouldBe(16); // 20 - floor(0.20 * 20) = 16
        result.FloorTriggered.ShouldBeTrue();

        // 4 of the 5 outliers should be rejected, baselines all kept.
        for (var i = 0; i < 15; i++)
        {
            result.Reasons[i].ShouldBe(FrameRejectReason.Kept);
        }
        var outlierReasons = result.Reasons.Skip(15).ToArray();
        outlierReasons.Count(r => r != FrameRejectReason.Kept).ShouldBe(4);
        // The reprieved frame must be the lowest-HFD outlier (4.5).
        outlierReasons[0].ShouldBe(FrameRejectReason.Kept);
    }

    /// <summary>
    /// A frame with no detected stars is rejected whatever the gate is set to, including on the
    /// documented "off" path. It cannot be registered and cannot contribute, so this is a validity
    /// check and not a tuning choice.
    ///
    /// <para>The rule used to live in the dataset bake's own analyzer, so `tianwen stack` kept such
    /// frames unconditionally. That is the divergence this pins shut.</para>
    /// </summary>
    [Theory]
    [InlineData(3f)]
    [InlineData(0f)]     // the gate explicitly off
    [InlineData(-1f)]
    public void AFrameWithNoStarsIsRejectedAtAnySigma(float sigma)
    {
        var metrics = Enumerable.Range(0, 10)
            .Select(i => new FrameMetrics(2.5f, 2.5f, 0.45f, i == 4 ? 0 : 1000))
            .ToArray();

        var result = FrameQualityFilter.Filter(metrics, sigma);

        result.Reasons[4].ShouldBe(FrameRejectReason.StarCountTooLow);
        result.KeptCount.ShouldBe(9);
    }

    /// <summary>
    /// A zero-star frame takes no part in the statistics. With one zero among a tight body, a
    /// two-sided MAD that counted it would be inflated by the whole distance to zero, widening the
    /// tolerance for everything else.
    /// </summary>
    [Fact]
    public void AZeroStarFrameDoesNotWidenTheThresholdForTheRest()
    {
        // 19 frames at 1000 stars with one genuine left-tail outlier at 400, plus one zero.
        var metrics = Enumerable.Range(0, 21)
            .Select(i => new FrameMetrics(2.5f + 0.005f * (i % 7), 2.5f, 0.45f,
                i == 0 ? 0 : i == 1 ? 400 : 1000 + (i % 5)))
            .ToArray();

        var result = FrameQualityFilter.Filter(metrics, sigma: 3f);

        result.Reasons[0].ShouldBe(FrameRejectReason.StarCountTooLow);
        result.Reasons[1].ShouldBe(FrameRejectReason.StarCountTooLow);
    }

    /// <summary>
    /// A cap that works out to zero rejected frames reprieves every flagged frame. It used to index
    /// one past the end of a sorted severity copy: `sevSorted[n - maxReject]` with `maxReject == 0`
    /// is `sevSorted[n]`. Reachable on the old 0.20 default at exactly the minimum session length,
    /// since floor(0.20 * 4) is 0.
    /// </summary>
    [Fact]
    public void ACapOfZeroReprievesEveryFlaggedFrameInsteadOfThrowing()
    {
        var metrics = new[]
        {
            new FrameMetrics(2.50f, 2.50f, 0.45f, 1000),
            new FrameMetrics(2.51f, 2.51f, 0.45f, 1000),
            new FrameMetrics(2.49f, 2.49f, 0.45f, 1000),
            new FrameMetrics(9.00f, 9.00f, 0.45f, 1000),   // an unmistakable outlier
        };

        var result = FrameQualityFilter.Filter(metrics, sigma: 3f, maxRejectFraction: 0f);

        result.KeptCount.ShouldBe(4);
        result.FloorTriggered.ShouldBeTrue();
    }

    /// <summary>
    /// The cap is a BOUND, including when severities tie. The previous form took a cutoff VALUE off
    /// a sorted copy and reprieved `severity &lt; cutoff`, which reprieves nobody sitting exactly on
    /// it, so a tie at the cutoff left more than the cap rejected.
    /// </summary>
    [Fact]
    public void TiedSeveritiesCannotPushRejectionPastTheCap()
    {
        // 12 frames: 8 tight, 4 outliers at the IDENTICAL HFD so their severities tie exactly.
        var metrics = Enumerable.Range(0, 12)
            .Select(i => i < 8
                ? new FrameMetrics(2.50f + 0.005f * (i - 4), 2.5f, 0.45f, 1000)
                : new FrameMetrics(6.0f, 6.0f, 0.45f, 1000))
            .ToArray();

        var result = FrameQualityFilter.Filter(metrics, sigma: 3f, maxRejectFraction: 0.25f);

        // floor(0.25 * 12) = 3, so exactly three of the four tied outliers may be rejected.
        (metrics.Length - result.KeptCount).ShouldBe(3);
        result.FloorTriggered.ShouldBeTrue();
    }

    [Fact]
    public void Filter_SigmaZero_NoOp()
    {
        // sigma=0 is the documented "off" path for the field-level
        // option. The filter should keep everything regardless of how
        // bad the outlier looks.
        var metrics = Frames(10, baseHfd: 2.5f, baseEcc: 0.45f, (Hfd: 50f, Ecc: 0.99f));
        var result = FrameQualityFilter.Filter(metrics, sigma: 0f);

        result.KeptCount.ShouldBe(11);
        result.FloorTriggered.ShouldBeFalse();
        result.Reasons.ShouldAllBe(r => r == FrameRejectReason.Kept);
    }

    [Fact]
    public void Filter_SoLPierWLikeDistribution_DropsEarlyBloatedFrames()
    {
        // Mimic the SoL pierW observation: 28 frames at "good" PSF
        // quality (HFD 2.85, ecc 0.55 -- matches the per-frame medians
        // we logged for late pierW), plus 4 "early bloated" frames at
        // HFD ~3.7 (early pierW). Sigma=3.0 should catch all 4 as
        // outliers without triggering the 80% floor (4/32 = 12.5% which
        // is below the 20% cap).
        var bloated = new[]
        {
            (Hfd: 3.67f, Ecc: 0.527f),
            (Hfd: 3.71f, Ecc: 0.522f),
            (Hfd: 3.73f, Ecc: 0.520f),
            (Hfd: 3.65f, Ecc: 0.530f),
        };
        var metrics = Frames(28, baseHfd: 2.85f, baseEcc: 0.55f, bloated);
        var result = FrameQualityFilter.Filter(metrics, sigma: 3f);

        result.KeptCount.ShouldBe(28); // 4 bloated rejected
        result.FloorTriggered.ShouldBeFalse();

        // The 4 bloated frames (indices 28..31) are the rejects. The
        // 28 good frames stay.
        for (var i = 0; i < 28; i++)
        {
            result.Reasons[i].ShouldBe(FrameRejectReason.Kept);
        }
        for (var i = 28; i < 32; i++)
        {
            result.Reasons[i].ShouldBe(FrameRejectReason.HfdTooBroad);
        }
    }
}
