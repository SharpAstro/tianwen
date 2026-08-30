using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

internal partial record Session(
    Setup Setup,
    in SessionConfiguration Configuration,
    IPlateSolver PlateSolver,
    IExternal External,
    IServiceProvider ServiceProvider,
    ScheduledObservationTree Observations
) : ISession
{
    private readonly ILogger _logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(Session));
    private readonly ITimeProvider _timeProvider = ServiceProvider.GetRequiredService<ITimeProvider>();

    const int UNINITIALIZED_OBSERVATION_INDEX = -1;

    private readonly ConcurrentQueue<GuiderEventArgs> _guiderEvents = [];
    private readonly ConcurrentDictionary<int, FrameMetrics[]> _baselineByObservation = [];
    private readonly ConcurrentDictionary<int, List<FrameMetrics>[]> _baselineSamples = [];
    private int _activeObservation = UNINITIALIZED_OBSERVATION_INDEX;
    private int _spareIndex;
    private int _totalFramesWritten;
    private long _totalExposureTimeTicks;

    // Per-driver fault accumulator. Incremented by ResilientCall via its
    // onReconnect callback; decremented in the imaging loop after every
    // DeviceFaultDecayFrames successful frames. When any driver crosses
    // Configuration.DeviceFaultEscalationThreshold, the observation loop
    // trips DeviceUnrecoverable and finalises cleanly.
    private readonly ConcurrentDictionary<IDeviceDriver, int> _driverFaultCounts = new();
    private int _framesSinceLastFaultDecay;

    // Consecutive failed telemetry polls per driver. Reset on success. After
    // PROACTIVE_RECONNECT_THRESHOLD failures in a row, PollDriverReadAsync
    // kicks off a ConnectAsync so reconnect is already in flight by the time
    // the next exposure/slew is issued.
    private readonly ConcurrentDictionary<IDeviceDriver, int> _consecutivePollFailures = new();
    private const int PROACTIVE_RECONNECT_THRESHOLD = 3;

    // Per-focuser EWMA of inferred backlash (in steps), measured opportunistically
    // from the verification exposure that AutoFocus already takes. Updated by
    // BacklashEstimator after each successful AutoFocus run; consumed by
    // GetEffectiveBacklash to size overshoot on the next BacklashCompensation move.
    // Sample count gates over-confidence on the first noisy night.
    // Bootstrapped from BacklashHistoryPersistence on first encounter; persisted back
    // to the sidecar JSON after every EWMA update. The actual values also flow back to
    // the focuser URI at session end via the snapshot exposed on ISession.
    private readonly ConcurrentDictionary<IFocuserDriver, BacklashEstimateRecord> _focuserBacklashEstimates = new();
    private readonly ConcurrentDictionary<IFocuserDriver, bool> _focuserBacklashLoaded = new();

    // Safety multiplier on the EWMA to size the overshoot; stay comfortably above
    // the estimate so a slightly-noisy sample doesn't produce a wing on the next run.
    private const double BACKLASH_OVERSHOOT_SAFETY = 1.5;

    // --- Observable session surface ---
    private volatile SessionPhase _phase;
    private volatile string? _currentActivity;
    private volatile string? _failureReason;
    private readonly ConcurrentQueue<FocusRunRecord> _focusHistory = [];
    private ImmutableArray<(int Position, float Hfd)> _activeFocusSamples = [];
    private readonly CircularBuffer<GuideErrorSample> _guideSamples = new CircularBuffer<GuideErrorSample>(300);
    private volatile GuideStats? _lastGuideStats;
    private volatile string? _guiderState;
    private volatile SettleProgress? _guiderSettleProgress;
    private volatile bool _ditherPending;
    private TimeSpan _guideExposure;
    private readonly ConcurrentQueue<ExposureLogEntry> _exposureLog = [];
    private readonly ConcurrentQueue<PlateSolveRecord> _plateSolveHistory = [];
    private readonly ConcurrentQueue<CoolingSample> _coolingSamples = [];
    private readonly ConcurrentQueue<PhaseTimestamp> _phaseTimeline = [];
    private volatile CameraExposureState[] _cameraStates = [];

    public SessionPhase Phase => _phase;
    public string? CurrentActivity => _currentActivity;
    public string? FailureReason => _failureReason;
    public MountState MountState => _mountState;

    /// <summary>
    /// Stamped only where the imaging loop already reads HA for its flip decision (Session.Imaging.cs), so it
    /// costs no extra device I/O and cannot disagree with the decision taken on the same reading. Cleared on
    /// every new observation, since a pending flip belongs to the target that is crossing.
    /// <para>
    /// Held as UTC ticks because the imaging loop writes it and the UI/wire projections read it from another
    /// thread: <c>DateTimeOffset?</c> is well over pointer-size, so a plain field would tear and could
    /// briefly report a nonsense instant. A <c>long</c> is atomic under <see cref="Volatile"/>.
    /// </para>
    /// </summary>
    public DateTimeOffset? MeridianFlipUtc
    {
        get => Volatile.Read(ref _meridianFlipUtcTicks) is var ticks and not 0
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;
        private set => Volatile.Write(ref _meridianFlipUtcTicks, value?.UtcTicks ?? 0);
    }

    private long _meridianFlipUtcTicks;

    // Mount safety limits. _limitActed is the once-per-entry latch (see EnforceMountLimitsAsync);
    // _limitReached is the one-way flag the imaging loop reads to finish the run. Both are written
    // by PollDeviceStatesAsync, which runs on slew waits and focus routines as well as the imaging
    // tick, and read from the loop -- hence Volatile rather than plain fields.
    private bool _limitActed;
    private bool _limitReached;
    private MountLimitVerdict _limitVerdict = MountLimitVerdict.Clear;

    // P5 (mount-safety-limits.md): a mount that stops tracking WITHOUT being asked is reporting its own
    // limit (or a stall), and must be read as a limit stop rather than a fault -- or fought, since the
    // next observation would switch tracking straight back on. _mountStopCommanded is raised by every
    // place this session itself stops tracking (the limit, sky flats, Finalise) and cleared the next
    // time tracking is observed ON; _sawTracking guards the start of a run, where tracking is off
    // legitimately; _untrackedPolls debounces, because a driver that has just finished a goto can be
    // read "not slewing, not yet tracking" for one poll.
    private bool _mountStopCommanded;
    private bool _sawTracking;
    private int _untrackedPolls;
    private const int DriverStopDebouncePolls = 2;

    /// <summary>
    /// The most recent mount-limit verdict, for telemetry and the notification feed.
    /// </summary>
    public MountLimitVerdict MountLimitVerdict => _limitVerdict;

    // Start "unknown" (all-NaN coords), not default(MountState) which would read as a real RA0/Dec0.
    // The first PollDeviceStatesAsync (RunAsync, right after InitialisationAsync) fills it in. UI
    // consumers treat NaN RA as "no pointing yet" and keep the last known value rather than snapping
    // the reticle to the celestial-equator origin during the seconds-long init window.
    private MountState _mountState = new(double.NaN, double.NaN, double.NaN, default, false, false);
    // Reused SOFA transform for converting the mount's native (JNOW) pointing to J2000 for the
    // sky-map reticle. Built once from site conditions (fixed for the session) with only its
    // DateTime refreshed per poll; dropped + rebuilt if a conversion ever returns NaN (e.g. it
    // was built before the site sync). Mirrors AppSignalHandler's preview-poll conversion.
    private Transform? _mountStateTransform;

    public string? LastFramePath => _lastFramePath;
    private volatile string? _lastFramePath;

    public Image?[] LastCapturedImages => _lastCapturedImages;
    private volatile Image?[] _lastCapturedImages = [];

    // Persistent viewer channels: allocated once per telescope, reused across frames.
    // Debayer writes directly into these. The viewer reads them for GPU upload.
    private Channel[]?[] _viewerChannels = [];

    // SPCC caching: per-channel system throughput only recomputed on filter/camera change
    private (FilterCurve R, FilterCurve G, FilterCurve B)? _cachedTsys;
    private string? _cachedTsysKey; // composite key: sensorModel|filterName|sensorType

    public FrameMetrics[] LastFrameMetrics => _lastFrameMetrics;
    private FrameMetrics[] _lastFrameMetrics = [];

    public ImmutableDictionary<Uri, BacklashEstimateRecord> FocuserBacklashEstimates
    {
        get
        {
            // Build a URI-keyed snapshot from the driver-keyed dictionary by walking the Setup.
            // Setup is small (≤ a few telescopes); this is only called at session-end so the
            // O(N) walk is fine vs. carrying a parallel URI-keyed dictionary on every update.
            var builder = ImmutableDictionary.CreateBuilder<Uri, BacklashEstimateRecord>();
            foreach (var telescope in Setup.Telescopes)
            {
                if (telescope.Focuser is { Driver: { } driver, Device: { } device }
                    && _focuserBacklashEstimates.TryGetValue(driver, out var record))
                {
                    builder[device.DeviceUri] = record;
                }
            }
            return builder.ToImmutable();
        }
    }

    /// <summary>Per-camera frame metrics history for drift detection regression. Last N results per OTA.</summary>
    private CircularBuffer<FrameMetrics>[] _frameMetricsHistory = [];
    public int TotalFramesWritten => Volatile.Read(ref _totalFramesWritten);
    public TimeSpan TotalExposureTime => TimeSpan.FromTicks(Interlocked.Read(ref _totalExposureTimeTicks));
    public int CurrentObservationIndex => _activeObservation;
    /// <summary>Number of meridian flips actually performed or detected this session. Test seam used to
    /// assert that a non-German mount (fork / Alt-Az) images across the meridian without ever flipping.</summary>
    internal int MeridianFlipCount { get; private set; }
    public ImmutableArray<FocusRunRecord> FocusHistory => [.. _focusHistory];
    public ImmutableArray<(int Position, float Hfd)> ActiveFocusSamples => _activeFocusSamples;
    public ImmutableArray<GuideErrorSample> GuideSamples => _guideSamples.Snapshot;
    public GuideStats? LastGuideStats => _lastGuideStats;
    public string? GuiderState => _guiderState;
    public SettleProgress? GuiderSettleProgress => _guiderSettleProgress;
    public TimeSpan GuideExposure => _guideExposure;
    public Image? LastGuideFrame => Setup.Guider?.Driver?.LastGuideFrame;

    /// <inheritdoc/>
    public int LastGuideFrameNumber => Setup.Guider?.Driver?.LastGuideFrameNumber ?? 0;
    public (double X, double Y)? GuideStarPosition => Setup.Guider?.Driver?.GuideStarPosition;
    public double? GuideStarSNR => Setup.Guider?.Driver?.GuideStarSNR;
    public (float[] H, float[] V)? GuideStarProfile => Setup.Guider?.Driver?.GuideStarProfile;
    public CalibrationOverlayData? CalibrationOverlay => Setup.Guider?.Driver?.CalibrationOverlay;
    public ImmutableArray<ExposureLogEntry> ExposureLog => [.. _exposureLog];
    public ImmutableArray<PlateSolveRecord> PlateSolveHistory => [.. _plateSolveHistory];
    public ImmutableArray<CoolingSample> CoolingSamples => [.. _coolingSamples];
    public ImmutableArray<PhaseTimestamp> PhaseTimeline => [.. _phaseTimeline];
    public ImmutableArray<CameraExposureState> CameraStates => [.. _cameraStates];

    // Display facts lifted off Setup so observers never need the driver graph (see ISessionTelemetry).
    // Built once: Setup's device set is fixed for the life of a session, and the UI polls this per frame.
    private ImmutableArray<TelescopeDisplayInfo> _telescopeDisplays;

    public ImmutableArray<TelescopeDisplayInfo> TelescopeDisplays
    {
        get
        {
            if (_telescopeDisplays.IsDefault)
            {
                var telescopes = Setup.Telescopes;
                var displays = ImmutableArray.CreateBuilder<TelescopeDisplayInfo>(telescopes.Length);
                foreach (var telescope in telescopes)
                {
                    displays.Add(new TelescopeDisplayInfo(
                        telescope.Camera.Device.DisplayName,
                        telescope.Focuser is not null,
                        telescope.FilterWheel is not null));
                }
                _telescopeDisplays = displays.MoveToImmutable();
            }
            return _telescopeDisplays;
        }
    }

    public string MountDisplayName => Setup.Mount.Device.DisplayName;

    public event EventHandler<SessionPhaseChangedEventArgs>? PhaseChanged;
    public event EventHandler<FrameWrittenEventArgs>? FrameWritten;
    public event EventHandler<PlateSolveCompletedEventArgs>? PlateSolveCompleted;
    public event EventHandler<ScoutCompletedEventArgs>? ScoutCompleted;
    public event EventHandler<GuiderStateChangedEventArgs>? GuiderStateChanged;
    public event EventHandler<SessionPromptEventArgs>? PromptRequested;

    /// <summary>
    /// Asks the user to perform a physical step and confirm, via <see cref="PromptRequested"/>. Returns
    /// <c>true</c> to proceed, <c>false</c> to decline (the caller then skips the gated step). When a
    /// handler is present it raises the prompt and awaits the response, bounded by
    /// <paramref name="cancellationToken"/>: a session cancel while the prompt is open resolves to
    /// <c>false</c>.
    /// <para>
    /// With <b>no</b> interactive handler subscribed (CLI / server / tests) it logs the instruction and
    /// answers <see cref="SessionConfiguration.UnattendedPromptResponse"/>, which defaults to
    /// <see cref="UnattendedPromptResponse.Decline"/>, i.e. skip the gated step. It deliberately does
    /// <b>not</b> hardcode "proceed": these prompts gate physical prerequisites, so proceeding asserts an
    /// act that nobody performed. See <see cref="UnattendedPromptResponse"/> for the full reasoning and
    /// for why blocking forever is equally unsafe.
    /// </para>
    /// </summary>
    /// <param name="requiresPhysicalPresence">Whether answering needs somebody at the rig. Travels to the
    /// UI on <see cref="SessionPromptEventArgs.RequiresPhysicalPresence"/> so a remote observer is warned
    /// that confirming from elsewhere asserts something they cannot see.</param>
    private async ValueTask<bool> RequestUserConfirmationAsync(
        string title, string message, CancellationToken cancellationToken,
        string continueLabel = "Continue", string cancelLabel = "Cancel",
        bool requiresPhysicalPresence = false)
    {
        var handler = PromptRequested;
        if (handler is null)
        {
            var unattended = Configuration.UnattendedPromptResponse;
            var proceed = unattended is UnattendedPromptResponse.Proceed;
            _logger.LogInformation(
                "User step, nobody to ask ({Response}): {Title}; {Message}",
                proceed ? "proceeding" : "skipping", title, message);
            return proceed;
        }

        // RunContinuationsAsynchronously: the UI thread calls Respond(); without this the session's
        // await-continuation would resume inline on that UI thread.
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _logger.LogInformation("Waiting for user: {Title}, {Message}", title, message);
        handler.Invoke(this, new SessionPromptEventArgs(
            title, message, continueLabel, cancelLabel, completion, requiresPhysicalPresence,
            // Carry the unattended policy to the handler: a hosted broadcaster may have subscribed for
            // remote clients and find none attached, and it must then answer exactly as the session
            // would have with no handler at all.
            defaultIfUnanswerable: Configuration.UnattendedPromptResponse is UnattendedPromptResponse.Proceed,
            // Stamped here, at the one place a prompt is born, so every observer -- this node's UI and a
            // remote board alike -- ages it from the same instant.
            raisedUtc: _timeProvider.GetUtcNow()));

        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled while the prompt was open: treat as "do not proceed".
            return false;
        }
    }

    /// <summary>
    /// Single write path for the polled guider app-state: raises <see cref="GuiderStateChanged"/>
    /// on transitions (e.g. "Guiding" → "LostLock") so UIs can surface star-loss / recovery as
    /// notifications instead of relying on the user to spot a flatlined guide graph. Called from
    /// both pollers (the calibration-phase <see cref="GuideStatsPoller"/> and the imaging-loop
    /// tick), so transition detection lives here, not at the poll sites.
    /// </summary>
    private void UpdateGuiderState(string? appState)
    {
        var previous = _guiderState;
        if (previous == appState)
        {
            return;
        }

        _guiderState = appState;
        GuiderStateChanged?.Invoke(this, new GuiderStateChangedEventArgs(previous, appState));
    }

    /// <summary>
    /// Per-observation, per-telescope baseline metrics for focus drift and environmental anomaly detection.
    /// Keyed by observation index because metrics vary with sky area, altitude, and guiding quality.
    /// </summary>
    internal IReadOnlyDictionary<int, FrameMetrics[]> BaselineByObservation => _baselineByObservation;

    public ScheduledObservation? ActiveObservation => _activeObservation is int active and >= 0 && active < Observations.Count ? Observations[active] : null;

    private int AdvanceObservation()
    {
        _spareIndex = 0;
        // Reset frame history on target change: drift baseline is per-target
        for (var i = 0; i < _frameMetricsHistory.Length; i++)
        {
            _frameMetricsHistory[i].Clear();
        }
        return Interlocked.Increment(ref _activeObservation);
    }

    /// <summary>
    /// Advances the observation index for test purposes, allowing tests to set up
    /// the session state before calling ObservationLoopAsync or ImagingLoopAsync directly.
    /// </summary>
    internal int AdvanceObservationForTest() => AdvanceObservation();

    /// <summary>
    /// Seeds a baseline for a specific observation index. Used by FOV obstruction tests
    /// to set up the "previous target's baseline" referenced by <c>ScoutAndProbeAsync</c>
    /// without running the full prior observation through the imaging loop.
    /// </summary>
    internal void SetBaselineForObservationForTest(int obsIndex, FrameMetrics[] baseline)
        => _baselineByObservation[obsIndex] = baseline;

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
        _phaseTimeline.Enqueue(new PhaseTimestamp(newPhase, _timeProvider.GetUtcNow()));
        _logger.LogInformation("Session phase: {OldPhase} → {NewPhase}", old, newPhase);
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
                focPos = await PollDriverReadAsync(focuser, focuser.GetPositionAsync, focPos, cancellationToken);
                focTemp = await PollDriverReadAsync(focuser, focuser.GetTemperatureAsync, focTemp, cancellationToken);
                focMoving = await PollDriverReadAsync(focuser, focuser.GetIsMovingAsync, focMoving, cancellationToken);
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
            var ra = await PollDriverReadAsync(mount, mount.GetRightAscensionAsync, _mountState.RightAscension, cancellationToken);
            var dec = await PollDriverReadAsync(mount, mount.GetDeclinationAsync, _mountState.Declination, cancellationToken);

            // Derive J2000 coords so the sky-map reticle renders in the same frame as catalog
            // stars/objects. Treating the native topocentric (JNOW) read as J2000 is off by ~22'
            // of precession in 2026 -- enough to look like a real pointing error on the map. Mirrors
            // AppSignalHandler.SamplePreviewMountAsync's idle-path conversion (same shared
            // EquatorialFrameConversion helper) so the reticle is identical whether or not a session
            // is running. J2000-native mounts pass the native values through unchanged.
            var (raJ2000, decJ2000) = (double.NaN, double.NaN);
            if (!double.IsNaN(ra) && !double.IsNaN(dec))
            {
                if (mount.EquatorialSystem == EquatorialCoordinateType.J2000)
                {
                    (raJ2000, decJ2000) = (ra, dec);
                }
                else if (mount.EquatorialSystem == EquatorialCoordinateType.Topocentric)
                {
                    _mountStateTransform ??= await mount.TryGetTransformAsync(ResolveSiteConditions(), cancellationToken);
                    if (_mountStateTransform is { } transform)
                    {
                        transform.DateTime = _timeProvider.GetUtcNow().UtcDateTime;
                        if (EquatorialFrameConversion.TopocentricToJ2000(transform, ra, dec) is { } j2000)
                        {
                            (raJ2000, decJ2000) = j2000;
                        }
                        else
                        {
                            // Transform not fully initialised (e.g. built before the site sync) ->
                            // drop it so the next poll rebuilds from the now-valid site. J2000 stays
                            // NaN this tick and the overlay falls back to the native read.
                            _mountStateTransform = null;
                        }
                    }
                }
            }

            var hourAngle = await PollDriverReadAsync(mount, mount.GetHourAngleAsync, _mountState.HourAngle, cancellationToken);
            // Slewing BEFORE tracking: on the SkyWatcher driver IsSlewingAsync is the goto-completion hook
            // that resumes tracking, so read the other way round a poll can see "not slewing, not
            // tracking" for a mount that is fine -- exactly the signature the driver-stop detector below
            // looks for.
            var isSlewing = await PollDriverReadAsync(mount, mount.IsSlewingAsync, _mountState.IsSlewing, cancellationToken);
            var isTracking = await PollDriverReadAsync(mount, mount.IsTrackingAsync, _mountState.IsTracking, cancellationToken);
            // Null on every driver that cannot model its axis (the interface default), carried as NaN like
            // the other unknowns in MountState; the limit falls back to the hour angle for it.
            var primaryAxisAngleDeg = await PollDriverReadAsync(
                mount, async ct => await mount.GetAxisAngleAsync(TelescopeAxis.Primary, ct) ?? double.NaN,
                _mountState.PrimaryAxisAngleDeg, cancellationToken);

            _mountState = new MountState(
                RightAscension: ra,
                Declination: dec,
                HourAngle: hourAngle,
                PierSide: await PollDriverReadAsync(mount, mount.GetSideOfPierAsync, _mountState.PierSide, cancellationToken),
                IsSlewing: isSlewing,
                IsTracking: isTracking,
                RaJ2000: raJ2000,
                DecJ2000: decJ2000,
                Altitude: SiteContext.Create(Configuration.SiteLatitude, Configuration.SiteLongitude, _timeProvider)
                    .AltitudeDegrees(hourAngle, dec),
                PrimaryAxisAngleDeg: primaryAxisAngleDeg);

            // Accept the reported pier side only on the edge where a slew has just finished. A goto is
            // the only thing in ordinary operation that carries a tube across the pier, so that is the
            // one moment the report is certainly right -- on a mount that merely COMPUTES the state it
            // drifts from then on, turning over as the POINTING crosses the meridian while the tube
            // stays put. Holding the landed value is what makes Session.GetSideOfPierAsync canonical.
            if (isSlewing)
            {
                _pierSideMayHaveMoved = true;
            }
            else if (_pierSideMayHaveMoved || _verifiedPointingState is PointingState.Unknown)
            {
                _verifiedPointingState = _mountState.PierSide;
                _pierSideMayHaveMoved = false;
            }

            DetectDriverEnforcedStop(isTracking, isSlewing);
            await EnforceMountLimitsAsync(mount, cancellationToken);
        }
    }

    /// <summary>
    /// P5: a mount that stopped tracking while nothing here asked it to has enforced a limit of its own
    /// (or stalled). Latches <see cref="MountLimitVerdict.DriverEnforcedStop"/> and ends the run the way a
    /// configured limit does -- and, unlike a configured limit, does so whether or not
    /// <see cref="Setup.MountLimits"/> is set, because the driver's limit exists whether or not ours does.
    /// </summary>
    private void DetectDriverEnforcedStop(bool isTracking, bool isSlewing)
    {
        if (isTracking)
        {
            _sawTracking = true;
            _mountStopCommanded = false;
            _untrackedPolls = 0;
            return;
        }

        if (!_sawTracking || isSlewing || _mountStopCommanded || _limitReached)
        {
            _untrackedPolls = 0;
            return;
        }

        if (++_untrackedPolls < DriverStopDebouncePolls)
        {
            return;
        }

        _limitVerdict = MountLimitVerdict.DriverEnforcedStop;
        _limitReached = true;
        _logger.LogWarning("Mount safety limit: {Verdict}", _limitVerdict.Describe());
    }

    /// <summary>
    /// Evaluates this rig's configured mechanical limits against the state just polled, and acts on
    /// the verdict at most once per entry into the limit.
    /// </summary>
    /// <remarks>
    /// <para>Lives on the poll rather than the imaging tick because the poll is what every slew
    /// wait and focus routine already calls -- a limit that only fired between exposures would
    /// watch a mount drive into a pier during a goto and say nothing.</para>
    ///
    /// <para><b>The latch is the whole reason this is not just an <c>if</c>.</b> The poll runs
    /// continuously, so acting per-poll would re-command a park every tick and restart the park slew
    /// forever, never arriving -- GSS's <c>SlewState != SlewType.SlewPark</c> guard, ported as
    /// <see cref="MountLimits"/>' <c>alreadyActed</c>. It downgrades to Warn rather than clearing,
    /// so the user keeps being told they are still in the limit.</para>
    ///
    /// <para>Acting is deliberately best-effort and does NOT gate the run's exit: whether the stop
    /// succeeded or not, the imaging loop is told to finish. A limit we could not act on is a
    /// stronger reason to end the night, not a weaker one.</para>
    /// </remarks>
    private async ValueTask EnforceMountLimitsAsync(IMountDriver mount, CancellationToken cancellationToken)
    {
        // A driver-enforced stop is latched for the rest of the run; a configured limit evaluating Clear
        // afterwards must not overwrite the verdict the run is ending on.
        if (Setup.MountLimits is not { Enabled: true } limits || _limitVerdict.Kind is MountLimitKind.DriverEnforced)
        {
            return;
        }

        // The pointing state is what tells the decider which way the hour angle counts: a rig that has
        // flipped is safe however far west it tracks, and the first cut of this stopped every such rig
        // ~30 min after its flip. The axis angle, where the driver has one, makes even that moot -- it
        // is the mechanical fact the hour angle only estimates. See MountLimits.Evaluate's remarks.
        var verdict = MountLimits.Evaluate(
            _mountState.HourAngle,
            MountLimits.TrustedPointingState(mount.PointingStateSource, _mountState.PierSide),
            _mountState.PrimaryAxisAngleDeg,
            _mountState.Altitude, _mountState.IsTracking, _limitActed, limits);

        if (!verdict.IsBreached)
        {
            if (Volatile.Read(ref _limitActed))
            {
                _logger.LogInformation("Mount is clear of its safety limits again.");
                Volatile.Write(ref _limitActed, false);
            }
            _limitVerdict = verdict;
            return;
        }

        _limitVerdict = verdict;

        if (verdict.IsWarningOnly || verdict.Response is MountLimitResponse.Warn)
        {
            _logger.LogWarning("Mount safety limit: {Verdict}", verdict.Describe());
            return;
        }

        _logger.LogError("Mount safety limit: {Verdict}. Responding with {Response}.", verdict.Describe(), verdict.Response);
        Volatile.Write(ref _limitActed, true);
        _limitReached = true;
        _mountStopCommanded = true;

        // Stop tracking first in BOTH responses. A park is motion across a path nothing has checked,
        // so the axis should not still be driving toward the limit while it is under way -- and if
        // the park then fails, a stopped mount is a better place to have left the rig than a
        // tracking one.
        await ResilientInvokeAsync(
            mount, ct => mount.SetTrackingAsync(false, ct),
            ResilientCallOptions.NonIdempotentAction, cancellationToken);

        if (verdict.Response is MountLimitResponse.Park && mount.CanPark)
        {
            await ResilientInvokeAsync(
                mount, mount.ParkAsync, ResilientCallOptions.NonIdempotentAction, cancellationToken);
        }
        else if (verdict.Response is MountLimitResponse.Park)
        {
            _logger.LogWarning("Mount safety limit asked for a park, but this mount cannot park; tracking was stopped instead.");
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

    /// <summary>
    /// Allocates the per-telescope observable-surface arrays (camera states, last-captured images, viewer
    /// channels, frame metrics + history) so the telemetry properties don't index empty arrays. Called at the
    /// top of every run entry point (<see cref="RunAsync"/>, <see cref="RunFlatsOnlyAsync"/>).
    /// </summary>
    private void AllocateObservableState()
    {
        _cameraStates = new CameraExposureState[Setup.Telescopes.Length];
        _lastCapturedImages = new Image?[Setup.Telescopes.Length];
        _viewerChannels = new Imaging.Channel[]?[Setup.Telescopes.Length];
        _lastFrameMetrics = new FrameMetrics[Setup.Telescopes.Length];
        _frameMetricsHistory = CreateFrameMetricsHistory(Setup.Telescopes.Length);
    }

    /// <summary>
    /// One buffer per OTA, sized to the drift-trend window
    /// (<see cref="SessionConfiguration.FocusDriftSampleSize"/>).
    /// </summary>
    private CircularBuffer<FrameMetrics>[] CreateFrameMetricsHistory(int scopes)
    {
        var history = new CircularBuffer<FrameMetrics>[scopes];
        for (var i = 0; i < scopes; i++)
        {
            history[i] = new CircularBuffer<FrameMetrics>(Math.Max(1, Configuration.FocusDriftSampleSize));
        }
        return history;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _failureReason = null;

        // Claim the equipment for the whole run -- BEFORE the main try, so a conflict is reported as a
        // plain failure rather than running the finally over hardware we never owned. Held across
        // Finalise too: parking the mount and warming the cameras is exactly when a stray disconnect
        // does the most damage.
        using var _equipment = TryAcquireEquipmentForRun("the imaging session");
        if (_equipment is null)
        {
            return;
        }

        try
        {
            AllocateObservableState();
            var active = AdvanceObservation();
            // run initialisation code
            if (active == 0)
            {
                SetPhase(SessionPhase.Initialising);
                if (!await InitialisationAsync(cancellationToken))
                {
                    _logger.LogError("Initialization failed, aborting session.");
                    _failureReason = "Could not get the equipment ready. Check the log for the step that failed.";
                    SetPhase(SessionPhase.Failed);
                    return;
                }
            }
            else if (ActiveObservation is null)
            {
                _logger.LogInformation("Session complete, finished {ObservationCount} observations, finalizing.", _activeObservation);
                SetPhase(SessionPhase.Complete);
                return;
            }

            // Initial device state poll after all devices are connected
            await PollDeviceStatesAsync(cancellationToken);

            // Optional dusk (evening) twilight sky-flat block. Runs at session start -- while the sky is still
            // in twilight, before the wait-for-dark -- and cools to the imaging setpoint first so the flats
            // match the light-frame temperature. Independent of the end-of-session (dawn) hook so both dusk
            // and dawn flats can be captured in one night (insurance against a clouded dawn). Skipped cleanly
            // when the dusk window has already passed (see TakeSkyFlatsAsync).
            if (Configuration.TakeSkyFlatsAtDusk)
            {
                SetPhase(SessionPhase.Cooling);
                await CoolCamerasToSetpointAsync(Configuration.SetpointCCDTemperature, Configuration.CooldownRampInterval, 80, SetupointDirection.Down, cancellationToken).ConfigureAwait(false);

                SetPhase(SessionPhase.Flats);
                await TakeSkyFlatsAsync(TwilightPeriod.Dusk, cancellationToken).ConfigureAwait(false);
            }

            SetPhase(SessionPhase.WaitingForDark);
            await WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(cancellationToken).ConfigureAwait(false);

            SetPhase(SessionPhase.Cooling);
            await CoolCamerasToSetpointAsync(Configuration.SetpointCCDTemperature, Configuration.CooldownRampInterval, 80, SetupointDirection.Down, cancellationToken).ConfigureAwait(false);

            SetPhase(SessionPhase.RoughFocus);
            if (!await InitialRoughFocusAsync(cancellationToken))
            {
                _logger.LogError("Failed to focus cameras (first time), aborting session.");
                _failureReason = "Could not achieve initial focus. Check that the covers are open, the sky is clear, and the focuser is near focus.";
                SetPhase(SessionPhase.Failed);
                return;
            }

            // Cloud gate (C): the zenith rough-focus gauge is an obstruction-free read of transparency.
            // If even the zenith is crushed, the whole sky is clouded; hold and re-gauge before sinking
            // the night into thick cloud. No-op when the sky reads clear or no valid gauge exists.
            await WaitForCloudGateAsync(cancellationToken);

            SetPhase(SessionPhase.AutoFocus);
            if (!await AutoFocusAllTelescopesAsync(cancellationToken))
            {
                _logger.LogWarning("Auto-focus did not converge for all telescopes, proceeding with rough focus.");
            }

            SetPhase(SessionPhase.CalibratingGuider);
            await CalibrateGuiderAsync(cancellationToken).ConfigureAwait(false);

            SetPhase(SessionPhase.Observing);
            await ObservationLoopAsync(cancellationToken).ConfigureAwait(false);

            // Optional end-of-session flat block. Runs on normal completion only (an abort/exception
            // skips straight to Finalise), and BEFORE Finalise warms the cameras so the flats are taken
            // at the imaging setpoint temperature.
            if (Configuration.TakeFlatsOnSessionEnd)
            {
                SetPhase(SessionPhase.Flats);
                try
                {
                    await TakeFlatsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best-effort backstop: a flats failure at the END of a successful night (flaky cover
                    // I/O, capture error) must never flip the session to Failed -- the night's exposures
                    // are unaffected. Contrast init, where a cover that fails to CONNECT fails the session
                    // deliberately (a flip-flat we cannot open leaves the OTA blind). Cancellation still
                    // propagates to the abort path.
                    _logger.LogError(ex, "End-of-session flats failed; continuing to Finalise.");
                }
            }

            SetPhase(SessionPhase.Complete);
        }
        catch (OperationCanceledException)
        {
            SetPhase(SessionPhase.Aborted);
        }
        catch (SessionFailedException sfe)
        {
            // A deliberate abort with a user-facing reason (e.g. a device that failed to connect at init).
            // The message goes to FailureReason verbatim; the technical cause to the log.
            _logger.LogError(sfe.InnerException ?? sfe, "Session failed: {Reason}", sfe.Message);
            _failureReason = sfe.Message;
            SetPhase(SessionPhase.Failed);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception while in main run loop, unrecoverable, aborting session.");
            _failureReason = $"Unexpected error: {e.Message}";
            SetPhase(SessionPhase.Failed);
        }
        finally
        {
            // Remember terminal state so we can restore it after Finalise
            var terminalPhase = _phase;

            // Finalise must complete: park mount, warm cameras, close covers.
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

        public GuideStatsPoller(Session session, Devices.Guider.IGuider guider, ITimeProvider timeProvider, CancellationToken parentToken)
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
                        session.UpdateGuiderState(appState);
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
                                timeProvider.GetUtcNow(), raErr, decErr,
                                gs.LastRaPulseMs ?? 0, gs.LastDecPulseMs ?? 0,
                                isDither, isSettling));
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* ignore transient errors */ }

                    await timeProvider.SleepAsync(TimeSpan.FromSeconds(2), ct);
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
