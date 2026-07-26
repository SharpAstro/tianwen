using System;
using System.Collections.Immutable;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing
{
    /// <summary>
    /// Everything a UI needs to <b>observe</b> a session: get-only state plus the notification events.
    /// Deliberately carries no way to start, stop, or otherwise act on a run -- that is
    /// <see cref="ISession"/>, which extends this.
    /// <para>
    /// <b>Why the split exists</b> (docs/plans/remote-profile.md P3): a session always runs on the node
    /// that owns the hardware, and a client may <i>watch</i> a session running on a remote rig. Splitting
    /// the ~95% observation surface off <see cref="ISession"/> lets a local <c>Session</c> and a remote
    /// mirror be interchangeable wherever the UI only reads -- which is everywhere except the two
    /// bootstrappers that actually launch a run. <c>LiveSessionState</c> holds one of these, so the Live
    /// Session / Guider tabs render either kind unchanged.
    /// </para>
    /// <para>
    /// <b>What is deliberately NOT here: <see cref="ISession.Setup"/>.</b> It exposes live driver objects,
    /// which cannot cross a wire. The UI only ever wanted names and counts off it, so those are exposed
    /// directly as <see cref="TelescopeDisplays"/> / <see cref="MountDisplayName"/> instead. If a new
    /// consumer reaches for <c>Setup</c> to render something, add the display-level fact here rather than
    /// widening the interface back to the driver graph.
    /// </para>
    /// </summary>
    public interface ISessionTelemetry
    {
        ScheduledObservation? ActiveObservation { get; }

        ScheduledObservationTree Observations { get; }

        /// <summary>Current session phase. Updated at each major transition in the run loop.</summary>
        SessionPhase Phase { get; }

        /// <summary>
        /// Per-OTA render facts (camera label + fitted-device flags), one per telescope in
        /// <c>Setup.Telescopes</c> order. Exposed here rather than reached for through
        /// <see cref="ISession.Setup"/> because these are display facts a remote mirror can supply;
        /// the length is also the authoritative OTA count.
        /// </summary>
        ImmutableArray<TelescopeDisplayInfo> TelescopeDisplays { get; }

        /// <summary>Display name of the mount. Same rationale as <see cref="TelescopeDisplays"/>.</summary>
        string MountDisplayName { get; }

        /// <summary>Total number of FITS frames written so far.</summary>
        int TotalFramesWritten { get; }

        /// <summary>Total accumulated exposure time across all written frames.</summary>
        TimeSpan TotalExposureTime { get; }

        /// <summary>Index of the current observation being imaged (or -1 if not started).</summary>
        int CurrentObservationIndex { get; }

        /// <summary>Snapshot of completed auto-focus runs.</summary>
        ImmutableArray<FocusRunRecord> FocusHistory { get; }

        /// <summary>In-progress V-curve samples (position, HFD) during auto-focus. Empty when not focusing.</summary>
        ImmutableArray<(int Position, float Hfd)> ActiveFocusSamples { get; }

        /// <summary>Rolling circular buffer of recent guide error samples (~5 min window).</summary>
        ImmutableArray<GuideErrorSample> GuideSamples { get; }

        /// <summary>Most recent guide stats (RMS, peak errors), or null if guiding hasn't started.</summary>
        GuideStats? LastGuideStats { get; }

        /// <summary>Current guider state string ("Guiding", "Calibrating", "Settling", "Looping", "Stopped", etc.).</summary>
        string? GuiderState { get; }

        /// <summary>Current settle progress, or null if not settling.</summary>
        SettleProgress? GuiderSettleProgress { get; }

        /// <summary>Guide exposure duration per frame.</summary>
        TimeSpan GuideExposure { get; }

        /// <summary>Last guide camera frame as a mono Image, or null. Only populated when guider tab is active.</summary>
        Image? LastGuideFrame { get; }

        /// <summary>Guide star position in frame pixels, or null if not tracking.</summary>
        (double X, double Y)? GuideStarPosition { get; }

        /// <summary>Guide star SNR, or null if not available.</summary>
        double? GuideStarSNR { get; }

        /// <summary>Star profile: horizontal and vertical intensity cross-sections, or null.</summary>
        (float[] H, float[] V)? GuideStarProfile { get; }

        /// <summary>Calibration overlay data for rendering on the guide camera image.</summary>
        CalibrationOverlayData? CalibrationOverlay { get; }

        /// <summary>All frames written during this session.</summary>
        ImmutableArray<ExposureLogEntry> ExposureLog { get; }

        /// <summary>All plate solve attempts recorded during this session, newest last.</summary>
        ImmutableArray<PlateSolveRecord> PlateSolveHistory { get; }

        /// <summary>Cooling ramp samples (temp + power per camera over time).</summary>
        ImmutableArray<CoolingSample> CoolingSamples { get; }

        /// <summary>Recorded phase start timestamps for timeline visualization.</summary>
        ImmutableArray<PhaseTimestamp> PhaseTimeline { get; }

        /// <summary>Per-camera exposure state for live countdown display. One entry per OTA.</summary>
        ImmutableArray<CameraExposureState> CameraStates { get; }

        /// <summary>
        /// Fine-grained activity description within the current phase.
        /// Updated at each sub-step (e.g. "V-curve step 3/9", "Warming -5°C → ambient").
        /// Null when no activity is in progress.
        /// </summary>
        string? CurrentActivity { get; }

        /// <summary>
        /// User-facing reason the session entered <see cref="SessionPhase.Failed"/> (e.g. which device could
        /// not be connected and what to check) -- plain language, no stack traces. Null while the session has
        /// not failed. Surfaces in the GUI notification feed, the hosted <c>/state</c> endpoint, and the CLI.
        /// </summary>
        string? FailureReason { get; }

        /// <summary>Polled mount state (RA, Dec, HA, pier side, slewing, tracking).</summary>
        MountState MountState { get; }

        /// <summary>Per-camera latest frame metrics (star count, HFD, FWHM). One per OTA.</summary>
        FrameMetrics[] LastFrameMetrics { get; }

        /// <summary>
        /// Snapshot of inferred backlash EWMA per focuser, keyed by the focuser device URI.
        /// Updated opportunistically by AutoFocusAsync via <see cref="BacklashEstimator.InferFromVerification"/>.
        /// Consumers (UI / hosting) read this on session end and mirror the values back into
        /// the focuser URI on the active profile so drivers seed from it on the next connect.
        /// Empty until the first AutoFocus run produces a high-confidence sample.
        /// </summary>
        ImmutableDictionary<Uri, BacklashEstimateRecord> FocuserBacklashEstimates { get; }

        /// <summary>
        /// Path to the most recently written FITS file, or null if no frames written yet.
        /// </summary>
        string? LastFramePath { get; }

        /// <summary>
        /// The most recently captured image per camera (in memory). Replaced on each new frame.
        /// Index matches <see cref="CameraStates"/>. Length equals telescope count.
        /// </summary>
        Image?[] LastCapturedImages { get; }

        /// <summary>Fired when the session transitions to a new phase.</summary>
        event EventHandler<SessionPhaseChangedEventArgs>? PhaseChanged;

        /// <summary>Fired after a frame is written to disk.</summary>
        event EventHandler<FrameWrittenEventArgs>? FrameWritten;

        /// <summary>Fired after each plate solve attempt completes (success or failure).</summary>
        event EventHandler<PlateSolveCompletedEventArgs>? PlateSolveCompleted;

        /// <summary>Fired after the FOV-obstruction scout completes (success, transparency, or skip).</summary>
        event EventHandler<ScoutCompletedEventArgs>? ScoutCompleted;

        /// <summary>Fired when the polled guider app-state string changes (e.g. "Guiding" → "LostLock"),
        /// so UIs can surface star-loss / recovery transitions as notifications.</summary>
        event EventHandler<GuiderStateChangedEventArgs>? GuiderStateChanged;

        /// <summary>
        /// Fired when the session needs the user to perform a physical step and confirm before proceeding
        /// (e.g. "switch on the manual flat panel", or — in a future dark-frame flow — "cover the scope").
        /// The handler shows a prompt and calls <see cref="SessionPromptEventArgs.Respond"/> with the user's
        /// decision. A headless caller (CLI / server) that does not subscribe makes the session auto-proceed,
        /// so this never blocks an unattended run. Gated on driver capability at the call site — e.g. the flat
        /// routine only prompts for a calibrator that is present but not
        /// <see cref="Devices.ICoverDriver.CanControlBrightness"/>.
        /// </summary>
        event EventHandler<SessionPromptEventArgs>? PromptRequested;
    }
}
