using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

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

        /// <summary>Site timezone offset for displaying times in local site time.</summary>
        public TimeSpan SiteTimeZone { get; set; }

        // --- UI state ---

        /// <summary>Needs redraw flag for TUI integration.</summary>
        public bool NeedsRedraw { get; set; } = true;

        /// <summary>Whether the abort confirmation strip is showing.</summary>
        public bool ShowAbortConfirm { get; set; }

        /// <summary>Scroll offset for the exposure log list.</summary>
        public int ExposureLogScrollOffset { get; set; }

        /// <summary>Scroll offset for the focus history list.</summary>
        public int FocusHistoryScrollOffset { get; set; }

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
        }
    }
}
