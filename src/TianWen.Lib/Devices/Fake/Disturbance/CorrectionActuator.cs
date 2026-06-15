namespace TianWen.Lib.Devices.Fake.Disturbance
{
    /// <summary>
    /// A device that can move the optical axis (or the apparent star) to cancel disturbances.
    /// Correctability is DERIVED, never declared on the term: an actuator inserted at its stage
    /// moves everything from that stage downstream to the sensor, so it can null a disturbance at
    /// its own stage or any stage downstream of it, provided it is also fast enough to track it.
    /// This is why adding a fast sensor-side actuator later flips seeing to "correctable" with no
    /// change to any disturbance term -- the whole point of deriving it.
    /// </summary>
    /// <param name="Stage">The mechanical stage the actuator acts at.</param>
    /// <param name="BandwidthHz">Closed-loop bandwidth in Hz.</param>
    internal readonly record struct CorrectionActuator(DisturbanceStage Stage, double BandwidthHz)
    {
        /// <summary>
        /// True if this actuator can null <paramref name="term"/>: the term must be at or downstream
        /// of the actuator's stage (so the actuator's motion reaches it) AND slow enough for the
        /// actuator to track. A coarse, conservative gate -- "partially correctable" terms (a slow
        /// term near the bandwidth edge) still return true here; the residual emerges from the closed
        /// loop, not from this boolean.
        /// </summary>
        public bool Corrects(IDisturbanceTerm term)
            => term.Stage >= Stage && BandwidthHz >= term.BandwidthHz;

        /// <summary>
        /// The mount guide-pulse actuator: acts at the RA/Dec axes with a ~0.5 Hz closed loop. Reaches
        /// every pointing-stage term (axis / drivetrain / tube) that is slow enough; misses fast gear
        /// noise and everything at the atmosphere stage. The only actuator that exists today.
        /// </summary>
        public static readonly CorrectionActuator MountPulse = new(DisturbanceStage.MountAxis, 0.5);
    }
}
