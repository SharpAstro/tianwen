namespace Astap.Lib.Astrometry.PlateSolve;

internal class AstrometryNetPlateSolverUnix : AstrometryNetPlateSolver
{
    public AstrometryNetPlateSolverUnix() : base(false)
    {
        // calls base
    }

    /// <inheritdoc/>
    public override float Priority => 0.9f;
}