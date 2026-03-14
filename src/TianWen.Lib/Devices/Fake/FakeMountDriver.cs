using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using static TianWen.Lib.Astrometry.Constants;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// In-memory mount driver for testing and simulation.
/// Tracks RA/Dec state directly without serial communication.
/// Supports configurable error injection: periodic error, polar misalignment drift,
/// mount backlash, and cone error.
/// </summary>
internal sealed class FakeMountDriver(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), IMountDriver
{
    // --- Physical constants ---
    private const double SIDEREAL_RATE_HOURS_PER_SECOND = 24.0 / 86164.0905;
    private const double DEFAULT_GUIDE_RATE_DEG_PER_SEC = SIDEREAL_RATE * 2.0 / 3.0 / 3600.0;
    private const double ARCSEC_PER_DEGREE = 3600.0;
    private const double DEFAULT_SLEW_RATE = 1.5; // degrees per second

    // --- Mount state (guarded by _sem) ---
    private readonly SemaphoreSlim _sem = new SemaphoreSlim(1, 1);
    private double _ra; // hours (0..24) — base RA without sidereal tracking
    private double _dec; // degrees (-90..90)
    private double _targetRa;
    private double _targetDec;
    private bool _isTracking;
    private volatile bool _isSlewing;
    private TrackingSpeed _trackingSpeed = TrackingSpeed.Sidereal;
    private double _guideRateRA = DEFAULT_GUIDE_RATE_DEG_PER_SEC;
    private double _guideRateDec = DEFAULT_GUIDE_RATE_DEG_PER_SEC;
    private double _raRate; // seconds of RA per sidereal second
    private double _decRate; // arcsec per SI second

    // On-demand tracking: we store the timestamp when tracking was checkpointed
    // and compute the sidereal advance + errors on each coordinate read.
    private long _trackingCheckpointTicks;
    private double _accumulatedRaHours; // tracking RA accumulated since last checkpoint
    private double _accumulatedDecDegrees; // tracking Dec drift accumulated since last checkpoint

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

    // Backlash tracking
    private int _lastDecDirection; // +1 = north, -1 = south, 0 = none
    private double _backlashRemaining; // arcsec of backlash still to be consumed

    // --- Accumulated tracking error from PE and polar drift ---
    // These accumulate over time and represent the "true" position error
    // that the guider must correct.
    private double _accumulatedPeRaArcsec; // accumulated PE in RA
    private double _accumulatedPolarDriftDecArcsec; // accumulated polar drift in Dec
    private double _accumulatedPolarDriftRaArcsec; // accumulated polar drift in RA

    /// <summary>
    /// Gets the current accumulated tracking error in RA (arcseconds).
    /// Includes periodic error and polar drift RA component.
    /// Positive = east of nominal.
    /// </summary>
    public async ValueTask<double> GetTrackingErrorRaArcsecAsync(CancellationToken cancellationToken = default)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        UpdateTrackingState();
        return _accumulatedPeRaArcsec + _accumulatedPolarDriftRaArcsec;
    }

    /// <summary>
    /// Gets the current accumulated tracking error in Dec (arcseconds).
    /// Includes polar drift Dec component.
    /// Positive = north of nominal.
    /// </summary>
    public async ValueTask<double> GetTrackingErrorDecArcsecAsync(CancellationToken cancellationToken = default)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        UpdateTrackingState();
        return _accumulatedPolarDriftDecArcsec;
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

    public EquatorialCoordinateType EquatorialSystem { get; } = EquatorialCoordinateType.Topocentric;

    public bool TimeIsSetByUs { get; private set; }

    public ValueTask<AlignmentMode> GetAlignmentAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(AlignmentMode.GermanPolar);

    public ValueTask<bool> IsTrackingAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_isTracking);

    public async ValueTask SetTrackingAsync(bool tracking, CancellationToken cancellationToken)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        if (tracking && !_isTracking)
        {
            Checkpoint();
        }
        else if (!tracking && _isTracking)
        {
            Checkpoint();
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

    public ValueTask<bool> AtParkAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(false);

    public ValueTask ParkAsync(CancellationToken cancellationToken)
    {
        _isTracking = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask UnparkAsync(CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    // --- Coordinates (computed on-demand with sidereal tracking + error injection) ---

    public async ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        UpdateTrackingState();
        return ConditionRA(_ra + _accumulatedRaHours);
    }

    public async ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken)
    {
        using var @lock = await _sem.AcquireLockAsync(cancellationToken);
        UpdateTrackingState();
        return Math.Clamp(_dec + _accumulatedDecDegrees, -90, 90);
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
    {
        var transform = new Transform(External.TimeProvider)
        {
            SiteLatitude = _siteLatitude,
            SiteLongitude = _siteLongitude,
            SiteElevation = _siteElevation
        };
        transform.RefreshDateTimeFromTimeProvider();
        return ValueTask.FromResult(transform.LocalSiderealTime);
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
            // Checkpoint tracking state before applying the pulse,
            // so the pulse applies on top of the current (drifted) position.
            Checkpoint();
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
        var timer = External.TimeProvider.CreateTimer(
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
        var slewTimer = External.TimeProvider.CreateTimer(SlewTimerCallback, null, period, period);
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

    public ValueTask SetSiteLatitudeAsync(double latitude, CancellationToken cancellationToken)
    {
        _siteLatitude = latitude;
        return ValueTask.CompletedTask;
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
        => ValueTask.FromResult<DateTime?>(External.TimeProvider.GetUtcNow().UtcDateTime);

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
        _accumulatedPeRaArcsec = 0;
        _accumulatedPolarDriftDecArcsec = 0;
        _accumulatedPolarDriftRaArcsec = 0;
        _backlashRemaining = 0;
        _lastDecDirection = 0;
        _accumulatedRaHours = 0;
        _accumulatedDecDegrees = 0;
        _trackingCheckpointTicks = External.TimeProvider.GetTimestamp();
    }

    /// <summary>
    /// Folds accumulated tracking (sidereal, PE, drift) into base coordinates,
    /// then resets the tracking accumulator. Must be called within the _sem.
    /// </summary>
    private void Checkpoint()
    {
        UpdateTrackingState();
        _ra = ConditionRA(_ra + _accumulatedRaHours);
        _dec = Math.Clamp(_dec + _accumulatedDecDegrees, -90, 90);
        _accumulatedRaHours = 0;
        _accumulatedDecDegrees = 0;
        _accumulatedPeRaArcsec = 0;
        _accumulatedPolarDriftDecArcsec = 0;
        _accumulatedPolarDriftRaArcsec = 0;
        _trackingCheckpointTicks = External.TimeProvider.GetTimestamp();
    }

    /// <summary>
    /// Computes the current tracking state on-demand. Called by coordinate getters.
    /// Updates accumulated RA/Dec based on elapsed time since last checkpoint.
    /// Must be called within the _sem.
    /// </summary>
    private void UpdateTrackingState()
    {
        if (!_isTracking || _isSlewing) return;

        var currentTicks = External.TimeProvider.GetTimestamp();
        var elapsedTicks = currentTicks - _trackingCheckpointTicks;
        var elapsedSeconds = (double)elapsedTicks / External.TimeProvider.TimestampFrequency;

        if (elapsedSeconds <= 0) return;

        // 1. Sidereal tracking: RA advances to compensate for Earth rotation
        _accumulatedRaHours = SIDEREAL_RATE_HOURS_PER_SECOND * elapsedSeconds;

        // 2. Periodic error: sinusoidal RA error
        if (PeriodicErrorAmplitudeArcsec > 0 && PeriodicErrorPeriodSeconds > 0)
        {
            _accumulatedPeRaArcsec = PeriodicErrorAmplitudeArcsec
                * Math.Sin(2.0 * Math.PI * elapsedSeconds / PeriodicErrorPeriodSeconds);
            _accumulatedRaHours += _accumulatedPeRaArcsec / (ARCSEC_PER_DEGREE * HOURS2DEG);
        }

        // 3. Polar misalignment drift in Dec
        if (PolarDriftRateDecArcsecPerSec != 0)
        {
            _accumulatedPolarDriftDecArcsec = PolarDriftRateDecArcsecPerSec * elapsedSeconds;
            _accumulatedDecDegrees = _accumulatedPolarDriftDecArcsec / ARCSEC_PER_DEGREE;
        }

        // 4. Polar misalignment drift in RA
        if (PolarDriftRateRaArcsecPerSec != 0)
        {
            _accumulatedPolarDriftRaArcsec = PolarDriftRateRaArcsecPerSec * elapsedSeconds;
            _accumulatedRaHours += _accumulatedPolarDriftRaArcsec / (ARCSEC_PER_DEGREE * HOURS2DEG);
        }
    }

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
