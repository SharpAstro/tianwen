using System.Collections.Generic;

namespace Astap.Lib.Imaging;

public record class ImageHistogram(uint[] Histogram, float Mean, float Total, float Threshold);
