namespace TianWen.Lib.Sequencing;

/// <summary>How much a note asks of the person reading it.</summary>
public enum NotificationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>A note about a run, as a person reads it.</summary>
public readonly record struct SessionNote(NotificationSeverity Severity, string Message);

/// <summary>
/// The ONE mapping from a run's events to the notes a person reads, for every host that shows them: the GUI's and the
/// TUI's bootstrappers for a run in-process, and a node's feed for its clients (P5b part 4 of
/// docs/plans/hardware-in-the-server.md). There used to be two, and a remote rig's feed read "Initialising -> WaitingForDark"
/// where the same run in-process read "Waiting for astronomical dark...". Null is "nothing worth a note": healthy outcomes
/// stay quiet, so the feed carries what changes what a person does.
/// </summary>
public static class SessionNotes
{
    /// <summary>A run starting: a session's, or a flat run on its own.</summary>
    public static SessionNote ForRunStart(bool flatRun) => Info(flatRun ? "Flat run started" : "Session started");

    /// <summary>A session run entering <paramref name="phase"/>; the terminal phases are <see cref="ForRunEnd"/>'s, once the
    /// run (its Finalise included) has ended.</summary>
    public static SessionNote? ForPhase(SessionPhase phase) => phase switch
    {
        SessionPhase.Initialising => Info("Initialising session…"),
        SessionPhase.WaitingForDark => Info("Waiting for astronomical dark…"),
        SessionPhase.Cooling => Info("Cooling cameras to setpoint…"),
        SessionPhase.RoughFocus => Info("Initial rough focus…"),
        SessionPhase.AutoFocus => Info("Auto-focusing…"),
        SessionPhase.CalibratingGuider => Info("Calibrating guider…"),
        SessionPhase.Observing => Info("Observation loop started"),
        SessionPhase.Finalising => Info("Finalising session…"),
        _ => null,
    };

    /// <summary>A session run that has ended in <paramref name="phase"/>, its failure named in the words it gave.</summary>
    public static SessionNote? ForRunEnd(SessionPhase phase, string? failureReason) => phase switch
    {
        SessionPhase.Complete => Info("Session complete"),
        SessionPhase.Aborted => new SessionNote(NotificationSeverity.Warning, "Session aborted"),
        SessionPhase.Failed => new SessionNote(NotificationSeverity.Error, failureReason is { Length: > 0 } why ? $"Session failed: {why}" : "Session failed"),
        _ => null,
    };

    /// <summary>The status line a flat run shows entering <paramref name="phase"/> (a status, not a note).</summary>
    public static string? FlatStatusForPhase(SessionPhase phase) => phase switch
    {
        SessionPhase.Initialising => "Connecting devices…",
        SessionPhase.Cooling => "Cooling cameras to setpoint…",
        SessionPhase.Flats => "Capturing flats…",
        SessionPhase.Finalising => "Finalising flat run…",
        _ => null,
    };

    /// <summary>A flat run that has ended in <paramref name="phase"/>.</summary>
    public static SessionNote ForFlatRunEnd(SessionPhase phase, string? failureReason) => phase switch
    {
        SessionPhase.Complete => Info("Flats complete"),
        SessionPhase.Aborted => new SessionNote(NotificationSeverity.Warning, "Flat run cancelled"),
        SessionPhase.Failed => new SessionNote(NotificationSeverity.Error, failureReason is { Length: > 0 } why ? $"Flats failed: {why}" : "Flats failed"),
        _ => Info("Flat run finished"),
    };

    /// <summary>
    /// A scout's verdict (the opaque pause between centring and guiding): quiet when healthy, a note when the field is
    /// thin or obstructed, a warning when the run moves on without the target.
    /// </summary>
    public static SessionNote? ForScout(ScoutCompletedEventArgs e) => (e.Classification, e.Outcome) switch
    {
        (ScoutClassification.Healthy, _) => null,
        (ScoutClassification.Transparency, _) =>
            Info($"Scout on {e.Target.Name}: low transparency; proceeding, and the recovery loop engages if it persists."),
        (ScoutClassification.Obstruction, ScoutOutcome.Proceed) =>
            Info($"Scout on {e.Target.Name}: the obstruction cleared during the wait; imaging now."),
        (ScoutClassification.Obstruction, ScoutOutcome.Advance) => new SessionNote(NotificationSeverity.Warning,
            $"Scout on {e.Target.Name}: field obstructed (~{string.Join("/", e.StarCountsPerOTA)} stars against the baseline)"
            + (e.EstimatedClearIn is { } c
                ? $", clearing in {c.TotalMinutes:F0} min; advancing to the next target."
                : " with no usable clear time; advancing to the next target.")),
        _ => null,
    };

    /// <summary>The guider losing its star and finding it again; quiet for ordinary state churn.</summary>
    public static SessionNote? ForGuiderTransition(string? oldState, string? newState) => (oldState, newState) switch
    {
        (_, "LostLock") => new SessionNote(NotificationSeverity.Warning, "Guide star lost, guider is re-acquiring."),
        ("LostLock", "Guiding" or "Settling") => Info("Guide star re-acquired."),
        _ => null,
    };

    private static SessionNote Info(string message) => new SessionNote(NotificationSeverity.Info, message);
}
