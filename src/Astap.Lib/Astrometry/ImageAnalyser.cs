using System.Collections.Generic;
using Astap.Lib.Imaging;

namespace Astap.Lib.Astrometry;

public class ImageAnalyser : IImageAnalyser
{
    public IReadOnlyList<ImagedStar> FindStars(Image image, double snr_min = 20, int max_stars = 500, int max_retries = 2)
        => image.FindStars(snr_min, max_stars, max_retries);
}