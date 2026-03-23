using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Guider;

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

    /// <summary>
    /// Fine-grained activity description within the current phase.
    /// Updated at each sub-step (e.g. "V-curve step 3/9", "Warming -5°C → ambient").
    /// Null when no activity is in progress.
    /// </summary>
    string? CurrentActivity { get; }

    /// <summary>Fired after a frame is written to disk.</summary>
    event EventHandler<FrameWrittenEventArgs>? FrameWritten;

    Task RunAsync(CancellationToken cancellationToken);
}
