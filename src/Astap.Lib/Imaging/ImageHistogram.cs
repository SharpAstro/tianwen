using System.Collections.Generic;

namespace Astap.Lib.Imaging;

public record class ImageHistogram(IReadOnlyList<uint> Histogram, double Mean, ulong Total, uint Threshold);
