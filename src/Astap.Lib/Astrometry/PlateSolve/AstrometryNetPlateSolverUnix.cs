namespace Astap.Lib.Astrometry.PlateSolve;

public class AstrometryNetPlateSolverUnix : AstrometryNetPlateSolver
{
    public AstrometryNetPlateSolverUnix() : base(false)
    {
        // calls base
    }

    /// <inheritdoc/>
    public override float Priority => 0.9f;
}