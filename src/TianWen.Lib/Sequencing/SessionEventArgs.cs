using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Event args for <see cref="ISession.PhaseChanged"/>.
/// </summary>
public sealed class SessionPhaseChangedEventArgs(SessionPhase oldPhase, SessionPhase newPhase) : EventArgs
{
    public SessionPhase OldPhase { get; } = oldPhase;
    public SessionPhase NewPhase { get; } = newPhase;
}

/// <summary>
/// Event args for <see cref="ISession.FrameWritten"/>.
/// </summary>
public sealed class FrameWrittenEventArgs(ExposureLogEntry entry) : EventArgs
{
    public ExposureLogEntry Entry { get; } = entry;
}

/// <summary>
/// Event args for <see cref="ISession.PlateSolveCompleted"/>.
/// </summary>
public sealed class PlateSolveCompletedEventArgs(PlateSolveRecord record) : EventArgs
{
    public PlateSolveRecord Record { get; } = record;
}

/// <summary>
/// Event args for <see cref="ISession.ScoutCompleted"/>. Fired after the FOV-obstruction
/// scout returns its routing decision (Proceed / Advance) so UIs can surface what just
/// happened — currently an opaque ~30-90s pause between centering and guider start.
/// </summary>
public sealed class ScoutCompletedEventArgs(
    Target target,
    ScoutClassification classification,
    TimeSpan? estimatedClearIn,
    ScoutOutcome outcome,
    int[] starCountsPerOTA) : EventArgs
{
    public Target Target { get; } = target;
    public ScoutClassification Classification { get; } = classification;

    /// <summary>
    /// When <see cref="Classification"/> is <see cref="ScoutClassification.Obstruction"/>,
    /// the trajectory-projected wait until the target's natural altitude clears the nudged
    /// horizon. Null when the target is setting (will never clear) or when classification
    /// was Healthy/Transparency.
    /// </summary>
    public TimeSpan? EstimatedClearIn { get; } = estimatedClearIn;

    /// <summary>
    /// Final routing decision: <see cref="ScoutOutcome.Proceed"/> (start guider, image)
    /// or <see cref="ScoutOutcome.Advance"/> (skip target).
    /// </summary>
    public ScoutOutcome Outcome { get; } = outcome;

    /// <summary>
    /// Pre-nudge scout star counts, one per OTA. Useful for log / chart rendering.
    /// </summary>
    public int[] StarCountsPerOTA { get; } = starCountsPerOTA;
}
