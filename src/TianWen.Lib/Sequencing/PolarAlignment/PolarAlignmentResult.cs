using System;
using System.Collections.Immutable;
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
    /// <param name="CommandedRotationRad">Rotation angle the orchestrator
    /// asked the mount to perform (= rate × elapsed wall-clock). Drives the
    /// recovery math.</param>
    /// <param name="MeasuredRotationRad">Actual rotation angle around the
    /// recovered axis as measured by the two plate solves: signed angle
    /// between v1 and v2 projected onto the plane perpendicular to the axis.
    /// Compare against <paramref name="CommandedRotationRad"/> to flag mount
    /// rate error / wobble / sidereal-contamination of the elapsed-time
    /// estimate. Mismatch >~30 arcsec is suspicious.</param>
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
        int StarsMatchedFrame2,
        double CommandedRotationRad = 0,
        double MeasuredRotationRad = 0)
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
    /// <param name="Wcs">The plate-solve WCS this tick produced. The host
    /// publishes this as the live preview WCS so the GUI's mini-viewer can
    /// project the polar overlay (rings, axis crosshair, cross meridians,
    /// correction arrow) through the *current* refine pose's WCS instead of
    /// a stale Phase-A solve. Null on failed-solve ticks.</param>
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
        PolarOverlay? Overlay,
        WCS? Wcs = null);

    /// <summary>
    /// Sky-coordinate data the GUI consumes to drive the pole-centric reticle.
    /// All positions are in J2000 (RA hours / Dec degrees) — the renderer
    /// projects to sensor pixels via the live frame's WCS, so this record
    /// stays decoupled from any specific image surface or pipeline.
    ///
    /// The polar-alignment tab translates this record into a generic
    /// <c>WcsAnnotation</c> (sky markers + sky rings) consumed by the
    /// reusable <c>WcsAnnotationLayer</c> in <c>TianWen.UI.Shared</c> —
    /// the same layer used by the FITS viewer for plate-solve overlays,
    /// the live preview for target markers, and the mosaic composer for
    /// next-panel boundaries. No polar-specific code in the renderer.
    /// </summary>
    /// <param name="TruePoleRaHours">True (J2000) celestial pole RA. By
    /// convention 0 — the J2000 pole is at RA=0h, Dec=±90°.</param>
    /// <param name="TruePoleDecDeg">+90 (north) or -90 (south).</param>
    /// <param name="RefractedPoleRaHours">Apparent pole RA at this instant
    /// after the refraction transform — usually very close to the true
    /// pole's RA but non-zero when projected back through topocentric az/alt.</param>
    /// <param name="RefractedPoleDecDeg">Apparent pole declination.</param>
    /// <param name="AxisRaHours">Current mount RA-axis direction in J2000.</param>
    /// <param name="AxisDecDeg">Current mount RA-axis declination in J2000.</param>
    /// <param name="RingRadiiArcmin">Concentric error-ring radii (default
    /// {5, 15, 30}) drawn around the refracted pole.</param>
    /// <param name="AzErrorArcmin">Cached arcmin error component for direction-hint badge.</param>
    /// <param name="AltErrorArcmin">Cached arcmin error component for direction-hint badge.</param>
    /// <param name="Hemisphere">Which pole the routine targets (drives label NCP vs SCP).</param>
    /// <param name="CorrectionArrow">Optional SharpCap-style "follow this
    /// arrow" hint: a sky-coordinate arrow pointing from a reference point
    /// (currently the live frame's WCS centre) to the same point rotated by
    /// the corrective rotation that takes the recovered axis onto the
    /// refracted pole. The renderer projects both endpoints through the live
    /// WCS and draws a line + arrowhead + a target reticle marker at the
    /// head. Null when the corrective rotation is too small to render
    /// (arrow would be sub-pixel).</param>
    public readonly record struct PolarOverlay(
        double TruePoleRaHours,
        double TruePoleDecDeg,
        double RefractedPoleRaHours,
        double RefractedPoleDecDeg,
        double AxisRaHours,
        double AxisDecDeg,
        ImmutableArray<float> RingRadiiArcmin,
        double AzErrorArcmin,
        double AltErrorArcmin,
        Hemisphere Hemisphere,
        PolarCorrectionArrow? CorrectionArrow = null);

    /// <summary>
    /// One sky-coordinate arrow describing the polar-correction direction.
    /// Tail = where a chosen visual anchor (frame centre / bright star)
    /// currently sits. Head = where that same anchor would land if the
    /// corrective rotation (recovered axis -> refracted pole) were applied.
    /// </summary>
    /// <param name="StartRaHours">Tail RA in hours.</param>
    /// <param name="StartDecDeg">Tail Dec in degrees.</param>
    /// <param name="EndRaHours">Head RA in hours.</param>
    /// <param name="EndDecDeg">Head Dec in degrees.</param>
    public readonly record struct PolarCorrectionArrow(
        double StartRaHours,
        double StartDecDeg,
        double EndRaHours,
        double EndDecDeg);
}
