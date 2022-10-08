using System.Threading;
using System.Threading.Tasks;

namespace Astap.Lib.Astrometry.PlateSolve;

public record struct ImageDim(
    float PixelScale, // arcsec per pixel
    int Width, // pixel
    int Height /* pixel */)
{
    const float ArcSecToDeg = 1.0f / 60.0f * 1.0f / 60.0f;

    /// <summary>
    /// Returns field of view in degrees
    /// </summary>
    public (float width, float height) FieldOfView => (ArcSecToDeg * PixelScale * Width, ArcSecToDeg * PixelScale * Height);
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
