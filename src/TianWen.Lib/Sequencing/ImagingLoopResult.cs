using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

enum ImageLoopNextAction
{
    AdvanceToNextObservation,
    RepeatCurrentObservation,
    BreakObservationLoop,
    /// <summary>
    /// One or more drivers crossed <see cref="SessionConfiguration.DeviceFaultEscalationThreshold"/>
    /// reconnect attempts during this observation. The session finalises cleanly
    /// (cameras warm up, guider disconnects): this is the "dead mount doesn't
    /// pretend to be alive" exit path, distinct from per-target Advance.
    /// </summary>
    DeviceUnrecoverable,

    /// <summary>
    /// The mount entered a configured mechanical limit and was stopped or parked. The session
    /// finalises cleanly, exactly like <see cref="DeviceUnrecoverable"/>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="DeviceUnrecoverable"/> because nothing is broken: the rig did what
    /// it was told and reached the edge of where it may point. Collapsing the two would report a
    /// working mount as a faulty one, and would make a limit look like a reason to go and check
    /// cables at 3am.
    /// </remarks>
    LimitReached
}

internal readonly record struct MeridianFlipResult(bool Success, double HourAngle, PointingState PierSide, FlipVerdict Verdict = default)
{
    public static readonly MeridianFlipResult Failed = new(false, double.NaN, PointingState.Unknown, FlipVerdict.Inconclusive);
}