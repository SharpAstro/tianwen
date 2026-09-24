using Shouldly;
using System;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pure-function tests for <see cref="FocusDriftDetector"/>; the least-squares HFD trend
/// used by the imaging loop's focus-drift check instead of a single-frame ratio comparison.
/// </summary>
public class FocusDriftDetectorTests
{
    private const float ImagingExposureSeconds = 30f;
    private const short ImagingGain = 100;
    // The wheel slot the baseline was focused through (the luminance filter on a typical LRGB wheel).
    private const int BaselineFilter = 0;
    private const int OtherFilter = 3;

    private static FrameMetrics Metrics(float hfd, int stars = 100, float exposureSeconds = ImagingExposureSeconds, short gain = ImagingGain, int filterPosition = BaselineFilter)
        => new FrameMetrics(stars, hfd, hfd * 1.2f, TimeSpan.FromSeconds(exposureSeconds), gain, filterPosition);

    private static readonly FrameMetrics Baseline = Metrics(2.0f);

    [Fact]
    public void Single_frame_outlier_does_not_inflate_the_trend()
    {
        // 13 steady frames at HFD 2.0 then one 20% spike (wind gust / passing haze).
        var history = new FrameMetrics[14];
        for (var k = 0; k < 13; k++)
        {
            history[k] = Metrics(2.0f);
        }
        history[13] = Metrics(2.4f);

        var trend = FocusDriftDetector.EstimateTrendHfd(history, Baseline, fallbackHfd: 2.4f, minSamples: 5);

        // The fit averages the spike out: trend stays well under the raw 2.4 and, at the
        // default 1.07 drift threshold, does not trigger where the single-frame ratio (1.2) would.
        trend.ShouldBeGreaterThan(2.0f);
        trend.ShouldBeLessThan(2.15f);
        (trend / Baseline.MedianHfd).ShouldBeLessThan(1.07f);
    }

    [Fact]
    public void Sustained_linear_drift_projects_to_the_latest_hfd()
    {
        // Monotonic temperature drift: HFD grows 0.05 per frame.
        var history = new FrameMetrics[10];
        for (var k = 0; k < 10; k++)
        {
            history[k] = Metrics(2.0f + 0.05f * k);
        }

        var trend = FocusDriftDetector.EstimateTrendHfd(history, Baseline, fallbackHfd: 0f, minSamples: 5);

        // Exact linear data: the fit evaluated at the newest frame reproduces it.
        trend.ShouldBe(2.45f, 1e-3f);
        (trend / Baseline.MedianHfd).ShouldBeGreaterThan(1.07f);
    }

    [Fact]
    public void Non_comparable_samples_do_not_bias_the_fit()
    {
        // Steady comparable frames interleaved with frames from different acquisition
        // settings (short high-gain exposures) carrying wild HFDs. The original inline
        // implementation divided by the window length instead of the included-sample
        // count, so every skipped sample corrupted slope and intercept.
        var history = new FrameMetrics[12];
        for (var k = 0; k < 12; k++)
        {
            history[k] = k % 2 == 0
                ? Metrics(2.0f)
                : Metrics(99f, exposureSeconds: 1f, gain: 300);
        }

        var trend = FocusDriftDetector.EstimateTrendHfd(history, Baseline, fallbackHfd: 0f, minSamples: 5);

        trend.ShouldBe(2.0f, 1e-3f);
    }

    [Fact]
    public void A_filter_ladder_at_equal_exposure_fits_only_the_baseline_filter()
    {
        // A ladder alternating two filters at the SAME exposure and gain. The other filter sits at a
        // higher HFD purely from chromatic focus shift, and grows over the window, so if it reached the
        // fit it would both lift the level and tilt the slope into a false drift trigger.
        var history = new FrameMetrics[12];
        for (var k = 0; k < 12; k++)
        {
            history[k] = k % 2 == 0
                ? Metrics(2.0f)
                : Metrics(2.6f + 0.05f * k, filterPosition: OtherFilter);
        }

        var trend = FocusDriftDetector.EstimateTrendHfd(history, Baseline, fallbackHfd: 0f, minSamples: 5);

        trend.ShouldBe(2.0f, 1e-3f);
        (trend / Baseline.MedianHfd).ShouldBeLessThan(1.07f);
    }

    [Fact]
    public void Invalid_samples_are_excluded()
    {
        // Frames with too few stars or NaN HFD (cloud passages, failed detection) are skipped.
        var history = new FrameMetrics[12];
        for (var k = 0; k < 12; k++)
        {
            history[k] = (k % 3) switch
            {
                0 => Metrics(2.0f),
                1 => Metrics(50f, stars: 2),
                _ => Metrics(float.NaN),
            };
        }

        var trend = FocusDriftDetector.EstimateTrendHfd(history, Baseline, fallbackHfd: 0f, minSamples: 3);

        trend.ShouldBe(2.0f, 1e-3f);
    }

    [Fact]
    public void Fewer_comparable_samples_than_minimum_falls_back_to_raw_hfd()
    {
        // 8 frames in the window but only 4 comparable: below minSamples the trend is
        // not trusted and the check falls back to the newest frame's raw HFD.
        var history = new FrameMetrics[8];
        for (var k = 0; k < 8; k++)
        {
            history[k] = k < 4 ? Metrics(2.0f) : Metrics(3.0f, exposureSeconds: 1f);
        }

        var trend = FocusDriftDetector.EstimateTrendHfd(history, Baseline, fallbackHfd: 2.34f, minSamples: 5);

        trend.ShouldBe(2.34f);
    }

    [Fact]
    public void Empty_history_falls_back_to_raw_hfd()
    {
        var trend = FocusDriftDetector.EstimateTrendHfd([], Baseline, fallbackHfd: 2.34f, minSamples: 5);

        trend.ShouldBe(2.34f);
    }
}
