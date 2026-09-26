using System;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Aggregated metrics from a single frame's star detection, used as baseline for focus drift
/// and environmental anomaly detection. Lighter weight than keeping a full <see cref="StarList"/>.
/// Includes exposure, gain and filter context because star metrics are not comparable across different
/// acquisition settings (e.g., short high-gain auto-focus exposures vs. longer imaging exposures, or the
/// chromatic focus shift between two filters at equal exposure).
/// Already keyed per-telescope since each OTA has different optics and thus different HFD/FWHM.
/// </summary>
/// <param name="FilterPosition">
/// The filter wheel slot the frame was taken through, -1 when the OTA has no wheel or the slot is unknown.
/// Deliberately required with no default: a metrics instance that forgot its filter would never compare
/// equal to one that stated it, which would silently switch focus-drift detection off.
/// </param>
public readonly record struct FrameMetrics(int StarCount, float MedianHfd, float MedianFwhm, TimeSpan Exposure, short Gain, int FilterPosition)
{
    public readonly bool IsValid => StarCount > 3 && MedianHfd > 0 && !float.IsNaN(MedianHfd);

    /// <summary>
    /// Whether this metrics instance was captured with the same acquisition settings as <paramref name="other"/>
    /// (equal exposure, gain AND filter position), meaning their star metrics (HFD, FWHM, star count) are
    /// directly comparable. Exactly when their <see cref="AcquisitionSetting"/>s are equal.
    /// </summary>
    public readonly bool IsComparableTo(in FrameMetrics other)
        => AcquisitionSetting.Of(this) == AcquisitionSetting.Of(other);

    /// <summary>
    /// Border margin fraction (0.1 = 10% border on each side = 80% central region).
    /// Stars outside this region are excluded from the count to avoid false condition
    /// deterioration from tracking drift shifting edge stars out of frame.
    /// </summary>
    public const float BorderMargin = 0.1f;

    public static FrameMetrics FromStarList(StarList stars, TimeSpan exposure, short gain, int filterPosition, int imageWidth = 0, int imageHeight = 0)
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
            gain,
            filterPosition
        );
    }
}

/// <summary>
/// What <see cref="FrameMetrics.IsComparableTo"/> compares, as a key: two frames are comparable exactly when
/// their settings are equal. The session keeps one focus-drift baseline per setting, so a filter ladder
/// compares each frame with the baseline of its own slot and exposure rather than with whichever slot
/// came last. Kept off <see cref="FrameMetrics"/> itself, which crosses the wire.
/// </summary>
internal readonly record struct AcquisitionSetting(TimeSpan Exposure, short Gain, int FilterPosition)
{
    public static AcquisitionSetting Of(in FrameMetrics metrics) => new AcquisitionSetting(metrics.Exposure, metrics.Gain, metrics.FilterPosition);
}
