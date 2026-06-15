namespace TianWen.Lib.Devices.Fake.Disturbance
{
    /// <summary>
    /// Everything a <see cref="IDisturbanceTerm"/> needs to evaluate its current contribution.
    /// Carries ABSOLUTE elapsed time (not a per-call delta) so repeated evaluations at the same
    /// instant are idempotent: stochastic terms advance their internal state only when
    /// <see cref="ElapsedSeconds"/> increases, so a mount that reads RA then Dec at the same tick
    /// sees a consistent value rather than double-stepping the noise.
    /// </summary>
    /// <param name="ElapsedSeconds">Seconds since the disturbance baseline was last established
    /// (a sync or slew checkpoint). Drives drift, periodic-error time fallback, flexure, and the
    /// step time of an impulse term.</param>
    /// <param name="RaWormPhaseRadians">RA worm angle in radians (encoder position mod worm period),
    /// the phase positional periodic error is keyed to. <see cref="double.NaN"/> when no encoder is
    /// available, in which case a periodic-error term falls back to a wall-clock sine.</param>
    /// <remarks>
    /// Position-dependent terms (polar misalignment) will extend this with believed RA/Dec and pier
    /// side when they join the model; the additive terms here do not need them.
    /// </remarks>
    internal readonly record struct DisturbanceContext(
        double ElapsedSeconds,
        double RaWormPhaseRadians);
}
