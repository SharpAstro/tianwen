using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Astrometry.Focus;

internal class ImageAnalyser : IImageAnalyser
{
    public Task<IReadOnlyList<ImagedStar>> FindStarsAsync(Image image, float snrMin = 20f, int maxStars = 500, int maxIterations = 2, CancellationToken cancellationToken = default)
        => image.FindStarsAsync(snrMin, maxStars, maxIterations, cancellationToken);
}