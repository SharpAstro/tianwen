using System.Collections.Generic;

namespace Astap.Lib.Imaging;

public record class ImageHistogram(IReadOnlyList<uint> Histogram, float Mean, float Total, float Threshold);
