using System;
using System.Collections.Generic;
using System.Threading;
using TianWen.Lib.Devices.Guider;
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
        public IReadOnlyList<GuideErrorSample> GuideSamples { get; set; } = [];

        /// <summary>Most recent guide RMS stats.</summary>
        public GuideStats? LastGuideStats { get; set; }

        /// <summary>Completed auto-focus run snapshots.</summary>
        public IReadOnlyList<FocusRunRecord> FocusHistory { get; set; } = [];

        /// <summary>All frames written during this session.</summary>
        public IReadOnlyList<ExposureLogEntry> ExposureLog { get; set; } = [];

        /// <summary>Cooling ramp samples for the cooling graph.</summary>
        public IReadOnlyList<CoolingSample> CoolingSamples { get; set; } = [];

        /// <summary>Fine-grained activity description within the current phase.</summary>
        public string? CurrentActivity { get; set; }

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
            CurrentActivity = session.CurrentActivity;
        }
    }
}
