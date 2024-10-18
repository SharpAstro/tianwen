namespace TianWen.Lib.Astrometry.PlateSolve;

internal class AstrometryNetPlateSolverMultiPlatform : AstrometryNetPlateSolver
{
    public AstrometryNetPlateSolverMultiPlatform() : base(true)
    {
        // calls base
    }

    /// <inheritdoc/>
    public override float Priority => 0.8f;
}
