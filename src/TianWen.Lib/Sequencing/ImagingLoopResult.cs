namespace TianWen.Lib.Sequencing;

enum ImageLoopNextAction
{
    AdvanceToNextObservation,
    RepeatCurrentObservation,
    BreakObservationLoop,
    /// <summary>
    /// One or more drivers crossed <see cref="SessionConfiguration.DeviceFaultEscalationThreshold"/>
    /// reconnect attempts during this observation. The session finalises cleanly
    /// (cameras warm up, guider disconnects) — this is the "dead mount doesn't
    /// pretend to be alive" exit path, distinct from per-target Advance.
    /// </summary>
    DeviceUnrecoverable
}

internal readonly record struct MeridianFlipResult(bool Success, double HourAngle)
{
    public static readonly MeridianFlipResult Failed = new(false, double.NaN);
}