using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using static TianWen.Lib.Astrometry.CoordinateUtils;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Sequencing;

internal partial record Session(
    Setup Setup,
    in SessionConfiguration Configuration,
    IPlateSolver PlateSolver,
    IExternal External,
    ScheduledObservationTree Observations
) : ISession
{
    const int UNINITIALIZED_OBSERVATION_INDEX = -1;

    private readonly ConcurrentQueue<GuiderEventArgs> _guiderEvents = [];
    private readonly ConcurrentDictionary<int, FrameMetrics[]> _baselineByObservation = [];
    private readonly ConcurrentDictionary<int, List<FrameMetrics>[]> _baselineSamples = [];
    private int _activeObservation = UNINITIALIZED_OBSERVATION_INDEX;
    private int _spareIndex;
    private int _totalFramesWritten;
    private long _totalExposureTimeTicks;

    // --- Observable session surface ---
    private volatile SessionPhase _phase;
    private volatile string? _currentActivity;
    private readonly ConcurrentQueue<FocusRunRecord> _focusHistory = [];
    private readonly CircularBuffer<GuideErrorSample> _guideSamples = new CircularBuffer<GuideErrorSample>(300);
    private volatile GuideStats? _lastGuideStats;
    private readonly ConcurrentQueue<ExposureLogEntry> _exposureLog = [];
    private readonly ConcurrentQueue<CoolingSample> _coolingSamples = [];
    private readonly ConcurrentQueue<PhaseTimestamp> _phaseTimeline = [];

    public SessionPhase Phase => _phase;
    public string? CurrentActivity => _currentActivity;
    public int TotalFramesWritten => _totalFramesWritten;
    public TimeSpan TotalExposureTime => TimeSpan.FromTicks(Interlocked.Read(ref _totalExposureTimeTicks));
    public int CurrentObservationIndex => _activeObservation;
    public IReadOnlyList<FocusRunRecord> FocusHistory => [.. _focusHistory];
    public IReadOnlyList<GuideErrorSample> GuideSamples => _guideSamples.ToList();
    public GuideStats? LastGuideStats => _lastGuideStats;
    public IReadOnlyList<ExposureLogEntry> ExposureLog => [.. _exposureLog];
    public IReadOnlyList<CoolingSample> CoolingSamples => [.. _coolingSamples];
    public IReadOnlyList<PhaseTimestamp> PhaseTimeline => [.. _phaseTimeline];

    public event EventHandler<SessionPhaseChangedEventArgs>? PhaseChanged;
    public event EventHandler<FrameWrittenEventArgs>? FrameWritten;

    /// <summary>
    /// Per-observation, per-telescope baseline metrics for focus drift and environmental anomaly detection.
    /// Keyed by observation index because metrics vary with sky area, altitude, and guiding quality.
    /// </summary>
    internal IReadOnlyDictionary<int, FrameMetrics[]> BaselineByObservation => _baselineByObservation;

    public ScheduledObservation? ActiveObservation => _activeObservation is int active and >= 0 && active < Observations.Count ? Observations[active] : null;

    private int AdvanceObservation()
    {
        _spareIndex = 0;
        return Interlocked.Increment(ref _activeObservation);
    }

    /// <summary>
    /// Advances the observation index for test purposes, allowing tests to set up
    /// the session state before calling ObservationLoopAsync or ImagingLoopAsync directly.
    /// </summary>
    internal int AdvanceObservationForTest() => AdvanceObservation();

    private void SetPhase(SessionPhase newPhase)
    {
        var old = _phase;
        _phase = newPhase;
        _currentActivity = null; // reset on phase change
        _phaseTimeline.Enqueue(new PhaseTimestamp(newPhase, External.TimeProvider.GetUtcNow()));
        External.AppLogger.LogInformation("Session phase: {OldPhase} → {NewPhase}", old, newPhase);
        PhaseChanged?.Invoke(this, new SessionPhaseChangedEventArgs(old, newPhase));
    }

    internal void AppendGuideErrorSample(GuideErrorSample sample)
    {
        _guideSamples.Add(sample);
    }

    internal void UpdateGuideStats(GuideStats stats)
    {
        _lastGuideStats = stats;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var active = AdvanceObservation();
            // run initialisation code
            if (active == 0)
            {
                SetPhase(SessionPhase.Initialising);
                if (!await InitialisationAsync(cancellationToken))
                {
                    External.AppLogger.LogError("Initialization failed, aborting session.");
                    SetPhase(SessionPhase.Failed);
                    return;
                }
            }
            else if (ActiveObservation is null)
            {
                External.AppLogger.LogInformation("Session complete, finished {ObservationCount} observations, finalizing.", _activeObservation);
                SetPhase(SessionPhase.Complete);
                return;
            }

            SetPhase(SessionPhase.WaitingForDark);
            await WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(cancellationToken).ConfigureAwait(false);

            SetPhase(SessionPhase.Cooling);
            await CoolCamerasToSetpointAsync(Configuration.SetpointCCDTemperature, Configuration.CooldownRampInterval, 80, SetupointDirection.Down, cancellationToken).ConfigureAwait(false);

            // TODO wait until 5 min to astro dark, and/or implement IExternal.IsPolarAligned

            SetPhase(SessionPhase.RoughFocus);
            if (!await InitialRoughFocusAsync(cancellationToken))
            {
                External.AppLogger.LogError("Failed to focus cameras (first time), aborting session.");
                SetPhase(SessionPhase.Failed);
                return;
            }

            SetPhase(SessionPhase.AutoFocus);
            if (!await AutoFocusAllTelescopesAsync(cancellationToken))
            {
                External.AppLogger.LogWarning("Auto-focus did not converge for all telescopes, proceeding with rough focus.");
            }

            SetPhase(SessionPhase.CalibratingGuider);
            await CalibrateGuiderAsync(cancellationToken).ConfigureAwait(false);

            SetPhase(SessionPhase.Observing);
            await ObservationLoopAsync(cancellationToken).ConfigureAwait(false);

            SetPhase(SessionPhase.Complete);
        }
        catch (OperationCanceledException)
        {
            SetPhase(SessionPhase.Aborted);
        }
        catch (Exception e)
        {
            External.AppLogger.LogError(e, "Exception while in main run loop, unrecoverable, aborting session.");
            SetPhase(SessionPhase.Failed);
        }
        finally
        {
            // Remember terminal state so we can restore it after Finalise
            var terminalPhase = _phase;

            // Finalise must complete — park mount, warm cameras, close covers.
            // Uses CancellationToken.None because interrupting warmup could damage hardware.
            SetPhase(SessionPhase.Finalising);
            await Finalise(CancellationToken.None).ConfigureAwait(false);

            // Restore the terminal state so the UI shows Complete/Aborted/Failed, not Finalising
            SetPhase(terminalPhase);
        }
    }


    public async ValueTask DisposeAsync()
    {
        await Setup.DisposeAsync();

        GC.SuppressFinalize(this);
    }

}
