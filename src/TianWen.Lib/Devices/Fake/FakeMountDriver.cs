using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices.Fake.Disturbance;
using TianWen.Lib.Devices.Fake.Disturbance.Terms;
using static TianWen.Lib.Astrometry.Constants;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// In-memory mount driver for testing and simulation.
/// Tracks RA/Dec state directly without serial communication.
/// Supports configurable error injection: periodic error, polar misalignment drift,
/// mount backlash, and cone error.
/// </summary>
internal sealed class FakeMountDriver(FakeDevice fakeDevice, IServiceProvider serviceProvider) : FakeDeviceDriverBase(fakeDevice, serviceProvider), IMountDriver
{
    // --- Physical constants ---
    private const double DEFAULT_GUIDE_RATE_DEG_PER_SEC = SIDEREAL_RATE * 2.0 / 3.0 / 3600.0;
    private const double ARCSEC_PER_DEGREE = 3600.0;
    private const double ARCSEC_PER_RA_HOUR = 3600.0 * 15.0; // RA-coordinate arcsec per hour
    private const double DEFAULT_SLEW_RATE = 1.5; // degrees per second

    // --- Mount state (guarded by _sem) ---
    private readonly SemaphoreSlim _sem = new SemaphoreSlim(1, 1);
    private double _ra; // hours (0..24) — initialized to LST on first site config
    private double _dec = 90.0; // degrees (-90..90) — GEM park position: celestial pole
    private double _targetRa;
    private double _targetDec;
    private bool _isTracking;
    private volatile bool _isSlewing;
    private TrackingSpeed _trackingSpeed = TrackingSpeed.Sidereal;
    private double _guideRateRA = DEFAULT_GUIDE_RATE_DEG_PER_SEC;
    private double _guideRateDec = DEFAULT_GUIDE_RATE_DEG_PER_SEC;
    private double _raRate; // seconds of RA per sidereal second
    private double _decRate; // arcsec per SI second

    // On-demand disturbances: the believed pointing (_ra/_dec) is the commanded position; the
    // additive disturbances (PE, polar drift, wind, flexure, cable snag, gear) are computed on each
    // coordinate read as a pure function of elapsed-since-epoch, via the shared DisturbanceModel --
    // the same subsystem FakeSkywatcher uses. FakeMountDriver is a believed-only fake (no hidden
    // true-pointing seam), so the disturbances LEAK into the public reads (that is what the guider
    // chases). Built lazily on the first tracked read (after the knobs are set), then cached so the
    // stochastic terms (wind / gear) keep their state across frames. The epoch re-bases when the
    // position is commanded (slew / sync / set-position) or tracking (re)starts -- NOT on guide
    // pulses, so the disturbance keeps accumulating while the guider corrects _ra/_dec underneath it.
    private DisturbanceModel? _disturbances;
    private DateTimeOffset? _disturbanceEpoch;

    // Pulse guide state
    private int _activePulseGuides; // count of active pulse guide timers
    private ITimer? _pulseGuideTimerRA;
    private ITimer? _pulseGuideTimerDec;

    // Slew state
    private ITimer? _slewTimer;

    // --- Site ---
    private double _siteLatitude = 48.2; // Vienna default
    private double _siteLongitude = 16.3;
    private double _siteElevation = 200.0;

    // --- Error injection (all zero = perfect mount) ---

    /// <summary>
    /// Periodic error amplitude in arcseconds (peak).
    /// Typical real-world values: 5–30 arcsec.
    /// </summary>
    public double PeriodicErrorAmplitudeArcsec { get; set; }

    /// <summary>
    /// Worm gear period in seconds. Typical: 480s (8 min) for common mounts.
    /// </summary>
    public double PeriodicErrorPeriodSeconds { get; set; } = 480.0;

    /// <summary>
    /// Polar misalignment drift rate in arcsec/second in declination.
    /// Positive = drifting north. Simplified model (constant rate).
    /// Typical: 0.1–1.0 arcsec/sec for a few arcminutes of misalignment.
    /// </summary>
    public double PolarDriftRateDecArcsecPerSec { get; set; }

    /// <summary>
    /// Polar misalignment RA drift rate in arcsec/second.
    /// This is the RA component of polar misalignment (cos(HA) dependent in reality,
    /// simplified to constant here).
    /// </summary>
    public double PolarDriftRateRaArcsecPerSec { get; set; }

    /// <summary>
    /// Declination backlash in arcseconds. When Dec guide direction reverses,
    /// this many arcseconds of dead zone must be traversed before the mount
    /// actually moves.
    /// </summary>
    public double DecBacklashArcsec { get; set; }

    /// <summary>
    /// Cone error in arcseconds. Adds a systematic RA offset that
    /// flips sign on meridian flip.
    /// </summary>
    public double ConeErrorArcsec { get; set; }

    /// <summary>
    /// Wind gust amplitude in arcseconds (Ornstein-Uhlenbeck stationary std dev).
    /// 0 = disabled. Typical: 1–5 arcsec for moderate wind.
    /// </summary>
    public double WindGustAmplitudeArcsec { get; set; }

    /// <summary>
    /// Wind gust decay time constant (tau) in seconds.
    /// Controls how quickly gusts die out. Typical: 3–10 seconds.
    /// </summary>
    public double WindGustDecayTimeSeconds { get; set; } = 5.0;

    /// <summary>
    /// Random seed for wind gust simulation. Same seed = deterministic output.
    /// </summary>
    public int WindGustSeed { get; set; } = 42;

    /// <summary>
    /// Time (seconds after tracking starts) at which a cable snag impulse occurs.
    /// 0 = disabled.
    /// </summary>
    public double CableSnagTimeSeconds { get; set; }

    /// <summary>
    /// Cable snag impulse amplitude in RA (arcseconds).
    /// </summary>
    public double CableSnagAmplitudeRaArcsec { get; set; }

    /// <summary>
    /// Cable snag impulse amplitude in Dec (arcseconds).
    /// </summary>
    public double CableSnagAmplitudeDecArcsec { get; set; }

    /// <summary>
    /// Flexure drift rate in Dec arcseconds per hour of HA change.
    /// Simulates differential flexure as the telescope tracks across the sky.
    /// 0 = disabled.
    /// </summary>
    public double FlexureDriftRateDecArcsecPerHaHour { get; set; }

    /// <summary>
    /// Gear noise amplitude (1-sigma) in arcseconds. Models gear mesh imperfections
    /// and encoder noise as a time-correlated Ornstein-Uhlenbeck process (the shared
    /// <see cref="GearNoiseTerm"/>). The noise is consistent across reads at the same time
    /// instant and evolves with a decay time of <see cref="GearNoiseDecayTimeSeconds"/>.
    /// Default: 0 (off) -- an unconfigured mount is a perfect mount, matching FakeSkywatcher.
    /// Set &gt; 0 (typical ~0.3 arcsec for mid-range gear trains) to inject jitter.
    /// </summary>
    public double GearNoiseArcsec { get; set; }

    /// <summary>
    /// Gear noise decay time constant (tau) in seconds. Controls the frequency
    /// profile of gear noise. Short tau = high-frequency jitter, long tau = low-frequency drift.
    /// Default: 0.5s (gear mesh noise is relatively fast-changing).
    /// </summary>
    public double GearNoiseDecayTimeSeconds { get; set; } = 0.5;

    /// <summary>
    /// Random seed for gear noise. Same seed = deterministic jitter sequence.
    /// </summary>
    public int GearNoiseSeed { get; set; } = 17;

    // Backlash tracking (applied in PulseGuideAsync, independent of the disturbance model)
    private int _lastDecDirection; // +1 = north, -1 = south, 0 = none
    private double _backlashRemaining; // arcsec of backlash still to be consumed

    /// <summary>
    /// Gets the current tracking error in RA (arcseconds) = the disturbance model's RA pointing
    /// delta (PE + polar drift + wind + cable snag + gear). Positive = east of nominal. Zero when
    /// not tracking.
    /// </summary>
    public async ValueTask<double> GetTrackingErrorRaArcsecAsync(CancellationToken cancellationToken = default)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        return DisturbancePointingDeltaArcsec().RaArcsec;
    }

    /// <summary>
    /// Gets the current tracking error in Dec (arcseconds) = the disturbance model's Dec pointing
    /// delta (polar drift + wind + flexure + cable snag + gear). Positive = north of nominal. Zero
    /// when not tracking.
    /// </summary>
    public async ValueTask<double> GetTrackingErrorDecArcsecAsync(CancellationToken cancellationToken = default)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        return DisturbancePointingDeltaArcsec().DecArcsec;
    }

    // --- IMountDriver implementation ---

    public bool CanSetTracking { get; } = true;
    public bool CanSetSideOfPier { get; } = false;
    public bool CanSetRightAscensionRate { get; } = false;
    public bool CanSetDeclinationRate { get; } = false;
    public bool CanSetGuideRates { get; } = true;
    public bool CanPulseGuide { get; } = true;
    public bool CanPark { get; } = true;
    public bool CanSetPark { get; } = false;
    public bool CanUnpark { get; } = true;
    public bool CanSlew { get; } = true;
    public bool CanSlewAsync { get; } = true;
    public bool CanSync { get; } = true;

    public bool CanMoveAxis(TelescopeAxis axis) => false;

    public IReadOnlyList<AxisRate> AxisRates(TelescopeAxis axis) => [];

    public ValueTask MoveAxisAsync(TelescopeAxis axis, double rate, CancellationToken cancellationToken)
        => throw new NotSupportedException("MoveAxis not supported on FakeMountDriver");

    public IReadOnlyList<TrackingSpeed> TrackingSpeeds { get; } = [TrackingSpeed.Sidereal, TrackingSpeed.Lunar, TrackingSpeed.Solar];

    // Fake mount operates in J2000 directly: its internal _ra/_dec are meant to be
    // catalog-epoch coordinates, not epoch-of-date. Declaring Topocentric would force
    // an apparent-to-J2000 conversion at every read site, which for a park-at-pole
    // simulator introduces a visible ~0.35 deg precession offset on the sky map
    // (see SamplePreviewMountAsync in AppSignalHandler). Real GEM/ASCOM mounts that
    // genuinely track JNow keep reporting Topocentric — that's correct for them.
    public EquatorialCoordinateType EquatorialSystem { get; } = EquatorialCoordinateType.J2000;

    public bool TimeIsSetByUs { get; private set; }

    /// <summary>
    /// Alignment mode this fake reports. Defaults to German equatorial (the mount type that
    /// meridian-flips). Settable so tests can simulate a fork/equatorial (<see cref="AlignmentMode.Polar"/>)
    /// or Alt-Az mount, which track across the meridian without flipping.
    /// </summary>
    internal AlignmentMode Alignment { get; set; } = AlignmentMode.GermanPolar;

    public ValueTask<AlignmentMode> GetAlignmentAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(Alignment);

    public ValueTask<bool> IsTrackingAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_isTracking);

    public async ValueTask SetTrackingAsync(bool tracking, CancellationToken cancellationToken)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        if (tracking && !_isTracking)
        {
            // Disturbances accumulate from when tracking starts (a fresh field), so re-base the
            // epoch and reset the stochastic terms here.
            RebaseDisturbances();
        }
        _isTracking = tracking;
    }

    public ValueTask<TrackingSpeed> GetTrackingSpeedAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_trackingSpeed);

    public ValueTask SetTrackingSpeedAsync(TrackingSpeed value, CancellationToken cancellationToken)
    {
        _trackingSpeed = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> AtHomeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(false);

    private bool _isParked;

    public ValueTask<bool> AtParkAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_isParked);

    public ValueTask ParkAsync(CancellationToken cancellationToken)
    {
        _isTracking = false;
        _isParked = true;
        return ValueTask.CompletedTask;
    }

    public async ValueTask UnparkAsync(CancellationToken cancellationToken)
    {
        _isParked = false;
        // Home position: counterweight-down at pole, HA=6h (standard GEM park)
        _dec = _siteLatitude >= 0 ? 90.0 : -90.0;
        IMountDriver self = this;
        if (await self.TryGetTransformAsync(cancellationToken) is { } transform)
        {
            _ra = ConditionRA(transform.LocalSiderealTime - 6.0);
        }
    }

    // --- Coordinates (computed on-demand with sidereal tracking + error injection) ---

    public async ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        var (raArcsec, _) = DisturbancePointingDeltaArcsec();
        return ConditionRA(_ra + raArcsec / ARCSEC_PER_RA_HOUR);
    }

    public async ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        var (_, decArcsec) = DisturbancePointingDeltaArcsec();
        return Math.Clamp(_dec + decArcsec / ARCSEC_PER_DEGREE, -90, 90);
    }

    // --- Encoder simulation ---
    // Simulates encoder ticks: 360° = EncoderTicksPerRevolution ticks
    internal const int EncoderTicksPerRevolution = 11_520_000; // typical high-res encoder

    // 180 worm teeth (like EQ6), PE period = 11520000/180 = 64000 steps (~8 min at sidereal rate)
    private const int WormTeeth = 180;

    public ValueTask<uint> GetWormPeriodStepsAsync(TelescopeAxis axis, CancellationToken cancellationToken)
        => ValueTask.FromResult(axis is TelescopeAxis.Primary or TelescopeAxis.Seconary
            ? (uint)(EncoderTicksPerRevolution / WormTeeth)
            : 0u);

    public async ValueTask<long?> GetAxisPositionAsync(TelescopeAxis axis, CancellationToken cancellationToken)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        var (raArcsec, decArcsec) = DisturbancePointingDeltaArcsec();
        var reportedRa = ConditionRA(_ra + raArcsec / ARCSEC_PER_RA_HOUR);
        var reportedDec = _dec + decArcsec / ARCSEC_PER_DEGREE;
        return axis switch
        {
            // The RA axis encoder reads the MECHANICAL axis angle, which follows the hour
            // angle (LST - RA): while tracking, RA stays constant but the axis (and worm)
            // physically rotates at sidereal rate. Deriving the encoder from RA (the old
            // behaviour) froze it during tracking -- the worm never turned, so encoder-phase
            // features and any PE keyed to worm rotation were decorrelated from reality.
            TelescopeAxis.Primary => (long)(ConditionRA(LocalSiderealTime() - reportedRa) / 24.0 * EncoderTicksPerRevolution),
            TelescopeAxis.Seconary => (long)((reportedDec + 90.0) / 360.0 * EncoderTicksPerRevolution),
            _ => null
        };
    }

    public async ValueTask<double> GetTargetRightAscensionAsync(CancellationToken cancellationToken)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        return _targetRa;
    }

    public async ValueTask<double> GetTargetDeclinationAsync(CancellationToken cancellationToken)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        return _targetDec;
    }

    public ValueTask<double> GetSiderealTimeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(LocalSiderealTime());

    private double LocalSiderealTime()
    {
        var transform = new Transform(TimeProvider)
        {
            SiteLatitude = _siteLatitude,
            SiteLongitude = _siteLongitude,
            SiteElevation = _siteElevation
        };
        transform.RefreshDateTimeFromTimeProvider();
        return transform.LocalSiderealTime;
    }

    public async ValueTask<double> GetHourAngleAsync(CancellationToken cancellationToken)
    {
        var lst = await GetSiderealTimeAsync(cancellationToken);
        var ra = await GetRightAscensionAsync(cancellationToken);
        var ha = lst - ra;
        while (ha > 12) ha -= 24;
        while (ha < -12) ha += 24;
        return ha;
    }

    // --- Guide rates ---

    public ValueTask<double> GetGuideRateRightAscensionAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_guideRateRA);

    public ValueTask SetGuideRateRightAscensionAsync(double value, CancellationToken cancellationToken)
    {
        _guideRateRA = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetGuideRateDeclinationAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_guideRateDec);

    public ValueTask SetGuideRateDeclinationAsync(double value, CancellationToken cancellationToken)
    {
        _guideRateDec = value;
        return ValueTask.CompletedTask;
    }

    // --- Tracking rates ---

    public ValueTask<double> GetRightAscensionRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_raRate);

    public ValueTask SetRightAscensionRateAsync(double value, CancellationToken cancellationToken)
    {
        _raRate = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetDeclinationRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_decRate);

    public ValueTask SetDeclinationRateAsync(double value, CancellationToken cancellationToken)
    {
        _decRate = value;
        return ValueTask.CompletedTask;
    }

    // --- Pulse guiding ---

    public async ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Mount is not connected");
        }

        var durationSec = duration.TotalSeconds;

        using (await _sem.AcquireLockAsync(cancellationToken))
        {
            // The pulse adjusts the believed/commanded position (_ra/_dec) directly. Disturbances
            // are added on read off a stable epoch (NOT folded here), so the pulse correction and
            // the ongoing disturbance compose exactly as on a real rig: the guider drives _ra/_dec
            // to keep (believed + disturbance) on the lock position.
            switch (direction)
            {
                case GuideDirection.East:
                case GuideDirection.West:
                {
                    var sign = direction == GuideDirection.West ? 1.0 : -1.0;
                    var deltaRaDeg = _guideRateRA * durationSec * sign;
                    var deltaRaHours = deltaRaDeg * DEG2HOURS;
                    _ra = ConditionRA(_ra + deltaRaHours);
                    break;
                }
                case GuideDirection.North:
                case GuideDirection.South:
                {
                    var sign = direction == GuideDirection.North ? 1.0 : -1.0;
                    var newDirection = sign > 0 ? 1 : -1;

                    // Backlash: if direction reversed, consume backlash first
                    var effectiveArcsec = _guideRateDec * ARCSEC_PER_DEGREE * durationSec;

                    if (_lastDecDirection != 0 && newDirection != _lastDecDirection)
                    {
                        // Direction reversal — engage backlash
                        _backlashRemaining = DecBacklashArcsec;
                    }

                    if (_backlashRemaining > 0)
                    {
                        var consumed = Math.Min(_backlashRemaining, effectiveArcsec);
                        _backlashRemaining -= consumed;
                        effectiveArcsec -= consumed;
                    }

                    _lastDecDirection = newDirection;
                    _dec = Math.Clamp(_dec + sign * effectiveArcsec / ARCSEC_PER_DEGREE, -90, 90);
                    break;
                }
            }
        }

        // Simulate pulse guide duration via timer (outside lock)
        Interlocked.Increment(ref _activePulseGuides);

        var isRA = direction is GuideDirection.East or GuideDirection.West;
        ref var timerRef = ref isRA ? ref _pulseGuideTimerRA : ref _pulseGuideTimerDec;
        var timer = TimeProvider.CreateTimer(
            _ => Interlocked.Decrement(ref _activePulseGuides),
            null,
            duration,
            Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref timerRef, timer)?.Dispose();
    }

    public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(Volatile.Read(ref _activePulseGuides) > 0);

    // --- Slewing ---

    public ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_isSlewing);

    public ValueTask AbortSlewAsync(CancellationToken cancellationToken)
    {
        _isSlewing = false;
        Interlocked.Exchange(ref _slewTimer, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    public async ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Mount is not connected");
        }

        using (await _sem.AcquireLockAsync(cancellationToken))
        {
            _targetRa = ra;
            _targetDec = dec;
            _isSlewing = true;
            _isTracking = true;
        }

        // Start slew timer (outside lock)
        var period = TimeSpan.FromMilliseconds(100);
        var slewTimer = TimeProvider.CreateTimer(SlewTimerCallback, null, period, period);
        Interlocked.Exchange(ref _slewTimer, slewTimer)?.Dispose();
    }

    private void SlewTimerCallback(object? state)
    {
        if (!_isSlewing) return;

        // Try-acquire: skip tick if lock is held (non-blocking)
        if (!_sem.Wait(0)) return;
        try
        {
            var slewRateDegreesPerTick = DEFAULT_SLEW_RATE * 0.1; // 100ms ticks
            var slewRateHoursPerTick = slewRateDegreesPerTick * DEG2HOURS;

            var raDiff = _targetRa - _ra;
            if (raDiff > 12) raDiff -= 24;
            if (raDiff < -12) raDiff += 24;

            var decDiff = _targetDec - _dec;

            var raStep = Math.Sign(raDiff) * Math.Min(Math.Abs(raDiff), slewRateHoursPerTick);
            var decStep = Math.Sign(decDiff) * Math.Min(Math.Abs(decDiff), slewRateDegreesPerTick);

            _ra = ConditionRA(_ra + raStep);
            _dec += decStep;

            var raReached = Math.Abs(raDiff) < 0.0001;
            var decReached = Math.Abs(decDiff) < 0.0001;

            if (raReached) _ra = _targetRa;
            if (decReached) _dec = _targetDec;

            if (raReached && decReached)
            {
                _isSlewing = false;
                ResetTrackingErrors();
            }
        }
        finally
        {
            _sem.Release();
        }

        if (!_isSlewing)
        {
            Interlocked.Exchange(ref _slewTimer, null)?.Dispose();
        }
    }

    public async ValueTask SyncRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        _ra = ra;
        _dec = dec;
        ResetTrackingErrors();
    }

    // --- Pier side ---

    public async ValueTask<PointingState> GetSideOfPierAsync(CancellationToken cancellationToken)
    {
        var ha = await GetHourAngleAsync(cancellationToken);
        return ha >= 0 ? PointingState.Normal : PointingState.ThroughThePole;
    }

    public ValueTask SetSideOfPierAsync(PointingState pointingState, CancellationToken cancellationToken)
        => throw new NotSupportedException("Cannot set side of pier on FakeMountDriver");

    public async ValueTask<PointingState> DestinationSideOfPierAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        var lst = await GetSiderealTimeAsync(cancellationToken);
        var ha = lst - ra;
        while (ha > 12) ha -= 24;
        while (ha < -12) ha += 24;
        return ha >= 0 ? PointingState.Normal : PointingState.ThroughThePole;
    }

    // --- Site ---

    public ValueTask<double> GetSiteLatitudeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_siteLatitude);

    public async ValueTask SetSiteLatitudeAsync(double latitude, CancellationToken cancellationToken)
    {
        _siteLatitude = latitude;
        // Home position: counterweight-down at pole, HA=6h (standard GEM park)
        _dec = latitude >= 0 ? 90.0 : -90.0;
        IMountDriver self = this;
        if (await self.TryGetTransformAsync(cancellationToken) is { } transform)
        {
            _ra = ConditionRA(transform.LocalSiderealTime - 6.0);
        }
    }

    public ValueTask<double> GetSiteLongitudeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_siteLongitude);

    public ValueTask SetSiteLongitudeAsync(double longitude, CancellationToken cancellationToken)
    {
        _siteLongitude = longitude;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetSiteElevationAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_siteElevation);

    public ValueTask SetSiteElevationAsync(double elevation, CancellationToken cancellationToken)
    {
        _siteElevation = elevation;
        return ValueTask.CompletedTask;
    }

    // --- Time ---

    public ValueTask<DateTime?> TryGetUTCDateFromMountAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<DateTime?>(TimeProvider.GetUtcNow().UtcDateTime);

    public ValueTask SetUTCDateAsync(DateTime dateTime, CancellationToken cancellationToken)
    {
        TimeIsSetByUs = true;
        return ValueTask.CompletedTask;
    }

    // --- Tracking with error injection ---

    /// <summary>
    /// Sets the mount to point at a specific RA/Dec and resets all accumulated errors.
    /// Call this after construction to place the mount at the desired starting position.
    /// </summary>
    public async ValueTask SetPositionAsync(double ra, double dec, CancellationToken cancellationToken = default)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        _ra = ConditionRA(ra);
        _dec = Math.Clamp(dec, -90, 90);
        _targetRa = _ra;
        _targetDec = _dec;
        ResetTrackingErrors();
    }

    private void ResetTrackingErrors()
    {
        _backlashRemaining = 0;
        _lastDecDirection = 0;
        RebaseDisturbances();
    }

    /// <summary>
    /// Re-bases the disturbance epoch to now and resets the stochastic terms, so the additive
    /// disturbances accumulate afresh from the current commanded position. Called when the position
    /// is commanded (slew / sync / set-position) or tracking (re)starts. Must be called within _sem.
    /// </summary>
    private void RebaseDisturbances()
    {
        _disturbances?.Reset();
        _disturbanceEpoch = TimeProvider.GetUtcNow();
    }

    /// <summary>
    /// The current additive disturbance in native arcsec (RA-coordinate arcsec, Dec arcsec), as a
    /// pure function of elapsed-since-epoch via the shared <see cref="DisturbanceModel"/> -- the same
    /// subsystem FakeSkywatcher uses. Returns (0, 0) when not tracking, mid-slew, or before tracking
    /// has started. The model is built lazily on the first tracked read (after the knobs are set) and
    /// cached so the stochastic terms keep their state. Must be called within _sem.
    /// </summary>
    private (double RaArcsec, double DecArcsec) DisturbancePointingDeltaArcsec()
    {
        if (!_isTracking || _isSlewing || _disturbanceEpoch is not { } epoch)
        {
            return (0.0, 0.0);
        }
        var elapsedSeconds = (TimeProvider.GetUtcNow() - epoch).TotalSeconds;
        if (elapsedSeconds <= 0.0)
        {
            return (0.0, 0.0);
        }
        _disturbances ??= BuildDisturbanceModel();
        // No worm encoder is wired on this simple fake, so the PE term falls back to its wall-clock
        // sine (worm phase = NaN). NOTE: NO sidereal term -- a tracking mount HOLDS the commanded
        // RA/Dec; it is the RA-axis ENCODER (HA = LST - RA, see GetAxisPositionAsync) that advances
        // at the sidereal rate. The disturbances LEAK into the public reads here (believed-only fake),
        // which is what the guider chases.
        return _disturbances.PointingDelta(new DisturbanceContext(elapsedSeconds, double.NaN));
    }

    private DisturbanceModel BuildDisturbanceModel() => new(new IDisturbanceTerm[]
    {
        // The knob is the PEAK amplitude; the term takes peak-to-peak.
        new PeriodicErrorTerm(2.0 * PeriodicErrorAmplitudeArcsec, PeriodicErrorPeriodSeconds),
        // Polar misalignment as a simplified constant-rate drift in both axes (the realistic
        // HA-dependent tilt is modelled properly by FakeSkywatcher's believed->true transform).
        new LinearDriftTerm(PolarDriftRateRaArcsecPerSec, PolarDriftRateDecArcsecPerSec),
        new WindGustTerm(WindGustAmplitudeArcsec, WindGustDecayTimeSeconds, WindGustSeed),
        new CableSnagTerm(CableSnagTimeSeconds, CableSnagAmplitudeRaArcsec, CableSnagAmplitudeDecArcsec),
        new FlexureTerm(FlexureDriftRateDecArcsecPerHaHour),
        new GearNoiseTerm(GearNoiseArcsec, GearNoiseDecayTimeSeconds, GearNoiseSeed),
    });

    private static double ConditionRA(double ra)
    {
        while (ra >= 24) ra -= 24;
        while (ra < 0) ra += 24;
        return ra;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Interlocked.Exchange(ref _slewTimer, null)?.Dispose();
        Interlocked.Exchange(ref _pulseGuideTimerRA, null)?.Dispose();
        Interlocked.Exchange(ref _pulseGuideTimerDec, null)?.Dispose();
        _sem.Dispose();
    }
}
