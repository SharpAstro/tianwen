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
public readonly record struct FrameMetrics(int StarCount, float MedianHfd, float MedianFwhm, TimeSpan Exposure, short Gain)
{
    public readonly bool IsValid => StarCount > 3 && MedianHfd > 0 && !float.IsNaN(MedianHfd);

    /// <summary>
    /// Whether this metrics instance was captured with the same acquisition settings as <paramref name="other"/>,
    /// meaning their star metrics (HFD, FWHM, star count) are directly comparable.
    /// </summary>
    public readonly bool IsComparableTo(in FrameMetrics other) => Exposure == other.Exposure && Gain == other.Gain;

    /// <summary>
    /// Border margin fraction (0.1 = 10% border on each side = 80% central region).
    /// Stars outside this region are excluded from the count to avoid false condition
    /// deterioration from tracking drift shifting edge stars out of frame.
    /// </summary>
    public const float BorderMargin = 0.1f;

    public static FrameMetrics FromStarList(StarList stars, TimeSpan exposure, short gain, int imageWidth = 0, int imageHeight = 0)
    {
        if (stars.Count == 0)
        {
            return default;
        }

        var count = stars.Count;

        // If image dimensions provided, only count stars in the central region
        if (imageWidth > 0 && imageHeight > 0)
        {
            var marginX = imageWidth * BorderMargin;
            var marginY = imageHeight * BorderMargin;
            count = 0;
            foreach (var star in stars)
            {
                if (star.XCentroid >= marginX && star.XCentroid <= imageWidth - marginX &&
                    star.YCentroid >= marginY && star.YCentroid <= imageHeight - marginY)
                {
                    count++;
                }
            }
        }

        return new FrameMetrics(
            count,
            stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median),
            stars.MapReduceStarProperty(SampleKind.FWHM, AggregationMethod.Median),
            exposure,
            gain
        );
    }
}
