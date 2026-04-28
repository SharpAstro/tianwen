using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using DIR.Lib;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Mutable snapshot of a running session, polled each frame by the live session tab.
    /// </summary>
    public class LiveSessionState
    {
        /// <summary>The active session instance, or null if no session is running.</summary>
        public ISession? ActiveSession { get; set; }

        /// <summary>Whether a session is currently running.</summary>
        public bool IsRunning { get; set; }

        /// <summary>
        /// Which mode the live view is in: <see cref="LiveSessionMode.Preview"/>,
        /// <see cref="LiveSessionMode.Session"/>, or <see cref="LiveSessionMode.PolarAlign"/>.
        /// Mutually exclusive — Session and PolarAlign can't both be active. Drives panel
        /// visibility, polling cadences, and the toolbar button states.
        /// </summary>
        public LiveSessionMode Mode { get; set; } = LiveSessionMode.Preview;

        /// <summary>CTS for cancelling the running session (linked to app-level CTS).</summary>
        public CancellationTokenSource? SessionCts { get; set; }



        // --- Cached from ISession (cheap volatile reads, polled each frame) ---

        /// <summary>Current session phase.</summary>
        public SessionPhase Phase { get; set; }

        /// <summary>Total FITS frames written so far.</summary>
        public int TotalFramesWritten { get; set; }

        /// <summary>Total accumulated exposure time.</summary>
        public TimeSpan TotalExposureTime { get; set; }

        /// <summary>Current observation index (-1 = not started).</summary>
        public int CurrentObservationIndex { get; set; } = -1;

        /// <summary>Currently active observation, if any.</summary>
        public ScheduledObservation? ActiveObservation { get; set; }

        /// <summary>Recent guide error samples for the guide graph.</summary>
        public ImmutableArray<GuideErrorSample> GuideSamples { get; set; } = [];

        /// <summary>Most recent guide RMS stats.</summary>
        public GuideStats? LastGuideStats { get; set; }

        /// <summary>Completed auto-focus run snapshots.</summary>
        public ImmutableArray<FocusRunRecord> FocusHistory { get; set; } = [];

        /// <summary>In-progress V-curve samples during auto-focus. Empty when not focusing.</summary>
        public ImmutableArray<(int Position, float Hfd)> ActiveFocusSamples { get; set; } = [];

        /// <summary>All frames written during this session.</summary>
        public ImmutableArray<ExposureLogEntry> ExposureLog { get; set; } = [];

        /// <summary>Cooling ramp samples for the cooling graph.</summary>
        public ImmutableArray<CoolingSample> CoolingSamples { get; set; } = [];

        /// <summary>Phase start timestamps for timeline rendering.</summary>
        public ImmutableArray<PhaseTimestamp> PhaseTimeline { get; set; } = [];

        /// <summary>Per-camera exposure state for countdown display.</summary>
        public ImmutableArray<CameraExposureState> CameraStates { get; set; } = [];

        /// <summary>Polled mount state (RA, Dec, HA, pier side, slewing, tracking).</summary>
        public MountState MountState { get; set; }

        /// <summary>Fine-grained activity description within the current phase.</summary>
        public string? CurrentActivity { get; set; }

        /// <summary>Path to the most recently written FITS frame for mini viewer.</summary>
        public string? LastFramePath { get; set; }

        /// <summary>The most recently captured images per camera for live preview. Null entries until first frame per camera.</summary>
        public Image?[] LastCapturedImages { get; set; } = [];

        /// <summary>Per-camera latest frame analysis result (star count, HFD, frame number).</summary>
        public FrameMetrics[] LastFrameMetrics { get; set; } = [];

        /// <summary>Current guider state string ("Guiding", "Calibrating", etc.).</summary>
        public string? GuiderState { get; set; }

        /// <summary>Current settle progress, or null if not settling.</summary>
        public SettleProgress? GuiderSettleProgress { get; set; }

        /// <summary>Guide exposure duration per frame.</summary>
        public TimeSpan GuideExposure { get; set; }

        /// <summary>Last guide camera frame (mono Image), or null.</summary>
        public Image? LastGuideFrame { get; set; }

        /// <summary>Guide star position in frame pixels, or null.</summary>
        public (double X, double Y)? GuideStarPosition { get; set; }

        /// <summary>Guide star SNR, or null.</summary>
        public double? GuideStarSNR { get; set; }

        /// <summary>Star profile: H and V intensity cross-sections, or null.</summary>
        public (float[] H, float[] V)? GuideStarProfile { get; set; }

        /// <summary>Calibration overlay data for guide camera L-overlay.</summary>
        public CalibrationOverlayData? CalibrationOverlay { get; set; }

        /// <summary>Site timezone offset for displaying times in local site time.</summary>
        public TimeSpan SiteTimeZone { get; set; }

        // --- Preview mode telemetry (populated when !IsRunning, from hub-connected drivers) ---

        /// <summary>
        /// Per-OTA live telemetry snapshot for preview mode.
        /// Index matches ActiveProfile.Data.OTAs. Length 0 when profile has no OTAs.
        /// </summary>
        public ImmutableArray<PreviewOTATelemetry> PreviewOTATelemetry { get; set; } = [];

        /// <summary>Mount telemetry for preview mode (RA/Dec/tracking). Default when no mount connected.
        /// <para>
        /// <b>Thread safety:</b> <see cref="MountState"/> is a record struct of ~50 bytes; an
        /// unsynchronised cross-thread write/read can tear (mix fields from two consecutive
        /// values). The poll continuation runs on a thread pool thread; the render thread reads
        /// per frame. We box the value in a small reference holder so the publish is a single
        /// atomic reference write via <see cref="Interlocked.Exchange{T}(ref T, T)"/> — readers
        /// see one consistent snapshot, no lock on the render hot path.
        /// </para>
        /// </summary>
        private sealed record MountStateHolder(MountState Value);
        private MountStateHolder _previewMountStateHolder = new(default);
        public MountState PreviewMountState
        {
            get => Volatile.Read(ref _previewMountStateHolder).Value;
            set => Interlocked.Exchange(ref _previewMountStateHolder, new MountStateHolder(value));
        }

        /// <summary>Resolved mount display name for preview mode.</summary>
        public string? PreviewMountDisplayName { get; set; }

        /// <summary>Whether a preview exposure is currently in progress (per OTA index).</summary>
        public bool[] PreviewCapturing { get; set; } = [];

        /// <summary>Whether a preview plate-solve is currently running for this OTA.
        /// Used to disable the [Solve] button (rendered as "Solving…") so the user
        /// can't queue duplicate solves while one is grinding through ASTAP.</summary>
        public bool[] PreviewPlateSolving { get; set; } = [];

        /// <summary>Preview capture start time per OTA, used for progress computation.</summary>
        public DateTimeOffset[] PreviewCaptureStart { get; set; } = [];

        /// <summary>Requested preview exposure per OTA (for progress bar).</summary>
        public TimeSpan[] PreviewExposureDuration { get; set; } = [];

        /// <summary>Per-OTA requested exposure time in seconds for next preview.</summary>
        public double[] PreviewExposureSeconds { get; set; } = [];

        /// <summary>Per-OTA requested gain value for next preview exposure (null = camera default).</summary>
        public int?[] PreviewGain { get; set; } = [];

        /// <summary>Per-OTA requested binning for next preview exposure.</summary>
        public short[] PreviewBinning { get; set; } = [];

        /// <summary>Twilight data from PlannerState for preview timeline rendering.</summary>
        public DateTimeOffset AstroDark { get; set; }

        /// <summary>End of astronomical twilight (dawn).</summary>
        public DateTimeOffset AstroTwilight { get; set; }

        /// <summary>Civil sunset time.</summary>
        public DateTimeOffset? CivilSet { get; set; }

        /// <summary>Civil sunrise time.</summary>
        public DateTimeOffset? CivilRise { get; set; }

        /// <summary>Nautical sunset time.</summary>
        public DateTimeOffset? NauticalSet { get; set; }

        /// <summary>Nautical sunrise time.</summary>
        public DateTimeOffset? NauticalRise { get; set; }

        /// <summary>Last preview plate solve result, or null.</summary>
        public PlateSolveResult? PreviewPlateSolveResult { get; set; }

        // --- Polar alignment mode telemetry (populated when Mode == PolarAlign) ---

        /// <summary>Current phase of the polar-alignment routine. Drives the side-panel status line.</summary>
        public PolarAlignmentPhase PolarPhase { get; set; }

        /// <summary>
        /// Phase A two-frame solve result (axis recovery + chord-angle sanity).
        /// Null until Phase A completes; remains set throughout Phase B for the
        /// locked-exposure indicator and the chord-angle verification readout.
        /// </summary>
        public TwoFrameSolveResult? PolarPhaseAResult { get; set; }

        /// <summary>
        /// Latest live refinement tick. Atomic reference replacement so the render thread
        /// always sees a consistent snapshot — the orchestrator runs on a thread pool task.
        /// </summary>
        public LiveSolveResult? LastPolarSolve { get; set; }

        /// <summary>
        /// Free-form status message shown in the side panel ("Press Next before rotating
        /// the RA axis", "Refining (1.2 Hz)", "Aligned within 1' — click Done", or the
        /// failure reason from <see cref="TwoFrameSolveResult.FailureReason"/>).
        /// </summary>
        public string? PolarStatusMessage { get; set; }

        /// <summary>CTS for cancelling an in-flight polar-alignment routine.</summary>
        public CancellationTokenSource? PolarAlignmentCts { get; set; }

        /// <summary>
        /// Source preference for the next polar-alignment run started from the toolbar:
        /// false = main camera at <see cref="MiniViewerState.SelectedCameraIndex"/>,
        /// true = the connected guider (PHD2 or built-in). Toggled by the small "G"
        /// button next to the "PA" toolbar button. Persists across runs within a
        /// session so a user with a guide-cam-only setup doesn't have to re-toggle
        /// every time. Authoritatively re-validated by the signal handler.
        /// </summary>
        public bool PolarAlignUseGuider { get; set; }

        /// <summary>
        /// Working copy of the polar-alignment configuration the user is editing
        /// in the setup panel (visible when <see cref="LiveSessionMode.PolarAlign"/>
        /// is active and <see cref="PolarPhase"/> is <c>Idle</c>). The Start
        /// button posts a snapshot of this through
        /// <c>StartPolarAlignmentSignal.Configuration</c>; the signal handler
        /// uses that value verbatim. Persists across mode switches within a
        /// session, so a user iterating on settings doesn't have to re-enter
        /// them on each Cancel + restart.
        /// </summary>
        public PolarAlignmentConfiguration PolarSetupConfig { get; set; } = PolarAlignmentConfiguration.Default;

        /// <summary>
        /// OTA index currently targeted by keyboard shortcuts and mouse clicks in
        /// preview mode. The GPU tab doesn't need this (each row registers its own
        /// clickable regions), but TUI keyboard shortcuts (Enter = capture, [/] = step
        /// exposure, etc.) act on whichever OTA is selected. Clamped against
        /// <see cref="OtaCount"/> where it's read, so stale values don't crash.
        /// </summary>
        public int SelectedPreviewOtaIndex { get; set; }

        // --- Derived ---

        /// <summary>
        /// Unified OTA count for layout, toolbar, and capture controls.
        /// Session mode: from setup telescopes. Preview mode: from profile OTAs.
        /// </summary>
        public int OtaCount => IsRunning
            ? (ActiveSession?.Setup.Telescopes.Length ?? 1)
            : Math.Max(1, PreviewOTATelemetry.Length);

        // --- UI state ---

        /// <summary>
        /// Dropdown attached to the top-strip mode pill (Preview / Polar Align).
        /// Drives mode switching when no session is running -- selecting Polar Align
        /// posts <c>StartPolarAlignmentSignal</c>, selecting Preview while polar is
        /// active posts <c>CancelPolarAlignmentSignal</c>. Hidden during sessions
        /// (the pill becomes the read-only phase indicator).
        /// </summary>
        public DropdownMenuState ModeDropdown { get; } = new();

        /// <summary>Needs redraw flag for TUI integration.</summary>
        public bool NeedsRedraw { get; set; } = true;

        /// <summary>Whether the abort confirmation strip is showing.</summary>
        public bool ShowAbortConfirm { get; set; }

        /// <summary>Scroll offset for the exposure log list.</summary>
        public int ExposureLogScrollOffset { get; set; }

        /// <summary>Scroll offset for the focus history list.</summary>
        public int FocusHistoryScrollOffset { get; set; }

        /// <summary>
        /// Ensures all per-OTA preview arrays match the given OTA count.
        /// Call before rendering preview mode to avoid index-out-of-range.
        /// </summary>
        public void ResizePreviewArrays(int otaCount)
        {
            if (PreviewCapturing.Length == otaCount)
            {
                return;
            }

            PreviewCapturing = new bool[otaCount];
            PreviewPlateSolving = new bool[otaCount];
            PreviewCaptureStart = new DateTimeOffset[otaCount];
            PreviewExposureDuration = new TimeSpan[otaCount];
            PreviewExposureSeconds = Enumerable.Repeat(5.0, otaCount).ToArray();
            PreviewGain = new int?[otaCount];
            PreviewBinning = Enumerable.Repeat((short)1, otaCount).ToArray();

            if (PreviewOTATelemetry.Length != otaCount)
            {
                PreviewOTATelemetry = [.. Enumerable.Repeat(
                    new PreviewOTATelemetry("", "", double.NaN, double.NaN, double.NaN, false, 0, double.NaN, false, "--", false, false, false),
                    otaCount)];
            }

            if (LastCapturedImages.Length != otaCount)
            {
                LastCapturedImages = new Image?[otaCount];
            }
        }

        /// <summary>
        /// Polls the active session and updates cached fields. Call once per frame.
        /// Designed to be cheap — reads volatile fields, no allocations on steady state.
        /// </summary>
        public void PollSession()
        {
            if (ActiveSession is not { } session)
            {
                return;
            }

            Phase = session.Phase;
            TotalFramesWritten = session.TotalFramesWritten;
            TotalExposureTime = session.TotalExposureTime;
            CurrentObservationIndex = session.CurrentObservationIndex;
            ActiveObservation = session.ActiveObservation;
            GuideSamples = session.GuideSamples;
            LastGuideStats = session.LastGuideStats;
            FocusHistory = session.FocusHistory;
            ActiveFocusSamples = session.ActiveFocusSamples;
            ExposureLog = session.ExposureLog;
            CoolingSamples = session.CoolingSamples;
            PhaseTimeline = session.PhaseTimeline;
            CameraStates = session.CameraStates;
            MountState = session.MountState;
            CurrentActivity = session.CurrentActivity;
            LastFramePath = session.LastFramePath;
            LastCapturedImages = session.LastCapturedImages;
            LastFrameMetrics = session.LastFrameMetrics;
            GuiderState = session.GuiderState;
            GuiderSettleProgress = session.GuiderSettleProgress;
            GuideExposure = session.GuideExposure;
            LastGuideFrame = session.LastGuideFrame;
            GuideStarPosition = session.GuideStarPosition;
            GuideStarSNR = session.GuideStarSNR;
            GuideStarProfile = session.GuideStarProfile;
            CalibrationOverlay = session.CalibrationOverlay;
        }
    }
}
