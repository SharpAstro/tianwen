namespace Astap.Lib.Astrometry.PlateSolve;

public class AstrometryNetPlateSolverMultiPlatform : AstrometryNetPlateSolver
{
    public AstrometryNetPlateSolverMultiPlatform() : base(true)
    {
        // calls base
    }

    /// <inheritdoc/>
    public override float Priority => 0.8f;
}
