using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

public interface ISession : IAsyncDisposable
{
    Setup Setup { get; }

    ScheduledObservation? ActiveObservation { get; }

    ScheduledObservationTree Observations { get; }

    /// <summary>Current session phase. Updated at each major transition in the run loop.</summary>
    SessionPhase Phase { get; }

    /// <summary>Total number of FITS frames written so far.</summary>
    int TotalFramesWritten { get; }

    /// <summary>Total accumulated exposure time across all written frames.</summary>
    TimeSpan TotalExposureTime { get; }

    /// <summary>Index of the current observation being imaged (or -1 if not started).</summary>
    int CurrentObservationIndex { get; }

    /// <summary>Snapshot of completed auto-focus runs.</summary>
    IReadOnlyList<FocusRunRecord> FocusHistory { get; }

    /// <summary>Rolling circular buffer of recent guide error samples (~5 min window).</summary>
    IReadOnlyList<GuideErrorSample> GuideSamples { get; }

    /// <summary>Most recent guide stats (RMS, peak errors), or null if guiding hasn't started.</summary>
    GuideStats? LastGuideStats { get; }

    /// <summary>All frames written during this session.</summary>
    IReadOnlyList<ExposureLogEntry> ExposureLog { get; }

    /// <summary>Cooling ramp samples (temp + power per camera over time).</summary>
    IReadOnlyList<CoolingSample> CoolingSamples { get; }

    /// <summary>Fired when the session transitions to a new phase.</summary>
    event EventHandler<SessionPhaseChangedEventArgs>? PhaseChanged;

    /// <summary>Recorded phase start timestamps for timeline visualization.</summary>
    IReadOnlyList<PhaseTimestamp> PhaseTimeline { get; }

    /// <summary>Per-camera exposure state for live countdown display. One entry per OTA.</summary>
    IReadOnlyList<CameraExposureState> CameraStates { get; }

    /// <summary>
    /// Fine-grained activity description within the current phase.
    /// Updated at each sub-step (e.g. "V-curve step 3/9", "Warming -5°C → ambient").
    /// Null when no activity is in progress.
    /// </summary>
    string? CurrentActivity { get; }

    /// <summary>Polled mount state (RA, Dec, HA, pier side, slewing, tracking).</summary>
    MountState MountState { get; }

    /// <summary>
    /// Path to the most recently written FITS file, or null if no frames written yet.
    /// Used by the UI to show a preview of the last captured frame.
    /// </summary>
    string? LastFramePath { get; }

    /// <summary>
    /// The most recently captured image per camera (in memory). Replaced on each new frame.
    /// Used by the UI to show a live preview without re-reading from disk.
    /// Index matches <see cref="CameraStates"/>. Length equals telescope count.
    /// </summary>
    Image?[] LastCapturedImages { get; }

    /// <summary>Fired after a frame is written to disk.</summary>
    event EventHandler<FrameWrittenEventArgs>? FrameWritten;

    Task RunAsync(CancellationToken cancellationToken);
}
