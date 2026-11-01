using TianWen.Lib.Devices;

namespace TianWen.Lib.Astrometry.PlateSolve;

internal class AstrometryNetPlateSolverUnix : AstrometryNetPlateSolver
{
    public AstrometryNetPlateSolverUnix(ITimeProvider timeProvider) : base(false, timeProvider)
    {
        // calls base
    }

    /// <inheritdoc/>
    public override float Priority => 0.9f;
}