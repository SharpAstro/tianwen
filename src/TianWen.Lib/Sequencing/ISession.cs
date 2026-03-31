using System;
using System.Collections.Immutable;
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
    ImmutableArray<FocusRunRecord> FocusHistory { get; }

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

    /// <summary>Cooling ramp samples (temp + power per camera over time).</summary>
    ImmutableArray<CoolingSample> CoolingSamples { get; }

    /// <summary>Fired when the session transitions to a new phase.</summary>
    event EventHandler<SessionPhaseChangedEventArgs>? PhaseChanged;

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

    /// <summary>Polled mount state (RA, Dec, HA, pier side, slewing, tracking).</summary>
    MountState MountState { get; }

    /// <summary>Per-camera latest frame metrics (star count, HFD, FWHM). One per OTA.</summary>
    FrameMetrics[] LastFrameMetrics { get; }

    /// <summary>
    /// Path to the most recently written FITS file, or null if no frames written yet.
    /// </summary>
    string? LastFramePath { get; }

    /// <summary>
    /// The most recently captured image per camera (in memory). Replaced on each new frame.
    /// Index matches <see cref="CameraStates"/>. Length equals telescope count.
    /// </summary>
    Image?[] LastCapturedImages { get; }

    /// <summary>Fired after a frame is written to disk.</summary>
    event EventHandler<FrameWrittenEventArgs>? FrameWritten;

    Task RunAsync(CancellationToken cancellationToken);
}
