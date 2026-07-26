using System;
using System.Threading.Tasks;
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
/// Event args for <see cref="ISession.PromptRequested"/> — a request for the user to perform a physical
/// step (e.g. "switch on the flat panel", "cover the scope for darks") and confirm before the session
/// proceeds. The session raises this and awaits <see cref="Respond"/>; a headless caller that does not
/// subscribe causes the session to auto-proceed. The completion is a
/// <see cref="TaskCompletionSource{Boolean}"/> created with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so the session's awaiting continuation
/// never runs inline on the UI thread that calls <see cref="Respond"/>.
/// </summary>
public sealed class SessionPromptEventArgs(
    string title,
    string message,
    string continueLabel,
    string cancelLabel,
    TaskCompletionSource<bool> completion,
    bool requiresPhysicalPresence = false,
    bool defaultIfUnanswerable = false) : EventArgs
{
    /// <summary>
    /// The answer a handler should give when it turns out <b>nobody can be asked</b> -- e.g. a hosted
    /// event broadcaster that subscribed on behalf of remote clients and then finds none attached.
    /// <c>false</c> (skip the gated step) unless the run opted into
    /// <see cref="UnattendedPromptResponse.Proceed"/>.
    /// <para>
    /// It travels <i>with the prompt</i> so the policy has exactly one owner: the session, which holds the
    /// <see cref="SessionConfiguration"/>. A handler that re-derived it would need the configuration
    /// exposed on <see cref="ISession"/> and could then disagree with the session about the same
    /// question -- which is precisely how a "safe default" quietly becomes two different defaults.
    /// </para>
    /// </summary>
    public bool DefaultIfUnanswerable { get; } = defaultIfUnanswerable;

    /// <summary>Short heading (e.g. "Manual flat panel").</summary>
    public string Title { get; } = title;

    /// <summary>
    /// <c>true</c> when answering requires somebody to be <b>physically at the rig</b> -- switching on a
    /// hand-switched panel, capping the scope. Every prompt raised today is of this kind.
    /// <para>
    /// <b>Why a flag and not left to the wording.</b> A remote observer looking at a mirrored session is
    /// not at the observatory, so confirming from there asserts something they cannot verify -- the same
    /// fabrication as answering the prompt automatically, just performed by a human. A UI needs to say so
    /// plainly, and it must not have to string-match the message prose to work out when. Clients are still
    /// allowed to answer (the operator may be on the phone with someone at the scope, or the panel is on a
    /// smart plug they just toggled); the flag is what lets the UI stop presenting it as a neutral
    /// one-click default.
    /// </para>
    /// </summary>
    public bool RequiresPhysicalPresence { get; } = requiresPhysicalPresence;

    /// <summary>Body instruction (e.g. "Switch on the flat panel for OTA 1, then Continue.").</summary>
    public string Message { get; } = message;

    /// <summary>Label for the proceed action (default "Continue").</summary>
    public string ContinueLabel { get; } = continueLabel;

    /// <summary>Label for the decline action (default "Cancel").</summary>
    public string CancelLabel { get; } = cancelLabel;

    private readonly TaskCompletionSource<bool> _completion = completion;

    /// <summary>
    /// Signals the session's decision: <c>true</c> = proceed, <c>false</c> = decline (skip this step).
    /// Idempotent — a second call is ignored (the first response wins), so a Continue click racing a
    /// session-cancel cannot throw.
    /// </summary>
    public void Respond(bool proceed) => _completion.TrySetResult(proceed);
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
/// Event args for <see cref="ISession.GuiderStateChanged"/>. Fired whenever the polled guider
/// app-state string changes (e.g. "Guiding" → "LostLock" → "Guiding"), so UIs can surface
/// star-loss / recovery transitions as notifications instead of relying on the user to spot a
/// flatlined guide graph.
/// </summary>
public sealed class GuiderStateChangedEventArgs(string? oldState, string? newState) : EventArgs
{
    public string? OldState { get; } = oldState;
    public string? NewState { get; } = newState;
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
