using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Sequencing.PolarAlignment
{
    /// <summary>
    /// Tunables for the polar-alignment routine. Kept separate from
    /// <see cref="SessionConfiguration"/> because the routine runs *outside*
    /// of any imaging session — it manipulates a manually-connected mount
    /// directly and never enters the imaging loop.
    /// </summary>
    /// <param name="ExposureRamp">Exposure ladder tried in order until a plate
    /// solve succeeds with at least <see cref="MinStarsForSolve"/> matched stars.
    /// Defaults to <see cref="AdaptiveExposureRamp.DefaultRamp"/>.</param>
    /// <param name="MinStarsForSolve">Minimum matched stars to accept a Phase B
    /// (refining) plate solve as valid, AND the relaxed-threshold gate the
    /// adaptive exposure ramp uses to pick the live-refine exposure rung.
    /// Refining runs at higher cadence and the per-frame chord arc is already
    /// known from Phase A, so we don't need a strict floor here. Default 25:
    /// going lower (e.g. 10) lets the ramp pick a sub-second rung but the
    /// IncrementalSolver loses anchors fast at short exposures (Phase A's
    /// seed frame has 60+ stars at 5 s; a 200 ms refine has 25-30 detected
    /// but only ~5-10 catalog-matched, below the fallback solve threshold)
    /// causing a "no-solve" cascade. 25 keeps the ramp at 500 ms-1 s
    /// typically, where the fast path stays seeded reliably.</param>
    /// <param name="RotationMinStars">Minimum matched stars to accept a Phase A
    /// (rotation) plate solve. The Phase A axis recovery runs end-to-end
    /// geometry on a single (v1, v2) pair, so each pose's plate-solve precision
    /// directly sets the floor on the recovered axis -- well worth holding out
    /// for a longer exposure to clear this rung. Default 50; keep above
    /// <see cref="MinStarsForSolve"/> so the starts-then-refines lifecycle has
    /// progressively looser gates.</param>
    /// <param name="RotationDeg">Phase A RA-axis rotation in degrees. SharpCap
    /// defaults to 90; we default to 45 because shorter rotations give the
    /// user more leeway to start near the meridian without crossing it during
    /// the rotation, and 45 deg is enough to recover the cone half-angle to
    /// sub-arcmin accuracy in practice.</param>
    /// <param name="SettleSeconds">Mount-settle wait between the rotation
    /// command finishing and the second-frame capture starting.</param>
    /// <param name="TargetAccuracyArcmin">Convergence threshold for Phase B —
    /// when both az and alt errors fall below this, the panel signals "done".</param>
    /// <param name="OnDone">What to do with the mount when the user clicks
    /// Done or Cancel: reverse-axis the original rotation (default), park, or
    /// leave in place.</param>
    /// <param name="SaveFrames">If true, copy each captured FITS to a per-run
    /// folder for offline analysis. Default false (frames live in temp).</param>
    /// <param name="MaxFrame2Retries">Maximum retries for the Phase A second
    /// frame after a failed plate solve. The user has likely just bumped the
    /// rig or the mount hasn't settled — give them a few attempts before
    /// failing the routine. Each retry waits <see cref="SettleSeconds"/>.</param>
    /// <param name="SmoothingWindow">Number of recent solves used by the
    /// refinement smoother to compute the EWMA-smoothed error and the
    /// "settled" flag. Larger = smoother readout but slower to react.</param>
    /// <param name="SettleSigmaArcmin">Standard-deviation threshold (arcmin)
    /// of the smoothing window's error magnitude below which the routine
    /// reports "settled" (user has stopped moving the knobs). Independent of
    /// whether the alignment itself is below <see cref="TargetAccuracyArcmin"/>.</param>
    /// <param name="RefineFullSolveInterval">Every Nth refinement frame runs a
    /// full hinted plate solve and re-seeds the incremental solver, instead
    /// of using the fast ROI-centroid path. Refreshes the anchor list against
    /// drift, picks up new bright stars that have rotated into the field, and
    /// resets accumulated affine-fit floating-point error. Set to 0 to
    /// disable (fast path only -- not recommended for long sessions).
    /// Default 30: at typical capture cadence (~2-5 Hz) the full solve fires
    /// every 6-15 s, barely visible to the user but enough to bound drift.</param>
    /// <param name="UseIncrementalSolver">When true (default), Phase B uses the
    /// fast ROI-centroid + affine refit path between full-solve re-seeds.
    /// When false, every refinement tick runs a full hinted plate solve --
    /// useful as an A/B-test bypass when chasing math regressions, or as a
    /// safe fallback on a setup where the incremental anchor tracking is
    /// unreliable.</param>
    /// <param name="ReferenceFrameAverages">Number of plate solves to average
    /// at each Phase A reference pose (v1 before rotation, v2 after rotation
    /// + settle). Each capture's WCS centre is summed as a J2000 unit vector
    /// and renormalised; per-frame plate-solve noise of sigma_raw shrinks to
    /// sigma_raw / sqrt(N). The Phase A axis recovery and the
    /// <see cref="PolarAxisSolver.LiveAxisRefiner"/> v2 baseline both feed
    /// off these references, so a clean reference is the floor of how tight
    /// the live-refining readout can ever be. Default 5: at typical 100-500ms
    /// exposures this adds ~0.5-2.5s per pose to Phase A and reduces
    /// reference noise by ~2.2x. Set 1 to disable (single solve per pose).</param>
    public readonly record struct PolarAlignmentConfiguration(
        ImmutableArray<TimeSpan> ExposureRamp,
        int MinStarsForSolve = 25,
        double RotationDeg = 45.0,
        double SettleSeconds = 5.0,
        double TargetAccuracyArcmin = 1.0,
        PolarAlignmentOnDone OnDone = PolarAlignmentOnDone.ReverseAxisBack,
        bool SaveFrames = false,
        int MaxFrame2Retries = 3,
        int SmoothingWindow = 15,
        double SettleSigmaArcmin = 0.5,
        int RefineFullSolveInterval = 30,
        bool UseIncrementalSolver = true,
        int ReferenceFrameAverages = 5,
        int RotationMinStars = 50)
    {
        /// <summary>
        /// Default configuration: <see cref="AdaptiveExposureRamp.DefaultRamp"/>
        /// + the documented defaults above. Use as a starting point and override
        /// with <c>with</c> expressions where needed.
        /// </summary>
        public static PolarAlignmentConfiguration Default { get; } =
            new(AdaptiveExposureRamp.DefaultRamp);
    }

    /// <summary>
    /// What the routine should do with the mount when the user clicks Done or Cancel.
    /// </summary>
    public enum PolarAlignmentOnDone
    {
        /// <summary>Reverse-axis the recorded Phase-A rotation (rate, duration). Default.</summary>
        ReverseAxisBack = 0,

        /// <summary>Issue <c>ParkAsync</c> after the routine completes.</summary>
        Park = 1,

        /// <summary>Leave the mount where it is after refinement (user is OK with current pose).</summary>
        LeaveInPlace = 2,
    }
}
