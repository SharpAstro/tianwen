namespace TianWen.Lib.Sequencing;

enum ImageLoopNextAction
{
    AdvanceToNextObservation,
    RepeatCurrentObservation,
    BreakObservationLoop
}

internal readonly record struct MeridianFlipResult(bool Success, double HourAngle)
{
    public static readonly MeridianFlipResult Failed = new(false, double.NaN);
}