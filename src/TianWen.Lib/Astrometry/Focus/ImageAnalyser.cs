using System.Collections.Generic;
using Astap.Lib.Imaging;

namespace Astap.Lib.Astrometry.Focus;

internal class ImageAnalyser : IImageAnalyser
{
    public IReadOnlyList<ImagedStar> FindStars(Image image, float snrMin = 20f, int maxStars = 500, int maxIterations = 2)
        => image.FindStars(snrMin, maxStars, maxIterations);
}