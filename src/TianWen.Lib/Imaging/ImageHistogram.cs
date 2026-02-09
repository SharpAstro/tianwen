using System.Collections.Generic;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Represents an image historgram, useful for star detection.
/// </summary>
/// <param name="Histogram"></param>
/// <param name="Mean"></param>
/// <param name="Total"></param>
/// <param name="Threshold"></param>
/// <param name="Denormalized">when true, image was denormalized to 16-bit</param>
public record class ImageHistogram(uint[] Histogram, float Mean, float Total, float Threshold, bool Denormalized);
