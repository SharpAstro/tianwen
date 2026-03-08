using System;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Astrometry.PlateSolve;

/// <summary>
/// Result of a plate solve attempt, containing the WCS solution (if any) and diagnostics.
/// </summary>
/// <param name="Solution">The WCS solution, or <c>null</c> if solving failed.</param>
/// <param name="Elapsed">Wall-clock time spent solving.</param>
public readonly record struct PlateSolveResult(WCS? Solution, TimeSpan Elapsed)
{
    /// <summary>
    /// Number of catalog stars queried within the search region.
    /// Only populated by solvers that use a local star catalog (e.g. <see cref="CatalogPlateSolver"/>).
    /// </summary>
    public int CatalogStars { get; init; }

    /// <summary>
    /// Number of stars detected in the image.
    /// Only populated by solvers that perform their own star detection.
    /// </summary>
    public int DetectedStars { get; init; }

    /// <summary>
    /// Number of catalog stars successfully projected into the image frame.
    /// </summary>
    public int ProjectedStars { get; init; }

    /// <summary>
    /// Number of star pairs matched between detected and projected catalog stars.
    /// </summary>
    public int MatchedStars { get; init; }

    /// <summary>
    /// Number of iterative refinement passes performed.
    /// </summary>
    public int Iterations { get; init; }
}
