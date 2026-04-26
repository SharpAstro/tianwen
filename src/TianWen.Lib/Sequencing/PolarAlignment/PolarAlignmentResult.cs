using System;
using System.Numerics;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Sequencing.PolarAlignment
{
    /// <summary>
    /// Result of Phase A: the two-frame plate-solve sequence that recovers
    /// the mount's RA-axis orientation in J2000 and decomposes it against
    /// the apparent celestial pole.
    /// </summary>
    /// <param name="Success">True iff both frames solved and axis recovery
    /// succeeded. When false, see <see cref="FailureReason"/>.</param>
    /// <param name="FailureReason">Free-form description shown to the user
    /// when the solve failed; null on success.</param>
    /// <param name="AxisJ2000">Recovered mount-axis direction in J2000
    /// unit-vector form. <see cref="Vec3.Length"/> = 0 on failure.</param>
    /// <param name="AzErrorRad">Azimuth offset of the axis from the apparent
    /// pole, in radians. Positive = axis east of pole.</param>
    /// <param name="AltErrorRad">Altitude offset of the axis from the apparent
    /// pole, in radians. Positive = axis above pole.</param>
    /// <param name="ChordAngleObservedRad">Observed angular distance between
    /// the two plate-solved frame centres. Used by the sanity check.</param>
    /// <param name="ChordAnglePredictedRad">Predicted chord angle from
    /// (delta, recovered cone half-angle). Mismatch >5 arcsec from observed
    /// indicates mid-rotation slew error or wrong delta.</param>
    /// <param name="LockedExposure">The exposure the adaptive ramp settled on
    /// for the first frame; reused for frame 2 and the entire refine loop.</param>
    /// <param name="StarsMatchedFrame1">Solved-star count in frame 1.</param>
    /// <param name="StarsMatchedFrame2">Solved-star count in frame 2.</param>
    public readonly record struct TwoFrameSolveResult(
        bool Success,
        string? FailureReason,
        Vec3 AxisJ2000,
        double AzErrorRad,
        double AltErrorRad,
        double ChordAngleObservedRad,
        double ChordAnglePredictedRad,
        TimeSpan LockedExposure,
        int StarsMatchedFrame1,
        int StarsMatchedFrame2)
    {
        /// <summary>Convenience: total angular axis-error magnitude in arcmin (sky distance).</summary>
        public double TotalErrorArcmin
        {
            get
            {
                // Sky-distance approximation: dAlt direct, dAz scaled by cos(altitude).
                // Near the pole, altitude ~ |latitude|; we approximate via the cone half-angle
                // implied by AltErrorRad alone, falling back to lat from ChordAngle if needed.
                // For arcmin-level UI, a single fixed cos(midAlt) factor at lat=45 introduces
                // <10% error across most observer latitudes — good enough as a magnitude readout.
                var dAlt = AltErrorRad;
                var dAz = AzErrorRad;
                var rad = Math.Sqrt(dAlt * dAlt + dAz * dAz);
                return rad * (180.0 / Math.PI) * 60.0;
            }
        }
    }

    /// <summary>
    /// One refinement tick during Phase B: a fresh plate-solve plus the
    /// recomputed axis error. Anchored against the original Phase A v1
    /// frame, so the user gets an updated readout each time the live solve
    /// completes.
    /// </summary>
    /// <param name="StarsMatched">Matched-star count of the live frame's solve.</param>
    /// <param name="ExposureUsed">Exposure of the live frame (locked across the routine).</param>
    /// <param name="FitsPath">Path to the saved live FITS frame, or null if not saved.</param>
    /// <param name="AzErrorRad">Raw (per-tick) azimuth error in radians.</param>
    /// <param name="AltErrorRad">Raw (per-tick) altitude error in radians.</param>
    /// <param name="SmoothedAzErrorRad">EWMA-smoothed azimuth error. Drives the
    /// gauge needles so single noisy frames don't bounce the readout.</param>
    /// <param name="SmoothedAltErrorRad">EWMA-smoothed altitude error.</param>
    /// <param name="IsSettled">True when the user has stopped moving the
    /// polar knobs, i.e. the recent error magnitudes have low variance. Does
    /// not imply the alignment is good — see <see cref="IsAligned"/>.</param>
    /// <param name="IsAligned">True when both smoothed errors are below the
    /// configured target accuracy. The user can click "Done" once both
    /// <see cref="IsSettled"/> and <see cref="IsAligned"/> are true.</param>
    /// <param name="ConsecutiveFailedSolves">How many solves in a row failed
    /// before this one succeeded. Surfaces transient bumps (e.g. the user
    /// kicked a leg of the tripod) so the GUI can show a calmer message
    /// instead of red-flashing.</param>
    /// <param name="AxisJ2000">Updated axis direction.</param>
    /// <param name="Overlay">Pixel-space data for the GUI's pole-centric
    /// reticle; null if the WCS could not project the pole onto the sensor
    /// (e.g. severe initial misalignment + narrow FOV).</param>
    public readonly record struct LiveSolveResult(
        int StarsMatched,
        TimeSpan ExposureUsed,
        string? FitsPath,
        double AzErrorRad,
        double AltErrorRad,
        double SmoothedAzErrorRad,
        double SmoothedAltErrorRad,
        bool IsSettled,
        bool IsAligned,
        int ConsecutiveFailedSolves,
        Vec3 AxisJ2000,
        PolarOverlay? Overlay);

    /// <summary>
    /// Pixel-space data the GUI/TUI consume to draw the pole-centric reticle:
    /// the two pole crosses (true vs apparent), the live rotation-axis marker,
    /// and the concentric error rings. All positions are in sensor pixels.
    /// </summary>
    /// <param name="TruePolePx">True (J2000) celestial pole projected onto the
    /// live frame in sensor pixels. Drawn as a white "+".</param>
    /// <param name="RefractedPolePx">Apparent (refracted) pole — the bullseye
    /// the user is steering toward. Drawn as a green "+".</param>
    /// <param name="CurrentAxisPx">Where the recovered mount RA-axis
    /// currently lands on the sensor. Drawn as the moving marker.</param>
    /// <param name="RingRadiiArcmin">Concentric error-ring radii in arcmin
    /// (default {5, 15, 30}). The shader projects each through the WCS to
    /// draw on-sensor circles centred on <see cref="RefractedPolePx"/>.</param>
    /// <param name="AzErrorArcmin">Cached arcmin error component for direction-hint badge.</param>
    /// <param name="AltErrorArcmin">Cached arcmin error component for direction-hint badge.</param>
    /// <param name="Hemisphere">Which pole the routine targets (drives label NCP vs SCP).</param>
    public readonly record struct PolarOverlay(
        Vector2 TruePolePx,
        Vector2 RefractedPolePx,
        Vector2 CurrentAxisPx,
        System.Collections.Immutable.ImmutableArray<float> RingRadiiArcmin,
        double AzErrorArcmin,
        double AltErrorArcmin,
        Hemisphere Hemisphere);
}
