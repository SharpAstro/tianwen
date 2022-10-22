using Astap.Lib.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Astap.Lib.Astrometry.PlateSolve;

public interface IPlateSolver
{
    internal const float DefaultRange = 0.03f;

    string Name { get; }

    /// <summary>
    /// A number from (0..1), where closer to one mans higher priority.
    /// </summary>
    float Priority { get; }

    Task<bool> CheckSupportAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Solves FITS file <paramref name="fitsFile"/>, given the image resolution and a possible reference location
    /// <paramref name="searchOrigin"/> with a search radius <paramref name="searchRadius"/>.
    /// </summary>
    /// <param name="fitsFile">An absolute path to a FITS file without a lock. Path should be in system native format.</param>
    /// <param name="imageDim"></param>
    /// <param name="range"></param>
    /// <param name="searchOrigin"></param>
    /// <param name="searchRadius"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentOutOfRangeException">Will be thrown if any of the parameters are out of range.</exception>
    /// <exception cref="PlateSolverException">Will be thrown is solving failed to an abnormal error.</exception>
    /// <exception cref="TaskCanceledException">If cancellation was requested via <paramref name="cancellationToken"/> and no solution was found.</exception>
    Task<(double ra, double dec)?> SolveFileAsync(
        string fitsFile,
        ImageDim? imageDim = default,
        float range = DefaultRange,
        (double ra, double dec)? searchOrigin = default,
        double? searchRadius = default,
        CancellationToken cancellationToken = default
    );
}
