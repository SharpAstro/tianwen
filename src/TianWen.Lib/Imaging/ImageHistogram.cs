using System.Collections.Generic;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Represents an image historgram, useful for star detection and stretching.
/// </summary>
/// <param name="Channel">Which channel this histogram was generated for</param>
/// <param name="Histogram">histogram values</param>
/// <param name="Mean"></param>
/// <param name="Total">Number of pixels that landed in the histogram. A COUNT, so an integer
/// type: as a <c>float</c> it was quantised above 2^24, which a 24 MP frame already exceeds.
/// <c>long</c> converts implicitly to <c>float</c>/<c>double</c>, so consumers doing fractional
/// arithmetic on it are unaffected.</param>
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
    long Total,
    float Threshold,
    byte ThresholdPct,
    float? RescaledMaxValue,
    float? Median,
    float? MAD,
    bool IgnoreBlack
);
