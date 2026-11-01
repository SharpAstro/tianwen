using TianWen.Lib.Devices;

namespace TianWen.Lib.Astrometry.PlateSolve;

internal class AstrometryNetPlateSolverMultiPlatform : AstrometryNetPlateSolver
{
    public AstrometryNetPlateSolverMultiPlatform(ITimeProvider timeProvider) : base(true, timeProvider)
    {
        // calls base
    }

    /// <inheritdoc/>
    public override float Priority => 0.8f;
}
