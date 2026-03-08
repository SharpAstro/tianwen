using System;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal class FakePlateSolver : IPlateSolver
{
    public string Name => "Fake plate solver";

    public float Priority => 0.01f; // small but non-zero priority

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public Task<PlateSolveResult> SolveFileAsync(string fitsFile, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        if (searchOrigin is not null)
        {
            return Task.FromResult(new PlateSolveResult(searchOrigin, TimeSpan.Zero));
        }

        if (Image.TryReadFitsFile(fitsFile, out var image))
        {
            // TODO: Read WCS from FITS file
        }

        return Task.FromResult(new PlateSolveResult(null, TimeSpan.Zero));
    }

    public Task<PlateSolveResult> SolveImageAsync(Image image, ImageDim? imageDim = null, float range = 0.03F, WCS? searchOrigin = null, double? searchRadius = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PlateSolveResult(searchOrigin, TimeSpan.Zero));
    }
}
