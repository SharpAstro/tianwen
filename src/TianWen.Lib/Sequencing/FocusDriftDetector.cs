using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Pure trend estimator for focus drift detection, the analogue of NINA's
/// <c>AutofocusAfterHFRIncreaseTrigger</c> (configurable sample size + amount threshold).
/// Instead of comparing the single newest frame's HFD against the baseline -- where one bloated
/// frame (wind gust, passing haze, guiding hiccup) triggers a spurious refocus -- fit a
/// least-squares line through the median HFD of the recent comparable frames and evaluate it at
/// the newest frame's position. Real focus drift (temperature) is slow and monotonic, so it
/// survives the fit; single-frame noise is averaged out.
/// </summary>
internal static class FocusDriftDetector
{
    /// <summary>
    /// Least-squares linear fit of median HFD over the samples in <paramref name="history"/>
    /// that are valid and comparable to <paramref name="baseline"/> (same exposure + gain),
    /// evaluated at the newest frame's position -- a de-noised stand-in for the newest frame's
    /// HFD. The frame ordinal within the window is the x coordinate; skipped samples keep their
    /// gap so the slope stays in per-frame units. Returns <paramref name="fallbackHfd"/> (the
    /// raw single-frame HFD) when fewer than <paramref name="minSamples"/> comparable samples
    /// exist or the fit is degenerate.
    /// </summary>
    public static float EstimateTrendHfd(ReadOnlySpan<FrameMetrics> history, in FrameMetrics baseline, float fallbackHfd, int minSamples)
    {
        var included = 0;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (var k = 0; k < history.Length; k++)
        {
            ref readonly var sample = ref history[k];
            if (!sample.IsValid || !sample.IsComparableTo(baseline))
            {
                continue;
            }
            included++;
            sumX += k;
            sumY += sample.MedianHfd;
            sumXY += k * sample.MedianHfd;
            sumX2 += (double)k * k;
        }

        if (included < minSamples)
        {
            return fallbackHfd;
        }

        // Note: the divisor is the count of INCLUDED samples, not the window length --
        // using the window length biases both slope and intercept whenever a sample was
        // skipped (the bug in the original inline implementation).
        var denom = included * sumX2 - sumX * sumX;
        if (denom <= 0)
        {
            // All included samples share one x coordinate (single frame); no trend to fit.
            return fallbackHfd;
        }

        var slope = (included * sumXY - sumX * sumY) / denom;
        var intercept = (sumY - slope * sumX) / included;
        return (float)(slope * (history.Length - 1) + intercept);
    }
}
