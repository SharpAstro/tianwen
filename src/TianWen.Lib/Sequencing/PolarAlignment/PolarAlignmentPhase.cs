namespace TianWen.Lib.Sequencing.PolarAlignment
{
    /// <summary>
    /// Where the polar-alignment routine is in its lifecycle. Drives the
    /// UI status line and the side-panel button states (Start/Cancel/Done).
    /// Lives in TianWen.Lib so the orchestrator (<see cref="PolarAlignmentSession"/>)
    /// can report sub-phase transitions via <c>IProgress&lt;PolarPhaseUpdate&gt;</c>
    /// without taking a dependency on the UI layer.
    /// </summary>
    public enum PolarAlignmentPhase
    {
        /// <summary>Mode active but routine not yet started; waiting for the user to click Start.</summary>
        Idle = 0,
        /// <summary>Adaptive exposure ramp running on frame 1.</summary>
        ProbingExposure = 1,
        /// <summary>Frame 1 capture complete, mount rotating about the RA axis.</summary>
        Rotating = 2,
        /// <summary>Mount settle wait + frame 2 capture (with retries).</summary>
        Frame2 = 3,
        /// <summary>Phase A solve complete; live refinement loop running.</summary>
        Refining = 4,
        /// <summary>Both <c>IsSettled</c> and <c>IsAligned</c> true; awaiting the user's Done click.</summary>
        Aligned = 5,
        /// <summary>Reverse-axis restore in progress after Done / Cancel.</summary>
        RestoringMount = 6,
        /// <summary>Routine failed (Phase A) — last status message contains the reason.</summary>
        Failed = 7,
    }

    /// <summary>
    /// Phase transition payload reported by <see cref="PolarAlignmentSession"/>
    /// during Phase A. Carries the current phase plus an optional contextual
    /// message so the UI can show e.g. "Rotating 60deg at 4.0deg/s (12.3s
    /// remaining)" instead of a static "Rotating RA axis...". UIs that don't
    /// care about the detail can render <see cref="Phase"/> alone.
    /// </summary>
    /// <param name="Phase">Current phase. Always set.</param>
    /// <param name="Detail">Optional human-readable detail string. Null when
    /// the phase change has no useful additional context (e.g. plain Frame2
    /// at the start of capture). The UI should fall back to a phase-specific
    /// default when null.</param>
    public readonly record struct PolarPhaseUpdate(
        PolarAlignmentPhase Phase,
        string? Detail = null);
}
