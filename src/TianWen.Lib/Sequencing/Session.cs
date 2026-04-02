using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;

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
    private ImmutableArray<(int Position, float Hfd)> _activeFocusSamples = [];
    private readonly CircularBuffer<GuideErrorSample> _guideSamples = new CircularBuffer<GuideErrorSample>(300);
    private volatile GuideStats? _lastGuideStats;
    private volatile string? _guiderState;
    private volatile SettleProgress? _guiderSettleProgress;
    private volatile bool _ditherPending;
    private TimeSpan _guideExposure;
    private readonly ConcurrentQueue<ExposureLogEntry> _exposureLog = [];
    private readonly ConcurrentQueue<CoolingSample> _coolingSamples = [];
    private readonly ConcurrentQueue<PhaseTimestamp> _phaseTimeline = [];
    private volatile CameraExposureState[] _cameraStates = [];

    public SessionPhase Phase => _phase;
    public string? CurrentActivity => _currentActivity;
    public MountState MountState => _mountState;
    private MountState _mountState;

    public string? LastFramePath => _lastFramePath;
    private volatile string? _lastFramePath;

    public Image?[] LastCapturedImages => _lastCapturedImages;
    private volatile Image?[] _lastCapturedImages = [];

    // Persistent viewer channels — allocated once per telescope, reused across frames.
    // Debayer writes directly into these. The viewer reads them for GPU upload.
    private Channel[]?[] _viewerChannels = [];

    public FrameMetrics[] LastFrameMetrics => _lastFrameMetrics;
    private FrameMetrics[] _lastFrameMetrics = [];

    /// <summary>Per-camera frame metrics history for drift detection regression. Last N results per OTA.</summary>
    internal CircularBuffer<FrameMetrics>[] FrameMetricsHistory => _frameMetricsHistory;
    private CircularBuffer<FrameMetrics>[] _frameMetricsHistory = [];
    public int TotalFramesWritten => Volatile.Read(ref _totalFramesWritten);
    public TimeSpan TotalExposureTime => TimeSpan.FromTicks(Interlocked.Read(ref _totalExposureTimeTicks));
    public int CurrentObservationIndex => _activeObservation;
    public ImmutableArray<FocusRunRecord> FocusHistory => [.. _focusHistory];
    public ImmutableArray<(int Position, float Hfd)> ActiveFocusSamples => _activeFocusSamples;
    public ImmutableArray<GuideErrorSample> GuideSamples => [.. _guideSamples];
    public GuideStats? LastGuideStats => _lastGuideStats;
    public string? GuiderState => _guiderState;
    public SettleProgress? GuiderSettleProgress => _guiderSettleProgress;
    public TimeSpan GuideExposure => _guideExposure;
    public Image? LastGuideFrame => Setup.Guider?.Driver?.LastGuideFrame;
    public (double X, double Y)? GuideStarPosition => Setup.Guider?.Driver?.GuideStarPosition;
    public double? GuideStarSNR => Setup.Guider?.Driver?.GuideStarSNR;
    public (float[] H, float[] V)? GuideStarProfile => Setup.Guider?.Driver?.GuideStarProfile;
    public CalibrationOverlayData? CalibrationOverlay => Setup.Guider?.Driver?.CalibrationOverlay;
    public ImmutableArray<ExposureLogEntry> ExposureLog => [.. _exposureLog];
    public ImmutableArray<CoolingSample> CoolingSamples => [.. _coolingSamples];
    public ImmutableArray<PhaseTimestamp> PhaseTimeline => [.. _phaseTimeline];
    public ImmutableArray<CameraExposureState> CameraStates => [.. _cameraStates];

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
        // Re-create frame history on target change — drift baseline is per-target
        for (var i = 0; i < _frameMetricsHistory.Length; i++)
        {
            _frameMetricsHistory[i] = new CircularBuffer<FrameMetrics>(30);
        }
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

        // Reset camera states when leaving Observing (abort/complete/fail)
        if (old is SessionPhase.Observing && newPhase is not SessionPhase.Observing)
        {
            _cameraStates = new CameraExposureState[_cameraStates.Length];
        }
        _phaseTimeline.Enqueue(new PhaseTimestamp(newPhase, External.TimeProvider.GetUtcNow()));
        External.AppLogger.LogInformation("Session phase: {OldPhase} → {NewPhase}", old, newPhase);
        PhaseChanged?.Invoke(this, new SessionPhaseChangedEventArgs(old, newPhase));
    }

    /// <summary>
    /// Polls focuser position, temperature, and moving state for all OTAs.
    /// Updates <see cref="_cameraStates"/> in place. Called each imaging tick.
    /// </summary>
    internal async ValueTask PollDeviceStatesAsync(CancellationToken cancellationToken)
    {
        // Poll focuser state per OTA
        for (var i = 0; i < Setup.Telescopes.Length && i < _cameraStates.Length; i++)
        {
            var telescope = Setup.Telescopes[i];
            var current = _cameraStates[i];

            var focPos = current.FocusPosition;
            var focTemp = current.FocuserTemperature;
            var focMoving = current.FocuserIsMoving;

            if (telescope.Focuser?.Driver is { Connected: true } focuser)
            {
                focPos = await CatchAsync(focuser.GetPositionAsync, cancellationToken, focPos);
                focTemp = await CatchAsync(focuser.GetTemperatureAsync, cancellationToken, focTemp);
                focMoving = await CatchAsync(focuser.GetIsMovingAsync, cancellationToken, focMoving);
            }

            if (focPos != current.FocusPosition || focTemp != current.FocuserTemperature || focMoving != current.FocuserIsMoving)
            {
                _cameraStates[i] = current with
                {
                    FocusPosition = focPos,
                    FocuserTemperature = focTemp,
                    FocuserIsMoving = focMoving
                };
            }
        }

        // Poll mount state
        var mount = Setup.Mount.Driver;
        if (mount.Connected)
        {
            _mountState = new MountState(
                RightAscension: await CatchAsync(mount.GetRightAscensionAsync, cancellationToken, _mountState.RightAscension),
                Declination: await CatchAsync(mount.GetDeclinationAsync, cancellationToken, _mountState.Declination),
                HourAngle: await CatchAsync(mount.GetHourAngleAsync, cancellationToken, _mountState.HourAngle),
                PierSide: await CatchAsync(mount.GetSideOfPierAsync, cancellationToken, _mountState.PierSide),
                IsSlewing: await CatchAsync(mount.IsSlewingAsync, cancellationToken, _mountState.IsSlewing),
                IsTracking: await CatchAsync(mount.IsTrackingAsync, cancellationToken, _mountState.IsTracking));
        }
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
            _cameraStates = new CameraExposureState[Setup.Telescopes.Length];
                _lastCapturedImages = new Image?[Setup.Telescopes.Length];
                _viewerChannels = new Imaging.Channel[]?[Setup.Telescopes.Length];
                _lastFrameMetrics = new FrameMetrics[Setup.Telescopes.Length];
                _frameMetricsHistory = new CircularBuffer<FrameMetrics>[Setup.Telescopes.Length];
                for (var i = 0; i < Setup.Telescopes.Length; i++)
                {
                    _frameMetricsHistory[i] = new CircularBuffer<FrameMetrics>(30); // last 30 frames per OTA
                }
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

            // Initial device state poll after all devices are connected
            await PollDeviceStatesAsync(cancellationToken);

            SetPhase(SessionPhase.WaitingForDark);
            await WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(cancellationToken).ConfigureAwait(false);

            SetPhase(SessionPhase.Cooling);
            await CoolCamerasToSetpointAsync(Configuration.SetpointCCDTemperature, Configuration.CooldownRampInterval, 80, SetupointDirection.Down, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Polls guide stats and settle progress on a timer, feeding the UI during phases
    /// where the imaging loop isn't running (e.g. calibration + initial settle).
    /// Disposed when the calling scope ends.
    /// </summary>
    private sealed class GuideStatsPoller : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;

        public GuideStatsPoller(Session session, Devices.Guider.IGuider guider, IExternal external, CancellationToken parentToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _task = Task.Run(async () =>
            {
                var ct = _cts.Token;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var (appState, _) = await guider.GetStatusAsync(ct);
                        session._guiderState = appState;
                        session._guiderSettleProgress = await guider.GetSettleProgressAsync(ct);

                        if (await guider.GetStatsAsync(ct) is { } gs)
                        {
                            session.UpdateGuideStats(gs);
                            var raErr = gs.LastRaErr ?? 0;
                            var decErr = gs.LastDecErr ?? 0;
                            var isDither = session._ditherPending;
                            if (isDither) session._ditherPending = false;
                            var isSettling = session._guiderState is "Settling";
                            session.AppendGuideErrorSample(new GuideErrorSample(
                                external.TimeProvider.GetUtcNow(), raErr, decErr,
                                gs.LastRaPulseMs ?? 0, gs.LastDecPulseMs ?? 0,
                                isDither, isSettling));
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* ignore transient errors */ }

                    await external.SleepAsync(TimeSpan.FromSeconds(2), ct);
                }
            }, _cts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _task; } catch { /* expected */ }
            _cts.Dispose();
        }
    }
}
