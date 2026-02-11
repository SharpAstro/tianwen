using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Astrometry.PlateSolve;

public interface IPlateSolver : IAsyncSupportedCheck
{
    internal const float DefaultRange = 0.03f;

    string Name { get; }

    /// <summary>
    /// A number from (0..1), where closer to one mans higher priority.
    /// </summary>
    float Priority { get; }

    /// <summary>
    /// Solves FITS file <paramref name="fitsFile"/>, given the image resolution and a possible reference location
    /// <paramref name="searchOrigin"/> with a search radius <paramref name="searchRadius"/>.
    /// </summary>
    /// <param name="fitsFile">An absolute path to a FITS file without a lock. Path should be in system native format.</param>
    /// <param name="imageDim">Image dimensions, used if FITS file does not provide enough information</param>
    /// <param name="range"></param>
    /// <param name="searchOrigin">Prefilled WCS if known</param>
    /// <param name="searchRadius">radius in degrees</param>
    /// <param name="cancellationToken">cancellation token</param>
    /// <returns></returns>
    /// <exception cref="ArgumentOutOfRangeException">Will be thrown if any of the parameters are out of range.</exception>
    /// <exception cref="PlateSolverException">Will be thrown is solving failed to an abnormal error.</exception>
    /// <exception cref="TaskCanceledException">If cancellation was requested via <paramref name="cancellationToken"/> and no solution was found.</exception>
    Task<WCS?> SolveFileAsync(
        string fitsFile,
        ImageDim? imageDim = default,
        float range = DefaultRange,
        WCS? searchOrigin = default,
        double? searchRadius = default,
        CancellationToken cancellationToken = default
    );
}
