namespace TianWen.Lib.Devices.Fake.Disturbance
{
    /// <summary>
    /// One physical disturbance source on the fake optical chain (periodic error, flexure, wind,
    /// seeing, ...). Implementations are deterministic given their seed and the sequence of
    /// <see cref="DisturbanceContext.ElapsedSeconds"/> they are evaluated at -- stochastic terms hold
    /// internal state but never read a wall clock or an unseeded RNG, so a test replays bit-for-bit.
    /// </summary>
    /// <remarks>
    /// <see cref="Evaluate"/> returns the term's CURRENT TOTAL contribution (not an increment), as
    /// offsets in the mount's native frame: <c>DRaArcsec</c> is RA-coordinate arcsec (NOT scaled by
    /// cos(dec)) and <c>DDecArcsec</c> is Dec arcsec. Consumers convert -- a mount divides RA arcsec
    /// by 54000 to reach hours; a camera divides by the pixel scale to reach pixels.
    /// </remarks>
    internal interface IDisturbanceTerm
    {
        /// <summary>The optical-chain stage this term injects at -- gates which actuator can null it.</summary>
        DisturbanceStage Stage { get; }

        /// <summary>The temporal signature of the term (documentation; see <see cref="DisturbanceCharacter"/>).</summary>
        DisturbanceCharacter Character { get; }

        /// <summary>Nominal frequency content of the term in Hz. A <see cref="CorrectionActuator"/> can only
        /// null a term whose bandwidth is at or below the actuator's closed-loop bandwidth.</summary>
        double BandwidthHz { get; }

        /// <summary>Current total contribution at <paramref name="ctx"/>, in native-frame arcsec
        /// (RA-coordinate arcsec, Dec arcsec). Idempotent for repeated calls at the same
        /// <see cref="DisturbanceContext.ElapsedSeconds"/>.</summary>
        (double DRaArcsec, double DDecArcsec) Evaluate(in DisturbanceContext ctx);

        /// <summary>Resets internal state (stochastic accumulators, RNG, last-evaluated time) so the
        /// term restarts cleanly for a new guide session. Stateless terms are a no-op.</summary>
        void Reset();
    }
}
