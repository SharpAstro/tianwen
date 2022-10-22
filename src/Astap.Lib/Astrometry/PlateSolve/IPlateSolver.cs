using Astap.Lib.Imaging;
using System.Threading;
using System.Threading.Tasks;

namespace Astap.Lib.Astrometry.PlateSolve;

public interface IPlateSolver
{
    internal const float DefaultRange = 0.03f;

    string Name { get; }

    float Priority { get; }

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
