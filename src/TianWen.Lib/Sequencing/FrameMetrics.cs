using System;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Aggregated metrics from a single frame's star detection, used as baseline for focus drift
/// and environmental anomaly detection. Lighter weight than keeping a full <see cref="StarList"/>.
/// Includes exposure and gain context because star metrics are not comparable across different
/// acquisition settings (e.g., short high-gain auto-focus exposures vs. longer imaging exposures).
/// Already keyed per-telescope since each OTA has different optics and thus different HFD/FWHM.
/// </summary>
internal readonly record struct FrameMetrics(int StarCount, float MedianHfd, float MedianFwhm, TimeSpan Exposure, short Gain)
{
    public bool IsValid => StarCount > 3 && MedianHfd > 0 && !float.IsNaN(MedianHfd);

    /// <summary>
    /// Whether this metrics instance was captured with the same acquisition settings as <paramref name="other"/>,
    /// meaning their star metrics (HFD, FWHM, star count) are directly comparable.
    /// </summary>
    public bool IsComparableTo(in FrameMetrics other) => Exposure == other.Exposure && Gain == other.Gain;

    public static FrameMetrics FromStarList(StarList stars, TimeSpan exposure, short gain)
    {
        if (stars.Count == 0)
        {
            return default;
        }

        return new FrameMetrics(
            stars.Count,
            stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median),
            stars.MapReduceStarProperty(SampleKind.FWHM, AggregationMethod.Median),
            exposure,
            gain
        );
    }
}
