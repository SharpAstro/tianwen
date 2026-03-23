namespace TianWen.Lib.Sequencing;

/// <summary>
/// Represents the current phase of a running session.
/// Transitions are monotonic within a normal run: NotStarted → ... → Complete/Failed/Aborted.
/// </summary>
public enum SessionPhase
{
    NotStarted,
    Initialising,
    WaitingForDark,
    Cooling,
    RoughFocus,
    AutoFocus,
    CalibratingGuider,
    Observing,
    Finalising,
    Complete,
    Failed,
    Aborted
}
