using System.Collections.Generic;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Represents an image historgram, useful for star detection and stretching.
/// </summary>
/// <param name="Channel">Which channel this histogram was generated for</param>
/// <param name="Histogram">histogram values</param>
/// <param name="Mean"></param>
/// <param name="Total"></param>
/// <param name="Threshold"></param>
/// <param name="ThresholdPct">Percentage of pixels above the threshold</param>
/// <param name="RescaledMaxValue">when not null, specifies the max pixel value the image was rescaled to</param>
/// <param name="Pedestral">first value that is non-zero</param>
/// <param name="Median">edian pixel value</param>
/// <param name="MAD">Median absolute deviation</param>
/// <param name="IgnoreBlack">Whether the histogram was generated while ignoring black pixels (0,0,0)</param>
public record ImageHistogram(
    int Channel,
    ImmutableArray<uint> Histogram,
    float Mean,
    float Total,
    float Threshold,
    byte ThresholdPct,
    float? RescaledMaxValue,
    float? Median,
    float? MAD,
    bool IgnoreBlack
);
