using System.Collections.Generic;
using Astap.Lib.Imaging;

namespace Astap.Lib.Astrometry.Focus;

public class ImageAnalyser : IImageAnalyser
{
    public IReadOnlyList<ImagedStar> FindStars(Image image, float snr_min = 20f, int max_stars = 500, int max_retries = 2)
        => image.FindStars(snr_min, max_stars, max_retries);
}