using System.Threading;
using System.Threading.Tasks;

namespace Astap.Lib.Astrometry.PlateSolve;

public record struct ImageDim(
    double PixelScale, // arcsec per pixel
    int Width, // pixel
    int Height /* pixel */)
{
    const double ArcSecToDeg = 1.0 / 60.0 * 1.0 / 60.0;

    /// <summary>
    /// Returns field of view in degrees
    /// </summary>
    public (double width, double height) FieldOfView => (ArcSecToDeg * PixelScale * Width, ArcSecToDeg * PixelScale * Height);
}

public interface IPlateSolver
{
    internal const float DefaultRange = 0.03f;

    string Name { get; }

    Task<bool> CheckSupportAsync(CancellationToken cancellationToken = default);

    Task<(double ra, double dec)?> SolveFileAsync(
        string fitsFile,
        ImageDim? imageDim = default,
        float range = DefaultRange,
        (double ra, double dec)? searchOrigin = default,
        double? searchRadius = default,
        CancellationToken cancellationToken = default
    );
}
