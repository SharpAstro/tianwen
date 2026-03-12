using System;
using System.Diagnostics;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// A plate solver for testing that delegates to <see cref="CatalogPlateSolver"/> when a
/// catalog DB is provided, and falls back to reading WCS from FITS headers or returning
/// the search origin as-is.
/// </summary>
internal class FakePlateSolver : IPlateSolver
{
    private readonly CatalogPlateSolver? _catalogSolver;

    public FakePlateSolver() { }

    public FakePlateSolver(ICelestialObjectDB db)
    {
        _catalogSolver = new CatalogPlateSolver(db);
    }

    public string Name => "Fake plate solver";

    public float Priority => 0.01f; // small but non-zero priority

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public async Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        // Try catalog solver first when available
        if (_catalogSolver is not null)
        {
            var result = await _catalogSolver.SolveFileAsync(fitsFile, imageDim, range, searchOrigin, searchRadius, cancellationToken);
            if (result.Solution is not null)
            {
                return result;
            }
        }

        var sw = Stopwatch.StartNew();

        // Fall back: read WCS from FITS headers
        if (Image.TryReadFitsFile(fitsFile, out _, out var wcs) && wcs is not null)
        {
            return new PlateSolveResult(wcs, sw.Elapsed);
        }

        // Final fallback: return search origin as-is
        return new PlateSolveResult(searchOrigin, sw.Elapsed);
    }

    public async Task<PlateSolveResult> SolveImageAsync(Image image, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        // Try catalog solver first when available
        if (_catalogSolver is not null)
        {
            var result = await _catalogSolver.SolveImageAsync(image, imageDim, range, searchOrigin, searchRadius, cancellationToken);
            if (result.Solution is not null)
            {
                return result;
            }
        }

        // Fallback: return search origin as-is
        return new PlateSolveResult(searchOrigin, TimeSpan.Zero);
    }
}
