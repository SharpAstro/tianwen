using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Connections;
using static TianWen.Lib.Astrometry.Constants;

namespace TianWen.Lib.Devices.Skywatcher;

internal record struct SkywatcherDeviceInfo(ISerialConnection SerialDevice);

/// <summary>
/// Abstract base driver for Skywatcher motor controller mounts (EQ6, HEQ5, AzEQ6, EQ6-R, AzGTi, etc.).
/// Two-axis GEM using Skywatcher motor controller protocol over serial (9600/115200 baud) or WiFi (UDP 11880).
/// </summary>
internal abstract class SkywatcherMountDriverBase<TDevice>(TDevice device, IServiceProvider serviceProvider)
    : DeviceDriverBase<TDevice, SkywatcherDeviceInfo>(device, serviceProvider), IMountDriver
    where TDevice : DeviceBase
{
    private static readonly Encoding _encoding = Encoding.ASCII;
    private static readonly ReadOnlyMemory<byte> CrTerminator = "\r"u8.ToArray();

    // Mount hardware parameters (populated during InitDeviceAsync)
    private uint _cprRa;
    private uint _cprDec;
    private uint _tmrFreq;
    private uint _highSpeedRatio = 16;
    private uint _wormPeriodRa; // steps per worm revolution (from :s command), 0 if unknown
    private uint _wormPeriodDec;
    private SkywatcherFirmwareInfo _firmwareInfo;
    private SkywatcherCapabilities _capabilities;
    private bool _supportsAdvancedCommands;

    // Axis state
    private int _posRa; // encoder steps relative to home (0x800000)
    private int _posDec;
    private volatile bool _isSlewingRa;
    private volatile bool _isSlewingDec;
    private bool _isParked;

    // Number of guide pulses currently executing (Interlocked inc/dec around StartPulseGuideAsync).
    // A pulse runs the axis "running, not tracking" -- the same wire signature as a slew -- so
    // IsSlewingAsync masks axis motion while this is > 0 and IsPulseGuidingAsync reports it
    // instead. This mirrors the ASCOM contract: Slewing is False during a PulseGuide; the
    // separate IsPulseGuiding property carries pulse-guide motion.
    private int _pulseGuideInFlight;

    /// <summary>
    /// Most recent RA encoder reading (steps from home), refreshed by
    /// <see cref="GetRightAscensionAsync"/> via the protocol's <c>j 1</c>
    /// query. Subclasses (notably <c>FakeSkywatcherMountDriver</c> for
    /// polar-misalignment simulation) read this to recover the pure
    /// encoder angle without the LST-drift contamination that
    /// <c>StepsToRa</c> introduces.
    /// </summary>
    protected int PosRa => _posRa;

    /// <summary>RA-axis counts per revolution (steps for 360deg). Zero
    /// before <c>InitDeviceAsync</c> queries the controller.</summary>
    protected uint CprRa => _cprRa;

    /// <summary>Test seam: the live serial connection, so wire-level tests can
    /// reach the fake's command transcript. Null before connect.</summary>
    internal ISerialConnection? SerialConnection => _deviceInfo.SerialDevice;

    // Guide state
    private SkywatcherGuideRate _guideRate = SkywatcherGuideRate.Sidereal0_5;

    /// <summary>
    /// Opt-in (<c>?decPulseGoto=true</c> on the mount URI, advanced setting): Dec pulses
    /// run as relative low-speed micro-GOTOs instead of rate-for-duration. The step count
    /// is exact and the axis settles as fast as the goto ramp allows, instead of holding
    /// f x sidereal for the full pulse duration (GSServer's DecPulseGoTo mode).
    /// </summary>
    private readonly bool _decPulseGoTo =
        bool.TryParse(device.Query.QueryValue(DeviceQueryKey.DecPulseGoTo), out var decPulseGoTo) && decPulseGoTo;

    /// <summary>
    /// Mount alignment mode (<c>?alignment=GermanPolar|Polar|AltAz</c>; default German equatorial).
    /// This is a USER setting, mirroring GSServer: the same AZ/EQ hardware (e.g. a SkyWatcher AZ-GTi
    /// on a wedge vs flat) reports the identical motor-controller model code, so the protocol cannot
    /// tell which way it is mounted: the SynScan app asks at connect; we make it configurable.
    /// <para>
    /// German/Polar drive the equatorial HA/Dec encoder-step transforms in this class. <b>Alt-az is
    /// REPORTED</b> (so the session skips meridian-flip + pier-side logic) but coordinate slews,
    /// sidereal tracking, and RA/Dec sync are <b>REFUSED</b>: the step transforms here are equatorial,
    /// so honouring an alt-az target would silently point the mount wrong. Full alt-az support is
    /// scoped in <c>docs/plans/altaz-mount-support.md</c>.
    /// </para>
    /// </summary>
    private readonly AlignmentMode _alignmentMode =
        Enum.TryParse<AlignmentMode>(device.Query.QueryValue(DeviceQueryKey.Alignment), ignoreCase: true, out var alignment)
            ? alignment
            : AlignmentMode.GermanPolar;

    /// <summary>
    /// Fails loudly for a coordinate operation we cannot yet perform in a non-equatorial alignment.
    /// The SkyWatcher transforms in this driver are equatorial (HA/Dec -&gt; encoder steps); an RA/Dec
    /// slew, sidereal tracking, or sync issued in alt-az mode would point the mount wrong, so we throw
    /// rather than execute it silently. See <c>docs/plans/altaz-mount-support.md</c>.
    /// </summary>
    private void EnsureEquatorialAlignment(string operation)
    {
        if (_alignmentMode == AlignmentMode.AltAz)
        {
            throw new NotSupportedException(
                $"{operation} is not supported in alt-azimuth alignment mode. The TianWen SkyWatcher driver " +
                "drives equatorial mounts only; put the mount on an equatorial wedge and set alignment=GermanPolar. " +
                "Full alt-az support is tracked in docs/plans/altaz-mount-support.md.");
        }
    }

    // Site
    private double _siteLatitude = double.NaN;
    private double _siteLongitude = double.NaN;
    private double _siteElevation = double.NaN;
    private bool _warnedHemisphereUnknown;

    /// <summary>
    /// Southern-hemisphere flag, driven by site latitude. Sets dir bit 1 on every
    /// :G motion-mode command AND mirrors the steps↔sky conversions: below the
    /// equator the RA worm physically turns the opposite way for sidereal tracking
    /// (GSServer: <c>SetTracking</c> passes the negated rate for EqS and
    /// <c>Axes.AxesAppToMount</c> mirrors the axis, <c>a[0] = 180 - a[0]</c>), so
    /// the wire direction and the conversion must flip together. NaN latitude
    /// (site not pushed yet) is treated as northern with a one-shot warning.
    /// </summary>
    private bool IsSouthernHemisphere
    {
        get
        {
            if (double.IsNaN(_siteLatitude))
            {
                if (!_warnedHemisphereUnknown)
                {
                    _warnedHemisphereUnknown = true;
                    Logger.LogWarning("Site latitude not set; assuming northern hemisphere for Skywatcher motion-mode direction");
                }
                return false;
            }
            return _siteLatitude < 0;
        }
    }

    // Snap port
    private volatile bool _snapActive;
    internal bool IsSnapActive => _snapActive;

    // EQMOD-style iterative-goto refinement state (see IsSlewingAsync). The RA target
    // steps of a goto encode the hour angle at COMMAND time, so a long slew lands late
    // by the slew duration of sidereal motion (~9' for a multi-hour swing). EQMOD closes
    // this by re-issuing short refinement gotos until the residual is inside tolerance;
    // we mirror that so a slew genuinely ARRIVES at the commanded sky position and a
    // J2000 slew -> J2000 readback round-trips. NaN RA = no goto pending.
    private double _gotoTargetRa = double.NaN;
    private double _gotoTargetDec;
    // The axis solution the goto was commanded with (see SkyToSteps). Decided ONCE in
    // BeginSlewRaDecAsync and reused by every refinement pass: a target just east of the meridian
    // has crossed it by the time the axes stop, and re-deciding per pass would flip the mount in the
    // middle of its own goto.
    private PointingState _gotoPointingState = PointingState.Normal;
    private int _gotoRefineAttempts;
    private int _gotoRefineInFlight; // Interlocked gate: concurrent IsSlewing pollers must not double-issue the refinement goto

    /// <summary>Residual pointing error above which a completed goto is refined with another pass.</summary>
    private const double GotoRefineToleranceArcsec = 30.0;

    /// <summary>Refinement pass cap; each pass shrinks the residual by the ratio of pass-to-initial slew duration.</summary>
    private const int MaxGotoRefineAttempts = 2;

    #region Capabilities

    public bool CanSetTracking => true;
    public bool CanSetSideOfPier => false;
    public bool CanPulseGuide => true;

    /// <summary>
    /// True: RA and Dec are separate motors addressed as separate axes, and the driver already
    /// counts pulses rather than flagging them (<c>_pulseGuideInFlight</c> is an
    /// <see cref="Interlocked"/> counter precisely so an overlapping pair clears only when BOTH
    /// finish). An RA pulse touches axis 1 and a Dec pulse axis 2; individual commands serialise on
    /// the port lock, which orders them without serialising the two HOLDS.
    /// </summary>
    public bool CanPulseGuideSimultaneously => true;
    public bool CanSetRightAscensionRate => false;
    public bool CanSetDeclinationRate => false;
    public bool CanSetGuideRates => true;
    public bool CanPark => true;
    public bool CanSetPark => false;
    public bool CanUnpark => true;
    public bool CanSlew => true;
    public bool CanSlewAsync => true;
    public bool CanSync => true;
    public bool CanCameraSnap => true;

    public bool CanMoveAxis(TelescopeAxis axis) => axis is TelescopeAxis.Primary or TelescopeAxis.Seconary;

    private static readonly AxisRate[] _axisRates =
    [
        new AxisRate(SIDEREAL_RATE / 3600.0 * 0.125), // 0.125x sidereal (guide)
        new AxisRate(SIDEREAL_RATE / 3600.0),           // 1x sidereal
        new AxisRate(SIDEREAL_RATE / 3600.0 * 8),       // 8x
        new AxisRate(SIDEREAL_RATE / 3600.0 * 16),      // 16x
        new AxisRate(SIDEREAL_RATE / 3600.0 * 32),      // 32x (slew)
        new AxisRate(3.0),                               // ~720x (fast slew)
    ];

    public IReadOnlyList<AxisRate> AxisRates(TelescopeAxis axis) => axis is TelescopeAxis.Primary or TelescopeAxis.Seconary
        ? _axisRates
        : [];

    #endregion

    #region Tracking

    public IReadOnlyList<TrackingSpeed> TrackingSpeeds => [TrackingSpeed.Sidereal, TrackingSpeed.Lunar, TrackingSpeed.Solar];

    public EquatorialCoordinateType EquatorialSystem => EquatorialCoordinateType.Topocentric;

    public ValueTask<AlignmentMode> GetAlignmentAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_alignmentMode);

    public ValueTask<TrackingSpeed> GetTrackingSpeedAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(TrackingSpeed.Sidereal); // Skywatcher only supports sidereal tracking natively

    public async ValueTask SetTrackingSpeedAsync(TrackingSpeed value, CancellationToken cancellationToken)
    {
        if (value != TrackingSpeed.Sidereal)
        {
            throw new ArgumentException($"Tracking speed {value} is not natively supported; only Sidereal is available", nameof(value));
        }
        await SetTrackingAsync(true, cancellationToken);
    }

    public async ValueTask<bool> IsTrackingAsync(CancellationToken cancellationToken)
    {
        // Query RA axis status
        var status = await QueryAxisStatusAsync('1', cancellationToken);
        return status.IsRunning && status.IsTracking;
    }

    public async ValueTask SetTrackingAsync(bool tracking, CancellationToken cancellationToken)
    {
        if (tracking)
        {
            // Sidereal single-axis tracking is equatorial-only; alt-az needs a dual-axis predictor.
            EnsureEquatorialAlignment("Tracking");

            if (_cprRa == 0 || _tmrFreq == 0)
            {
                return;
            }

            // Tracking runs the RA axis as a low-speed slew in the hemisphere's sidereal
            // direction: forward in the north, reverse in the south (GSS feeds EqS the
            // negated rate).
            var south = IsSouthernHemisphere;
            var siderealDegPerSec = SIDEREAL_RATE / 3600.0;
            var t1 = SkywatcherProtocol.ComputeT1Preset(_tmrFreq, _cprRa, siderealDegPerSec, false, _highSpeedRatio);

            var status = await QueryAxisStatusAsync('1', cancellationToken);
            if (status.IsRunning && status.IsTracking && status.IsForward == !south)
            {
                // Already running at a constant-speed rate in the tracking direction
                // (e.g. firmware auto-resumed tracking after a GOTO): change only the
                // step period: GSS AxisSlew's rateChangeOnly path. Re-sending :G here
                // would be rejected by real firmware (!2 motor not stopped).
                await SendCommandAsync('I', '1', SkywatcherProtocol.EncodeUInt24(t1), cancellationToken);
            }
            else
            {
                if (status.IsRunning)
                {
                    await StopAxisAndWaitAsync('1', cancellationToken);
                }
                await SendCommandAsync('G', '1', SkywatcherProtocol.EncodeMotionMode(SkywatcherMotionFunc.LowSpeedSlew, forward: !south, south), cancellationToken);
                await SendCommandAsync('I', '1', SkywatcherProtocol.EncodeUInt24(t1), cancellationToken);
                await SendCommandAsync('J', '1', null, cancellationToken);
            }
        }
        else
        {
            await SendCommandAsync('K', '1', null, cancellationToken); // decelerate stop RA
        }
    }

    #endregion

    #region Position

    public virtual async ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync('j', '1', null, cancellationToken);
        if (SkywatcherProtocol.TryParseResponse(response, out var data) && data.Length >= 6)
        {
            _posRa = SkywatcherProtocol.DecodePosition(data.AsSpan(0, 6));
        }
        return StepsToRa(_posRa, _posDec);
    }

    public virtual async ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync('j', '2', null, cancellationToken);
        if (SkywatcherProtocol.TryParseResponse(response, out var data) && data.Length >= 6)
        {
            _posDec = SkywatcherProtocol.DecodePosition(data.AsSpan(0, 6));
        }
        return StepsToDec(_posDec);
    }

    public ValueTask<double> GetTargetRightAscensionAsync(CancellationToken cancellationToken)
        => GetRightAscensionAsync(cancellationToken);

    public ValueTask<double> GetTargetDeclinationAsync(CancellationToken cancellationToken)
        => GetDeclinationAsync(cancellationToken);

    public ValueTask<double> GetSiderealTimeAsync(CancellationToken cancellationToken)
    {
        var transform = new Transform(TimeProvider);
        transform.RefreshDateTimeFromTimeProvider();
        transform.SiteLongitude = double.IsNaN(_siteLongitude) ? 0.0 : _siteLongitude;
        return ValueTask.FromResult(transform.LocalSiderealTime);
    }

    public async ValueTask<double> GetHourAngleAsync(CancellationToken cancellationToken)
    {
        var lst = await GetSiderealTimeAsync(cancellationToken);
        var ra = await GetRightAscensionAsync(cancellationToken);
        return CoordinateUtils.ConditionHA(lst - ra);
    }

    public ValueTask<uint> GetWormPeriodStepsAsync(TelescopeAxis axis, CancellationToken cancellationToken)
        => ValueTask.FromResult(axis == TelescopeAxis.Primary ? _wormPeriodRa : _wormPeriodDec);

    /// <summary>
    /// Returns raw encoder position as long for the given axis.
    /// </summary>
    public async ValueTask<long?> GetAxisPositionAsync(TelescopeAxis axis, CancellationToken cancellationToken)
    {
        var axisChar = axis == TelescopeAxis.Primary ? '1' : '2';
        var response = await SendAndReceiveAsync('j', axisChar, null, cancellationToken);
        if (SkywatcherProtocol.TryParseResponse(response, out var data) && data.Length >= 6)
        {
            return SkywatcherProtocol.DecodePosition(data.AsSpan(0, 6));
        }
        return null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One fresh <c>:j</c> round trip, the same read <see cref="GetAxisPositionAsync"/> makes, converted
    /// with the conventions <see cref="SkyToSteps"/> and <see cref="StepsToRa"/> already own: the RA
    /// angle is negated in the south (the motor runs the other way, <c>axisHours = 6 - HA</c>) so that
    /// positive is the tracking direction from home in both hemispheres; the Dec angle needs no such
    /// correction because <see cref="StepsToDec"/>'s mirror already keeps the straight half positive.
    /// Refreshes the cached position as a side effect, like every other read. Alt-az has no such axes.
    /// </remarks>
    public async ValueTask<double?> GetAxisAngleAsync(TelescopeAxis axis, CancellationToken cancellationToken)
    {
        if (_alignmentMode == AlignmentMode.AltAz || axis is not (TelescopeAxis.Primary or TelescopeAxis.Seconary))
        {
            return null;
        }
        var cpr = axis == TelescopeAxis.Primary ? _cprRa : _cprDec;
        if (cpr == 0 || await GetAxisPositionAsync(axis, cancellationToken) is not { } pos)
        {
            return null;
        }
        var steps = (int)pos;
        if (axis == TelescopeAxis.Primary)
        {
            _posRa = steps;
            var folded = FoldDegrees((double)steps / cpr * 360.0);
            return IsSouthernHemisphere ? -folded : folded;
        }
        _posDec = steps;
        return FoldedDecAxisDegrees(steps);
    }

    #endregion

    #region Slew

    public async ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken)
    {
        // A guide pulse runs the axis "running, not tracking" -- the same axis-status signature
        // as a slew. Per the ASCOM contract, Slewing must be False during a PulseGuide (the
        // separate IsPulseGuiding property reports that motion), so mask axis motion while a
        // pulse is in flight. Without this, calibration/guide pulses read as a perpetual slew.
        var pulseInFlight = Volatile.Read(ref _pulseGuideInFlight) > 0;
        var statusRa = await QueryAxisStatusAsync('1', cancellationToken);
        var statusDec = await QueryAxisStatusAsync('2', cancellationToken);
        _isSlewingRa = statusRa.IsRunning && !statusRa.IsTracking && !pulseInFlight;
        _isSlewingDec = statusDec.IsRunning && !statusDec.IsTracking && !pulseInFlight;
        var result = _isSlewingRa || _isSlewingDec;
        if (result)
        {
            Logger.LogDebug("IsSlewingAsync=true: RA(running={RaRun},tracking={RaTrk}) Dec(running={DecRun},tracking={DecTrk})",
                statusRa.IsRunning, statusRa.IsTracking, statusDec.IsRunning, statusDec.IsTracking);
            return true;
        }

        // EQMOD-style iterative goto: the axes have stopped, but if a goto is pending
        // its RA target steps went stale by the slew duration of sidereal motion (see
        // _gotoTargetRa). Re-issue short refinement gotos from this completion-detection
        // point (the poll-based protocol has no arrival callback) until the residual is
        // inside tolerance or the attempt cap is reached. While a refinement is in
        // flight the mount keeps reporting "slewing", so callers' wait-for-completion
        // loops remain correct without changes.
        if (!double.IsNaN(_gotoTargetRa))
        {
            if (Interlocked.CompareExchange(ref _gotoRefineInFlight, 1, 0) != 0)
            {
                // Another poller is already evaluating/issuing the refinement.
                return true;
            }
            try
            {
                if (_gotoRefineAttempts < MaxGotoRefineAttempts)
                {
                    var raNow = await GetRightAscensionAsync(cancellationToken);
                    var decNow = await GetDeclinationAsync(cancellationToken);
                    var dRaHours = _gotoTargetRa - raNow;
                    if (dRaHours > 12.0) dRaHours -= 24.0;
                    else if (dRaHours < -12.0) dRaHours += 24.0;
                    var cosDec = Math.Cos(_gotoTargetDec * Math.PI / 180.0);
                    var raErrArcsec = dRaHours * 15.0 * 3600.0 * cosDec;
                    var decErrArcsec = (_gotoTargetDec - decNow) * 3600.0;
                    var errArcsec = Math.Sqrt(raErrArcsec * raErrArcsec + decErrArcsec * decErrArcsec);
                    if (errArcsec > GotoRefineToleranceArcsec)
                    {
                        _gotoRefineAttempts++;
                        Logger.LogInformation(
                            "Iterative goto refinement {Attempt}/{Max}: residual {Err:F1}\" -> re-slewing to RA={Ra:F4}h Dec={Dec:F4}",
                            _gotoRefineAttempts, MaxGotoRefineAttempts, errArcsec, _gotoTargetRa, _gotoTargetDec);
                        await SlewToRaDecCoreAsync(_gotoTargetRa, _gotoTargetDec, _gotoPointingState, cancellationToken);
                        return true;
                    }
                }
                _gotoTargetRa = double.NaN; // arrived (or attempts exhausted) -- goto complete

                // A completed GoTo resumes sidereal tracking on a real SkyWatcher rig -- this mirrors
                // GSServer's SkyServer.GoToAsync, which ends with `Tracking = tracking || Tracking`.
                // This is our only post-goto hook (BeginSlew is fire-and-forget; completion is
                // poll-detected here), and it runs exactly once per goto under the _gotoRefineInFlight
                // gate. SetTrackingAsync is idempotent when the axis already auto-resumed (it only
                // re-times the step period via :I). Best-effort: a tracking-resume hiccup must not
                // break slew-completion detection -- let cancellation propagate, swallow the rest.
                try
                {
                    await SetTrackingAsync(true, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogWarning(ex, "Post-goto tracking resume failed; relying on firmware auto-resume.");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _gotoRefineInFlight, 0);
            }
        }
        return false;
    }

    public virtual async ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        EnsureEquatorialAlignment("Slewing to RA/Dec");

        if (_cprRa == 0 || _cprDec == 0)
        {
            throw new InvalidOperationException("Mount not initialized");
        }

        // Which of the two axis solutions reaches this target counterweight-down is decided by the
        // target's hour angle (GSS Axes.RaDecToAxesXy: east of the meridian -> through the pole), the
        // same rule DestinationSideOfPierAsync already answers for the session's flip logic. The
        // flip IS this choice landing on the other solution on a re-slew; GSS never flips while
        // tracking, and neither does this driver.
        var destination = await DestinationSideOfPierAsync(ra, dec, cancellationToken);
        _gotoPointingState = destination is PointingState.ThroughThePole ? destination : PointingState.Normal;

        // Arm the iterative-goto refinement (see IsSlewingAsync) BEFORE the first pass
        // so a poll racing the slew start still sees the pending target.
        _gotoTargetRa = ra;
        _gotoTargetDec = dec;
        _gotoRefineAttempts = 0;

        await SlewToRaDecCoreAsync(ra, dec, _gotoPointingState, cancellationToken);
    }

    /// <summary>
    /// One goto pass: stop both axes, refresh encoders, issue the :G/:H/:M/:J sequence
    /// for the delta to (<paramref name="ra"/>, <paramref name="dec"/>). Shared by
    /// <see cref="BeginSlewRaDecAsync"/> (which arms the refinement state) and the
    /// refinement passes in <see cref="IsSlewingAsync"/> (which must not re-arm it).
    /// </summary>
    private async ValueTask SlewToRaDecCoreAsync(double ra, double dec, PointingState pointingState, CancellationToken cancellationToken)
    {
        // Stop both axes and wait for FullStop: :K only STARTS a deceleration, and
        // real firmware rejects the goto's :G with !2 until the motor has stopped.
        // No tracking command is issued here. Tracking is resumed at the goto-completion point in
        // IsSlewingAsync (mirroring GSServer's post-GoTo `Tracking = ...`), and StartPulseGuideAsync reads
        // the LIVE tracking status (a fresh axis-status query, not a cached flag) to pick the RA
        // pulse branch -- so the RA pulse decision can never desync from the actual axis state.
        await StopAxisAndWaitAsync('1', cancellationToken);
        await StopAxisAndWaitAsync('2', cancellationToken);

        // Refresh cached encoder positions from the mount before computing the delta.
        // Without this, _posRa / _posDec may be stale (only updated on Get*Async reads),
        // and a stale value that happens to match the target produces delta=0 in
        // SlewAxisToAsync, which silently no-ops the slew; exactly the "sometimes
        // doesn't react" symptom.
        var raResp = await SendAndReceiveAsync('j', '1', null, cancellationToken);
        if (SkywatcherProtocol.TryParseResponse(raResp, out var raData) && raData.Length >= 6)
        {
            _posRa = SkywatcherProtocol.DecodePosition(raData.AsSpan(0, 6));
        }
        var decResp = await SendAndReceiveAsync('j', '2', null, cancellationToken);
        if (SkywatcherProtocol.TryParseResponse(decResp, out var decData) && decData.Length >= 6)
        {
            _posDec = SkywatcherProtocol.DecodePosition(decData.AsSpan(0, 6));
        }

        var (targetRaSteps, targetDecSteps) = SkyToSteps(ra, dec, pointingState);

        Logger.LogInformation(
            "BeginSlewRaDec: target RA={RaH:F4}h Dec={Dec:F4} ({State}) | RA steps {CurRa}->{TgtRa} (delta {DRa}) | Dec steps {CurDec}->{TgtDec} (delta {DDec})",
            ra, dec, pointingState, _posRa, targetRaSteps, targetRaSteps - _posRa,
            _posDec, targetDecSteps, targetDecSteps - _posDec);

        // Slew RA axis
        await SlewAxisToAsync('1', _posRa, targetRaSteps, cancellationToken);
        // Slew Dec axis
        await SlewAxisToAsync('2', _posDec, targetDecSteps, cancellationToken);
    }

    private async ValueTask SlewAxisToAsync(char axis, int currentSteps, int targetSteps, CancellationToken cancellationToken)
    {
        var delta = targetSteps - currentSteps;
        if (delta == 0)
        {
            Logger.LogInformation("SlewAxis {Axis}: delta=0, skipping (current already matches target)", axis);
            return;
        }

        var forward = delta > 0;
        var absDelta = Math.Abs(delta);

        // GSS picks the GOTO speed tier by distance: below the low-speed margin
        // (5 s of slewing at 128x sidereal = 640 sidereal-seconds of steps) it uses
        // the low-speed GOTO func, else high-speed. Break-point steps (:M) follow
        // GSS's defaults: 3500 for high-speed, 0 for low-speed.
        var cpr = axis == '1' ? _cprRa : _cprDec;
        var lowSpeedMarginSteps = (long)(640.0 * SIDEREAL_RATE / 3600.0 * cpr / 360.0);
        var func = absDelta > lowSpeedMarginSteps ? SkywatcherMotionFunc.HighSpeedGoto : SkywatcherMotionFunc.LowSpeedGoto;
        var breakSteps = func == SkywatcherMotionFunc.HighSpeedGoto ? 3500u : 0u;

        await SendCommandAsync('G', axis, SkywatcherProtocol.EncodeMotionMode(func, forward, IsSouthernHemisphere), cancellationToken);
        await SendCommandAsync('H', axis, SkywatcherProtocol.EncodeUInt24((uint)absDelta), cancellationToken); // step count
        await SendCommandAsync('M', axis, SkywatcherProtocol.EncodeUInt24(breakSteps), cancellationToken); // break-point increment
        await SendCommandAsync('J', axis, null, cancellationToken); // start
    }

    public async ValueTask AbortSlewAsync(CancellationToken cancellationToken)
    {
        // Disarm any pending iterative-goto refinement FIRST -- an abort must not be
        // followed by a refinement pass chasing the cancelled target.
        _gotoTargetRa = double.NaN;
        // Instant stop both axes
        await SendCommandAsync('L', '1', null, cancellationToken);
        await SendCommandAsync('L', '2', null, cancellationToken);
        _isSlewingRa = false;
        _isSlewingDec = false;
    }

    public virtual async ValueTask SyncRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        EnsureEquatorialAlignment("Sync to RA/Dec");

        // A sync says where the mount already IS, so it stays in the half the Dec encoder is in: the
        // straight solution for a mount that has flipped (or sits at home), the through-the-pole one
        // for a mount that has not. Choosing by the target's hour angle here would teleport the model
        // across the pier on every plate-solve sync of an unflipped rig.
        var pointingState = IsThroughThePole(_posDec) ? PointingState.ThroughThePole : PointingState.Normal;
        var (raSteps, decSteps) = SkyToSteps(ra, dec, pointingState);

        // Set encoder positions
        await SendCommandAsync('E', '1', SkywatcherProtocol.EncodePosition(raSteps), cancellationToken);
        await SendCommandAsync('E', '2', SkywatcherProtocol.EncodePosition(decSteps), cancellationToken);
        _posRa = raSteps;
        _posDec = decSteps;
    }

    #endregion

    #region Move Axis

    public async ValueTask MoveAxisAsync(TelescopeAxis axis, double rate, CancellationToken cancellationToken)
    {
        var axisChar = axis == TelescopeAxis.Primary ? '1' : '2';

        if (axis is not (TelescopeAxis.Primary or TelescopeAxis.Seconary))
        {
            throw new InvalidOperationException($"Axis {axis} is not supported");
        }

        if (rate == 0.0)
        {
            await SendCommandAsync('K', axisChar, null, cancellationToken);
            return;
        }

        // The axis may be running (tracking, or a previous MoveAxis at a different
        // rate): real firmware rejects :G mid-motion (!2), so stop and wait first.
        var status = await QueryAxisStatusAsync(axisChar, cancellationToken);
        if (status.IsRunning)
        {
            await StopAxisAndWaitAsync(axisChar, cancellationToken);
        }

        var forward = rate > 0;
        var absRate = Math.Abs(rate);

        // Determine if high speed
        var siderealDegPerSec = SIDEREAL_RATE / 3600.0;
        var highSpeed = absRate > siderealDegPerSec * 2; // high speed above 2x sidereal

        // High-speed slew func must pair with the highSpeed T1 preset below (the
        // firmware interprets the :I period at 1/highSpeedRatio scale in that mode).
        var func = highSpeed ? SkywatcherMotionFunc.HighSpeedSlew : SkywatcherMotionFunc.LowSpeedSlew;
        await SendCommandAsync('G', axisChar, SkywatcherProtocol.EncodeMotionMode(func, forward, IsSouthernHemisphere), cancellationToken);
        var cpr = axisChar == '1' ? _cprRa : _cprDec;
        var t1 = SkywatcherProtocol.ComputeT1Preset(_tmrFreq, cpr, absRate, highSpeed, _highSpeedRatio);
        await SendCommandAsync('I', axisChar, SkywatcherProtocol.EncodeUInt24(t1), cancellationToken);
        await SendCommandAsync('J', axisChar, null, cancellationToken);
    }

    #endregion

    #region Pulse Guide

    /// <summary>
    /// Minimum pulse duration. Below the serial round-trip latency a pulse is noise;
    /// matches GSServer's MinPulseDurationRa/Dec default of 20 ms.
    /// </summary>
    private static readonly TimeSpan MinPulseDuration = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// A background hold that failed after its caller had already been released, kept until someone
    /// asks. First one wins and it is cleared on read.
    /// </summary>
    /// <remarks>
    /// Start-and-return means the restore no longer has a caller to throw to, which is the one
    /// thing the blocking shape did get right. Rather than invent a channel for it, the fault is
    /// parked here and re-thrown from the next <see cref="StartPulseGuideAsync"/> or
    /// <see cref="IsPulseGuidingAsync"/> -- and the second of those is the one that matters,
    /// because it is what the guider polls WHILE waiting for this very pulse, so a failed restore
    /// still surfaces inside the same guide frame that caused it.
    /// </remarks>
    private Exception? _pendingPulseFault;

    private void ThrowPendingPulseFault()
    {
        if (Interlocked.Exchange(ref _pendingPulseFault, null) is { } fault)
        {
            throw fault;
        }
    }

    /// <summary>
    /// Starts a guide pulse and returns once the motor board has been COMMANDED, honouring the
    /// contract on <see cref="IMountDriver.StartPulseGuideAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>Synta boards have no "pulse for N ms" primitive -- an RA pulse changes the live step
    /// period and the driver must send the <c>:I</c> restore itself, a Dec pulse starts the axis and
    /// the driver must send the <c>:K</c> -- so SOMETHING has to hold the duration. It used to be
    /// the caller's await, which is why RA and Dec guide corrections serialised on this mount family
    /// and no other. It is now a background task, and the split is exactly at "commanded": the START
    /// commands go out on the caller's own path, so a mount that will not accept the pulse is still
    /// the caller's problem, while the wait and the restore are not.</para>
    ///
    /// <para><b>The in-flight count is raised BEFORE the first write and lowered only when the hold
    /// ends</b> (GSS #109 / finding 2): a caller must never be able to observe "no pulse running"
    /// for a pulse that has been issued. It stays a counter rather than a flag so an overlapping
    /// RA+Dec pair clears only when BOTH finish.</para>
    ///
    /// <para>The caller's token is deliberately passed into the hold. Cancelling a run should cut a
    /// pulse short, and the restore already runs under <see cref="CancellationToken.None"/> in a
    /// <c>finally</c> so it happens anyway -- an abandoned pulse must not leave the axis at
    /// <c>(1 +/- f) x sidereal</c>.</para>
    /// </remarks>
    public async ValueTask StartPulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
    {
        ThrowPendingPulseFault();

        if (duration < MinPulseDuration)
        {
            Logger.LogDebug("Ignoring {Direction} pulse of {DurationMs:F1} ms (below the {MinMs} ms floor)",
                direction, duration.TotalMilliseconds, MinPulseDuration.TotalMilliseconds);
            return;
        }

        // Mark a pulse in flight so IsSlewingAsync does not report this motion as a slew and
        // IsPulseGuidingAsync reports it instead (ASCOM semantics). Counter, not a flag, so an
        // overlapping RA+Dec pulse pair clears only when BOTH complete.
        Interlocked.Increment(ref _pulseGuideInFlight);

        // Signalled by the core the moment the board has been commanded; the caller waits on this
        // and nothing more. RunContinuationsAsynchronously so completing it cannot drag the hold's
        // remaining work onto the caller's thread, which would re-serialise the very thing this
        // change exists to stop.
        var commanded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RunPulseAsync(direction, duration, commanded, cancellationToken);
        await commanded.Task;
    }

    /// <summary>
    /// Runs the whole pulse -- start commands, the hold, the restore -- releasing the caller at the
    /// "commanded" point via <paramref name="commanded"/>.
    /// </summary>
    private async Task RunPulseAsync(
        GuideDirection direction, TimeSpan duration, TaskCompletionSource commanded, CancellationToken cancellationToken)
    {
        try
        {
            await PulseGuideCoreAsync(direction, duration, commanded, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled pulse is NOT a fault and must not be parked: parking it would re-throw an
            // OperationCanceledException from some unrelated later IsPulseGuiding, long after the
            // run that cancelled it had gone. The restore/stop still ran -- every branch does it in
            // a finally under CancellationToken.None precisely so an abandoned pulse cannot leave
            // the axis at (1 +/- f) x sidereal. Logged rather than swallowed silently.
            Logger.LogDebug(ex, "{Direction} pulse cancelled after {DurationMs:F0} ms; the axis was still restored",
                direction, duration.TotalMilliseconds);
            commanded.TrySetException(ex);
        }
        catch (Exception ex)
        {
            // Before "commanded" the caller is still waiting, so the fault is theirs to catch.
            // After it, nobody is: park it for the next Start or IsPulseGuiding to re-throw.
            if (!commanded.TrySetException(ex))
            {
                Interlocked.CompareExchange(ref _pendingPulseFault, ex, null);
            }
        }
        finally
        {
            // Belt and braces: a path that somehow returned without signalling must not wedge the
            // caller on a task that will never complete.
            commanded.TrySetResult();
            Interlocked.Decrement(ref _pulseGuideInFlight);
        }
    }

    private async ValueTask PulseGuideCoreAsync(
        GuideDirection direction, TimeSpan duration, TaskCompletionSource commanded, CancellationToken cancellationToken)
    {
        var guideRate = _guideRate;
        var siderealDegPerSec = SIDEREAL_RATE / 3600.0;

        if (direction is GuideDirection.East or GuideDirection.West)
        {
            // RA pulse guiding OFFSETS the sidereal tracking rate rather than replacing it:
            // West = (1+f) x sidereal, East = (1-f) x sidereal, both in the tracking
            // direction (forward north / reverse south). Commanding f x sidereal with a
            // direction flag (the old behaviour) made BOTH pulses drift the star east
            // relative to the sky: a West pulse ran the axis slower than sidereal (-f
            // relative) and an East pulse reversed it (-(1+f) relative), with a (1+f)/f
            // gain asymmetry between the two directions.
            // Never negative for East: SkywatcherGuideRate bounds Fraction at 1.0, so the axis
            // never has to reverse and a live :I (which sets magnitude only, direction living in :G)
            // is the correct command. That bound is the type's whole purpose -- see its remarks, and
            // GSServer's #89, which had to stop/re-:G/restart precisely because its rate is unbounded.
            var rateFactor = direction is GuideDirection.West
                ? guideRate.WestRateFactor
                : guideRate.EastRateFactor;
            // At 1.0x an East pulse cancels tracking exactly, and the boards cannot encode a zero step
            // period -- command sidereal/1000 so the axis looks stopped without a stop/start transient
            // (GSS does the same). Named on the type rather than a Math.Max clamp here: a clamp reads
            // as defence against negatives and would keep silently "working" if the bound were lifted.
            var guideSpeed = siderealDegPerSec *
                (direction is GuideDirection.East && guideRate.EastPulseHaltsTheAxis ? 1e-3 : rateFactor);
            var t1Pulse = SkywatcherProtocol.EncodeUInt24(
                SkywatcherProtocol.ComputeT1Preset(_tmrFreq, _cprRa, guideSpeed, false, _highSpeedRatio));

            // Decide the RA branch from the LIVE tracking status (a fresh axis-status query), not a
            // cached flag. A real SkyWatcher (and the fake) resumes sidereal tracking when a goto
            // completes; a cached "desired tracking" flag was NOT synced on that path, so the first
            // post-slew RA pulse took the stop/start branch and :K-stopped the just-resumed tracking,
            // de-tracking the mount through guider calibration (the garbage-data root cause). One
            // status round-trip per pulse always reflects reality and cannot desync.
            if (await IsTrackingAsync(cancellationToken))
            {
                // The axis is already running at sidereal in the tracking direction and the
                // combined rate never changes sign (f <= 1): change ONLY the step period
                // (:I) live, then restore it. No stop/start; a K+G+I+J round trip adds
                // decel/accel transients comparable to a short pulse's length, and real
                // firmware rejects :G while the motor is running (error !2).
                await SendCommandAsync('I', '1', t1Pulse, cancellationToken);
                commanded.TrySetResult();
                try
                {
                    await TimeProvider.SleepAsync(duration, cancellationToken);
                }
                finally
                {
                    // Restore the sidereal step period even when the pulse is cancelled;
                    // a stuck guide rate would drift the mount at (1±f) x sidereal forever.
                    // VERIFIED, not merely sent: this is the one command in a pulse whose failure
                    // is both invisible and permanent, so an unacknowledged restore is a fault the
                    // session must see rather than a log line nobody reads until the morning.
                    var t1Sidereal = SkywatcherProtocol.ComputeT1Preset(_tmrFreq, _cprRa, siderealDegPerSec, false, _highSpeedRatio);
                    await SendCommandVerifiedAsync('I', '1', SkywatcherProtocol.EncodeUInt24(t1Sidereal), CancellationToken.None);
                }
            }
            else
            {
                // Not tracking: there is no baseline to offset. Run the axis at the
                // combined rate for the duration, then stop it again.
                var south = IsSouthernHemisphere;
                await SendCommandAsync('G', '1', SkywatcherProtocol.EncodeMotionMode(SkywatcherMotionFunc.LowSpeedSlew, forward: !south, south), cancellationToken);
                await SendCommandAsync('I', '1', t1Pulse, cancellationToken);
                await SendCommandAsync('J', '1', null, cancellationToken);
                commanded.TrySetResult();
                try
                {
                    await TimeProvider.SleepAsync(duration, cancellationToken);
                }
                finally
                {
                    // Same reasoning as the :I1 restore above: this :K1 is the only thing that ends
                    // the motion the pulse started, and the axis is NOT tracking, so a lost stop is
                    // an axis running at the combined rate with nothing scheduled to halt it.
                    await SendCommandVerifiedAsync('K', '1', null, CancellationToken.None);
                }
            }
        }
        else
        {
            // Dec has no tracking baseline: move the axis at f x sidereal in the pulse direction.
            var forward = direction switch
            {
                GuideDirection.North => true,  // Dec forward
                GuideDirection.South => false, // Dec reverse
                _ => throw new ArgumentException($"Unknown guide direction {direction}", nameof(direction))
            };
            var guideSpeed = siderealDegPerSec * guideRate.Fraction;

            if (_decPulseGoTo)
            {
                await DecPulseAsMicroGotoAsync(forward, guideSpeed, duration, commanded, cancellationToken);
                return;
            }

            // Real firmware rejects :G while the axis is still decelerating from the previous
            // pulse's :K2 (error !2, silently discarded by SendCommandAsync); the pulse is then
            // lost. Mirror the decPulseGoTo / slew paths: ensure a full stop before :G2. Cheap
            // when already stopped (one status round-trip).
            if ((await QueryAxisStatusAsync('2', cancellationToken)).IsRunning)
            {
                await StopAxisAndWaitAsync('2', cancellationToken);
            }

            await SendCommandAsync('G', '2', SkywatcherProtocol.EncodeMotionMode(SkywatcherMotionFunc.LowSpeedSlew, forward, IsSouthernHemisphere), cancellationToken);
            var t1 = SkywatcherProtocol.ComputeT1Preset(_tmrFreq, _cprDec, guideSpeed, false, _highSpeedRatio);
            await SendCommandAsync('I', '2', SkywatcherProtocol.EncodeUInt24(t1), cancellationToken);
            await SendCommandAsync('J', '2', null, cancellationToken);
            commanded.TrySetResult();
            try
            {
                await TimeProvider.SleepAsync(duration, cancellationToken);
            }
            finally
            {
                // Verified for the same reason as the RA paths. Note :K only STARTS a deceleration,
                // so a '=' here proves the board accepted the stop, not that the axis has halted --
                // strictly weaker than the FullStop poll the goto paths use, and strictly stronger
                // than not looking at the answer at all, which is what this used to do.
                await SendCommandVerifiedAsync('K', '2', null, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Dec pulse as a relative low-speed micro-GOTO (GSServer's DecPulseGoTo mode): the
    /// duration converts to an exact step count (duration x rate in arcsec x steps/arcsec),
    /// the goto runs it, and we poll until FullStop (3.5 s cap, GSS's wait). Faster settling
    /// than holding the rate for the full duration, and the displacement is exact.
    /// </summary>
    private async ValueTask DecPulseAsMicroGotoAsync(
        bool forward, double guideSpeedDegPerSec, TimeSpan duration, TaskCompletionSource commanded, CancellationToken cancellationToken)
    {
        var arcsec = guideSpeedDegPerSec * 3600.0 * duration.TotalSeconds;
        var steps = (int)Math.Round(arcsec * _cprDec / (360.0 * 3600.0));
        if (steps == 0)
        {
            Logger.LogDebug("Dec micro-GOTO pulse of {DurationMs:F1} ms rounds to zero steps; skipping", duration.TotalMilliseconds);
            return;
        }

        var status = await QueryAxisStatusAsync('2', cancellationToken);
        if (status.IsRunning)
        {
            await StopAxisAndWaitAsync('2', cancellationToken);
        }

        steps = _firmwareInfo.MountModel.AdjustGotoSteps(steps);
        await SendCommandAsync('G', '2', SkywatcherProtocol.EncodeMotionMode(SkywatcherMotionFunc.LowSpeedGoto, forward, IsSouthernHemisphere), cancellationToken);
        await SendCommandAsync('H', '2', SkywatcherProtocol.EncodeUInt24((uint)steps), cancellationToken);
        await SendCommandAsync('M', '2', SkywatcherProtocol.EncodeUInt24(0), cancellationToken);
        await SendCommandAsync('J', '2', null, cancellationToken);
        commanded.TrySetResult();

        var pollInterval = TimeSpan.FromMilliseconds(25);
        var timeout = TimeSpan.FromSeconds(3.5);
        var waited = TimeSpan.Zero;
        while (waited < timeout)
        {
            if (!(await QueryAxisStatusAsync('2', cancellationToken)).IsRunning)
            {
                return;
            }
            await TimeProvider.SleepAsync(pollInterval, cancellationToken);
            waited += pollInterval;
        }
        Logger.LogWarning("Dec micro-GOTO pulse ({Steps} steps) did not reach full stop within {TimeoutSeconds:F1}s", steps, timeout.TotalSeconds);
    }

    /// <summary>
    /// Whether any pulse is still running -- and the place a failed background hold is reported.
    /// </summary>
    /// <remarks>
    /// Throwing from what looks like a read is deliberate. The guider polls this WHILE waiting for
    /// a pulse it just started, so it is the only place a restore that would not take can be
    /// reported inside the guide frame that caused it; report it from the next
    /// <see cref="StartPulseGuideAsync"/> alone and a run that stops guiding right afterwards never
    /// hears about a mount left at <c>(1 +/- f) x sidereal</c>. ASCOM allows any property to throw a
    /// driver exception, and this is exactly the situation the allowance is for.
    /// </remarks>
    public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken)
    {
        ThrowPendingPulseFault();
        return ValueTask.FromResult(Volatile.Read(ref _pulseGuideInFlight) > 0);
    }

    #endregion

    #region Guide Rates

    public ValueTask<double> GetGuideRateRightAscensionAsync(CancellationToken cancellationToken)
    {
        // Reports what the mount is ACTUALLY set to, derived from the snapped rate rather than from
        // whatever was last requested -- ASCOM's contract is that a getter reflects the hardware.
        return ValueTask.FromResult(_guideRate.Fraction * SIDEREAL_RATE / 3600.0);
    }

    public async ValueTask SetGuideRateRightAscensionAsync(double value, CancellationToken cancellationToken)
    {
        // The firmware offers five rates and nothing between them, so an ASCOM client's arbitrary
        // double must snap. SAY SO when it moves materially: guiding at a rate nobody asked for looks
        // like poor seeing or a mis-tuned aggressiveness, and silence there costs a night to diagnose.
        var siderealDegPerSec = SIDEREAL_RATE / 3600.0;
        var fraction = value / siderealDegPerSec;
        _guideRate = SkywatcherGuideRate.Nearest(fraction);
        if (SkywatcherGuideRate.WasSnapped(fraction, _guideRate))
        {
            Logger.LogInformation(
                "{MountName}: guide rate {Requested:F3}x sidereal is not encodable; using {Applied:F3}x.",
                Name, fraction, _guideRate.Fraction);
        }
        // Send :P to set the ST-4 autoguide port speed on the mount hardware
        await SendCommandAsync('P', '1', _guideRate.WireIndex, cancellationToken);
    }

    public ValueTask<double> GetGuideRateDeclinationAsync(CancellationToken cancellationToken)
        => GetGuideRateRightAscensionAsync(cancellationToken); // same guide speed for both axes

    public async ValueTask SetGuideRateDeclinationAsync(double value, CancellationToken cancellationToken)
    {
        await SetGuideRateRightAscensionAsync(value, cancellationToken);
        // RA setter already snapped the rate and sent :P to axis 1;
        // also send to axis 2 for the Dec ST-4 port
        await SendCommandAsync('P', '2', _guideRate.WireIndex, cancellationToken);
    }

    #endregion

    #region Rates (not supported)

    public ValueTask<double> GetRightAscensionRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(0.0);

    public ValueTask SetRightAscensionRateAsync(double value, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Skywatcher does not support setting RA rate offset");

    public ValueTask<double> GetDeclinationRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(0.0);

    public ValueTask SetDeclinationRateAsync(double value, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Skywatcher does not support setting Dec rate offset");

    #endregion

    #region Pier Side

    /// <summary>Read off the Dec encoder half -- the mechanical state -- except in alt-az, which has none.</summary>
    public PointingStateSource PointingStateSource
        => _alignmentMode == AlignmentMode.AltAz ? PointingStateSource.None : PointingStateSource.Measured;

    public async ValueTask<PointingState> GetSideOfPierAsync(CancellationToken cancellationToken)
    {
        // Alt-az mounts have no pier side. Reporting Unknown also makes the session's GEM-only flip
        // gate skip meridian-flip handling for this mount.
        if (_alignmentMode == AlignmentMode.AltAz)
        {
            return PointingState.Unknown;
        }

        // Pier side is determined from the Dec encoder position (the physical orientation of
        // the telescope), not from HA (which only tells you where the target is in the sky).
        // Following GSServer GermanPolar convention (SkyServer.SideOfPier):
        //   Raw mount Dec axis = pos / CPR * 360 + 90  (home encoder 0x800000 = 90°)
        //   App-space = 180 - raw
        //   Normal (counterweight down while looking WEST) when |app-space| < 90.0000000001,
        //   i.e. 0 <= raw < 180, which is 0 <= pos < CPR/2 -- the home boundary is INCLUSIVE, so a
        //   mount that has never moved is Normal and the connect-time pole sync round-trips to HA 0.
        // This is the REPORTING half of GSS's model; SkyToSteps is the CHOOSING half, and the two
        // must agree or a goto lands in a state it then mis-reports.
        var response = await SendAndReceiveAsync('j', '2', null, cancellationToken);
        if (SkywatcherProtocol.TryParseResponse(response, out var data) && data.Length >= 6 && _cprDec > 0)
        {
            _posDec = SkywatcherProtocol.DecodePosition(data.AsSpan(0, 6));
            return IsThroughThePole(_posDec) ? PointingState.ThroughThePole : PointingState.Normal;
        }
        return PointingState.Normal;
    }

    /// <summary>
    /// GSS's forced flip: reach the CURRENT sky position through the other axis solution. A no-op when
    /// the mount is already in the requested state. Refuses <see cref="PointingState.Unknown"/> and
    /// alt-az. GSS additionally refuses a forced flip outside its flip limits; this driver knows no such
    /// bound of its own -- the mount safety limit is the one that applies, and it acts on the result.
    /// </summary>
    public async ValueTask SetSideOfPierAsync(PointingState pointingState, CancellationToken cancellationToken)
    {
        EnsureEquatorialAlignment("Set side of pier");
        if (pointingState is not (PointingState.Normal or PointingState.ThroughThePole))
        {
            throw new ArgumentOutOfRangeException(nameof(pointingState), pointingState, "A pointing state to flip to must be Normal or ThroughThePole");
        }
        if (_cprRa == 0 || _cprDec == 0)
        {
            throw new InvalidOperationException("Mount not initialized");
        }
        if (await GetSideOfPierAsync(cancellationToken) == pointingState)
        {
            return;
        }

        // The same target, the other solution: exactly what a goto does when the destination's pier side
        // differs from the current one, so it shares the goto's refinement state and its command-time
        // choice of solution (see _gotoPointingState).
        var ra = await GetRightAscensionAsync(cancellationToken);
        var dec = await GetDeclinationAsync(cancellationToken);
        _gotoPointingState = pointingState;
        _gotoTargetRa = ra;
        _gotoTargetDec = dec;
        _gotoRefineAttempts = 0;
        await SlewToRaDecCoreAsync(ra, dec, pointingState, cancellationToken);
    }

    public ValueTask<PointingState> DestinationSideOfPierAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        // Alt-az has no pier side; returning Unknown also makes BeginSlewToTargetAsync refuse the
        // (equatorial) slew for an alt-az mount instead of pointing it wrong.
        if (_alignmentMode == AlignmentMode.AltAz)
        {
            return ValueTask.FromResult(PointingState.Unknown);
        }

        // Determine pier side based on hour angle
        var transform = new Transform(TimeProvider);
        transform.RefreshDateTimeFromTimeProvider();
        transform.SiteLongitude = double.IsNaN(_siteLongitude) ? 0.0 : _siteLongitude;
        var ha = CoordinateUtils.ConditionHA(transform.LocalSiderealTime - ra);
        return ValueTask.FromResult(ha >= 0 ? PointingState.Normal : PointingState.ThroughThePole);
    }

    #endregion

    #region Site

    public ValueTask<double> GetSiteLatitudeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_siteLatitude);

    public async ValueTask SetSiteLatitudeAsync(double latitude, CancellationToken cancellationToken)
    {
        _siteLatitude = latitude;
        await MaybeSyncToPoleAfterSiteSetAsync(cancellationToken);
    }

    public ValueTask<double> GetSiteLongitudeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_siteLongitude);

    public async ValueTask SetSiteLongitudeAsync(double longitude, CancellationToken cancellationToken)
    {
        _siteLongitude = longitude;
        await MaybeSyncToPoleAfterSiteSetAsync(cancellationToken);
    }

    /// <summary>
    /// If the encoder is still at raw home (0,0), i.e. the user just connected and the
    /// mount has not been moved: push a sync to (LST, ±90°) so the driver reports a
    /// sensible "parked at the pole" position. The original DoConnectDeviceAsync init
    /// also runs this block, but at that stage <c>_siteLatitude</c> is still NaN
    /// (profile-driven reconcile pushes lat/lon AFTER connect), so the sync gets skipped
    /// and the mount is reported as Dec=+90 / HA=6h regardless of hemisphere.
    /// Triggering it here picks up the moment when site coords actually land.
    /// </summary>
    private async ValueTask MaybeSyncToPoleAfterSiteSetAsync(CancellationToken cancellationToken)
    {
        if (!Connected) return;
        // "Parked at the pole" is an equatorial reporting convenience. In alt-az the home IS the raw
        // encoder zero (az 0 = north, alt 0 = horizontal), so there is nothing to sync, and an RA/Dec
        // sync is refused in alt-az anyway. Skip it so setting the site never fails for an alt-az mount.
        if (_alignmentMode == AlignmentMode.AltAz) return;
        if (_cprRa == 0 || _cprDec == 0) return;
        if (double.IsNaN(_siteLatitude) || double.IsNaN(_siteLongitude)) return;
        if (_posRa != 0 || _posDec != 0) return;

        var poleDec = _siteLatitude >= 0 ? 90.0 : -90.0;
        var lst = await GetSiderealTimeAsync(cancellationToken);
        await SyncRaDecAsync(lst, poleDec, cancellationToken);
    }

    public ValueTask<double> GetSiteElevationAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(double.IsNaN(_siteElevation) ? 0.0 : _siteElevation);

    public ValueTask SetSiteElevationAsync(double elevation, CancellationToken cancellationToken)
    {
        _siteElevation = elevation;
        return ValueTask.CompletedTask;
    }

    #endregion

    #region Time

    public bool TimeIsSetByUs => true;

    public ValueTask<DateTime?> TryGetUTCDateFromMountAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<DateTime?>(TimeProvider.GetUtcNow().UtcDateTime);

    public ValueTask SetUTCDateAsync(DateTime dateTime, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    #endregion

    #region Park

    public ValueTask<bool> AtHomeAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_posRa == 0 && _posDec == 0);

    public ValueTask<bool> AtParkAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_isParked);

    public async ValueTask ParkAsync(CancellationToken cancellationToken)
    {
        // Parking targets a fixed ENCODER position, not a sky position -- disarm any
        // pending iterative-goto refinement or it would chase the old sky target after
        // the park slew stops.
        _gotoTargetRa = double.NaN;
        // Stop both axes (tracking may be running) before the home goto; the goto's
        // :G is rejected with !2 on a moving motor.
        await StopAxisAndWaitAsync('1', cancellationToken);
        await StopAxisAndWaitAsync('2', cancellationToken);
        // Slew to home position (0x800000 = step 0)
        await SlewAxisToAsync('1', _posRa, 0, cancellationToken);
        await SlewAxisToAsync('2', _posDec, 0, cancellationToken);
        _isParked = true;
    }

    public ValueTask UnparkAsync(CancellationToken cancellationToken)
    {
        _isParked = false;
        return ValueTask.CompletedTask;
    }

    #endregion

    #region Camera Snap

    public async ValueTask CameraSnapAsync(CameraSnapSettings settings, CancellationToken cancellationToken)
    {
        // Aux port on
        await SendCommandAsync('O', '1', "1", cancellationToken);
        _snapActive = true;

        // Wait for shutter duration
        await TimeProvider.SleepAsync(settings.ShutterTime, cancellationToken);

        // Aux port off
        await SendCommandAsync('O', '1', "0", cancellationToken);
        _snapActive = false;
    }

    public ValueTask<CameraSnapSettings?> GetCameraSnapSettingsAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<CameraSnapSettings?>(null); // Snap port has no settings readback

    #endregion

    #region Driver Info

    public override string? DriverInfo => _firmwareInfo.VersionString is { Length: > 0 }
        ? $"Skywatcher {_firmwareInfo.MountModel.DisplayName} (FW {_firmwareInfo.VersionString})"
        : "Skywatcher Mount";

    public override string? Description => "Skywatcher Motor Controller mount";

    #endregion

    #region Connection

    protected override async Task<(bool Success, int ConnectionId, SkywatcherDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        ISerialConnection? serialDevice;
        try
        {
            if (await _device.ConnectSerialDeviceAsync(External, Logger, TimeProvider, encoding: _encoding, cancellationToken: cancellationToken) is { IsOpen: true } openedConnection)
            {
                serialDevice = openedConnection;
            }
            else
            {
                serialDevice = null;
            }
        }
        catch (Exception ex)
        {
            serialDevice = null;
            Logger.LogError(ex, "Error when connecting to serial port {DeviceUri}", _device.DeviceUri);
        }

        if (serialDevice is not null)
        {
            return (true, CONNECTION_ID_EXCLUSIVE, new SkywatcherDeviceInfo(serialDevice));
        }
        else
        {
            return (false, CONNECTION_ID_UNKNOWN, default(SkywatcherDeviceInfo));
        }
    }

    protected override async ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Query firmware from RA axis
            var fwResponse = await SendAndReceiveAsync('e', '1', null, cancellationToken);
            if (!SkywatcherProtocol.TryParseResponse(fwResponse, out var fwData) ||
                !SkywatcherProtocol.TryParseFirmwareResponse(fwData, out _firmwareInfo))
            {
                Logger.LogError("Failed to parse firmware response: {Response}", fwResponse);
                return false;
            }

            _supportsAdvancedCommands = SkywatcherProtocol.SupportsAdvancedCommands(
                _firmwareInfo.IntVersion, _firmwareInfo.MountModel);

            // Query capabilities
            var capResponse = await SendAndReceiveAsync('q', '1', "010000", cancellationToken);
            if (SkywatcherProtocol.TryParseResponse(capResponse, out var capData) && capData.Length >= 6)
            {
                _capabilities = SkywatcherProtocol.ParseCapabilities(capData);
            }

            // Query counts per revolution
            var cprRaResponse = await SendAndReceiveAsync('a', '1', null, cancellationToken);
            if (SkywatcherProtocol.TryParseResponse(cprRaResponse, out var cprRaData) && cprRaData.Length >= 6)
            {
                _cprRa = SkywatcherProtocol.DecodeUInt24(cprRaData.AsSpan(0, 6));
                _cprRa = SkywatcherProtocol.OverrideGearRatio(_cprRa, _firmwareInfo.MountModel);
            }

            var cprDecResponse = await SendAndReceiveAsync('a', '2', null, cancellationToken);
            if (SkywatcherProtocol.TryParseResponse(cprDecResponse, out var cprDecData) && cprDecData.Length >= 6)
            {
                _cprDec = SkywatcherProtocol.DecodeUInt24(cprDecData.AsSpan(0, 6));
            }

            // Query timer frequency
            var freqResponse = await SendAndReceiveAsync('b', '1', null, cancellationToken);
            if (SkywatcherProtocol.TryParseResponse(freqResponse, out var freqData) && freqData.Length >= 6)
            {
                _tmrFreq = SkywatcherProtocol.DecodeUInt24(freqData.AsSpan(0, 6));
            }

            // Query steps per worm revolution (PE period) via :s command
            var wormRaResponse = await SendAndReceiveAsync('s', '1', null, cancellationToken);
            if (SkywatcherProtocol.TryParseResponse(wormRaResponse, out var wormRaData) && wormRaData.Length >= 6)
            {
                _wormPeriodRa = SkywatcherProtocol.DecodeUInt24(wormRaData.AsSpan(0, 6));
            }

            var wormDecResponse = await SendAndReceiveAsync('s', '2', null, cancellationToken);
            if (SkywatcherProtocol.TryParseResponse(wormDecResponse, out var wormDecData) && wormDecData.Length >= 6)
            {
                _wormPeriodDec = SkywatcherProtocol.DecodeUInt24(wormDecData.AsSpan(0, 6));
            }

            // Query high-speed ratio
            var ratioResponse = await SendAndReceiveAsync('g', '1', null, cancellationToken);
            if (SkywatcherProtocol.TryParseResponse(ratioResponse, out var ratioData) && ratioData.Length >= 2)
            {
                if (byte.TryParse(ratioData.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var ratio))
                {
                    _highSpeedRatio = ratio;
                }
            }

            // Query current positions
            var posRaResponse = await SendAndReceiveAsync('j', '1', null, cancellationToken);
            if (SkywatcherProtocol.TryParseResponse(posRaResponse, out var posRaData) && posRaData.Length >= 6)
            {
                _posRa = SkywatcherProtocol.DecodePosition(posRaData.AsSpan(0, 6));
            }

            var posDecResponse = await SendAndReceiveAsync('j', '2', null, cancellationToken);
            if (SkywatcherProtocol.TryParseResponse(posDecResponse, out var posDecData) && posDecData.Length >= 6)
            {
                _posDec = SkywatcherProtocol.DecodePosition(posDecData.AsSpan(0, 6));
            }

            // Initialize both axes
            await SendCommandAsync('F', '3', null, cancellationToken);

            // Set ST-4 autoguide port speed to match our guide rate
            await SendCommandAsync('P', '1', _guideRate.WireIndex, cancellationToken);
            await SendCommandAsync('P', '2', _guideRate.WireIndex, cancellationToken);

            // Read site coordinates from URI
            await GetSiteLatitudeAsync(cancellationToken);
            await GetSiteLongitudeAsync(cancellationToken);

            // If encoder is at home (0,0), assume pointing at the pole (standard park position)
            if (_posRa == 0 && _posDec == 0 && !double.IsNaN(_siteLatitude))
            {
                var poleDec = _siteLatitude >= 0 ? 90.0 : -90.0;
                await SyncRaDecAsync(await GetSiderealTimeAsync(cancellationToken), poleDec, cancellationToken);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize Skywatcher mount");
            return false;
        }
    }

    protected override async Task<bool> DoDisconnectDeviceAsync(int connectionId, CancellationToken cancellationToken)
    {
        if (connectionId == CONNECTION_ID_EXCLUSIVE)
        {
            if (_deviceInfo.SerialDevice is { IsOpen: true } port)
            {
                await port.WaitAsync(cancellationToken);
                return port.TryClose();
            }
            else if (_deviceInfo.SerialDevice is { })
            {
                return true;
            }
        }
        return false;
    }

    #endregion

    #region Serial Protocol Helpers

    private async ValueTask<string?> SendAndReceiveAsync(char cmd, char axis, string? data, CancellationToken cancellationToken)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true } port)
        {
            return null;
        }

        using var @lock = await port.WaitAsync(cancellationToken);
        var command = SkywatcherProtocol.BuildCommand(cmd, axis, data);
        if (!await port.TryWriteAsync(command, cancellationToken))
        {
            return null;
        }
        return await port.TryReadTerminatedAsync(CrTerminator, cancellationToken);
    }

    /// <summary>
    /// One command round-trip, classified. A board that took the command answers <c>=</c>; one that
    /// refused answers <c>!X</c> (e.g. <c>!2</c> = motor not stopped, <c>!0</c> = unknown command);
    /// a board that says nothing at all leaves <see cref="Response"/> null, which is a read TIMEOUT
    /// and a different fact from a refusal -- the line may simply be desynced.
    /// </summary>
    private readonly record struct SkywatcherAck(string? Response)
    {
        public bool Accepted => Response is { Length: > 0 } && Response[0] == '=';
    }

    /// <summary>
    /// Attempts a safety-critical command gets before the driver gives up on it. Three, with no
    /// artificial delay between them: the two failure modes are a firmware refusal (instant, and a
    /// re-send is the whole remedy) and a read timeout (which has already spent the port's timeout
    /// waiting). This runs inside a pulse's <c>finally</c>, on the guide hot path, so the budget is
    /// deliberately small -- but any of it beats leaving an axis running.
    /// </summary>
    private const int VerifiedCommandAttempts = 3;

    private async ValueTask<SkywatcherAck> SendCommandRawAsync(char cmd, char axis, string? data, CancellationToken cancellationToken)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true } port)
        {
            throw new InvalidOperationException("Serial port is not connected");
        }

        using var @lock = await port.WaitAsync(cancellationToken);
        var command = SkywatcherProtocol.BuildCommand(cmd, axis, data);
        if (!await port.TryWriteAsync(command, cancellationToken))
        {
            throw new InvalidOperationException($"Failed to write command :{cmd}{axis}");
        }
        return new SkywatcherAck(await port.TryReadTerminatedAsync(CrTerminator, cancellationToken));
    }

    /// <summary>
    /// Best-effort send: log a refusal and carry on. Correct for every command whose failure costs
    /// one guide correction or one move, which the next frame re-issues. NOT correct for a command
    /// that stops an axis or restores its rate -- see <see cref="SendCommandVerifiedAsync"/>.
    /// </summary>
    private async ValueTask SendCommandAsync(char cmd, char axis, string? data, CancellationToken cancellationToken)
    {
        // A response beginning with '!' is a firmware error. These were once discarded silently,
        // which hid dropped commands: a rejected :G no-ops the whole G/H/M/J goto/pulse sequence
        // with no exception. Surface them so the gap is diagnosable.
        var ack = await SendCommandRawAsync(cmd, axis, data, cancellationToken);
        if (!ack.Accepted)
        {
            Logger.LogWarning("Skywatcher command :{Cmd}{Axis} was not acknowledged, response {Ack}",
                cmd, axis, ack.Response ?? "<none>");
        }
    }

    /// <summary>
    /// Send a command and VERIFY the board took it, retrying up to
    /// <see cref="VerifiedCommandAttempts"/> times and throwing
    /// <see cref="SkywatcherDriverException"/> when it never does.
    /// </summary>
    /// <remarks>
    /// <para>For the commands that put an axis back into a safe state, where a failure leaves the
    /// mount running at a rate the driver believes it has already cancelled. Sending such a command
    /// is not the same as it having taken effect, and the difference is invisible: an unrestored
    /// <c>:I1</c> tracks at up to 2x sidereal all night and shows up only as trailed subframes.
    /// See <see cref="SkywatcherDriverException"/> for why this set and not the others.</para>
    ///
    /// <para>Verifying is a genuinely NEW guarantee, not a preserved one. Before this the restore
    /// path had exactly one covered failure -- a write that returned false -- while a firmware
    /// refusal only reached the log and a read timeout reached nothing at all.</para>
    /// </remarks>
    private async ValueTask SendCommandVerifiedAsync(char cmd, char axis, string? data, CancellationToken cancellationToken)
    {
        var ack = default(SkywatcherAck);
        for (var attempt = 1; attempt <= VerifiedCommandAttempts; attempt++)
        {
            ack = await SendCommandRawAsync(cmd, axis, data, cancellationToken);
            if (ack.Accepted)
            {
                if (attempt > 1)
                {
                    Logger.LogWarning("Skywatcher command :{Cmd}{Axis} took effect on attempt {Attempt} of {Attempts}",
                        cmd, axis, attempt, VerifiedCommandAttempts);
                }
                return;
            }

            Logger.LogWarning("Skywatcher command :{Cmd}{Axis} was not acknowledged (response {Ack}) on attempt {Attempt} of {Attempts}",
                cmd, axis, ack.Response ?? "<none>", attempt, VerifiedCommandAttempts);
        }

        throw new SkywatcherDriverException(cmd, axis, ack.Response, VerifiedCommandAttempts);
    }

    private record struct AxisStatus(bool IsRunning, bool IsTracking, bool IsForward, bool IsInitDone);

    /// <summary>
    /// Decelerate-stop an axis and wait until it reports FullStop. :K only STARTS a
    /// deceleration; real firmware rejects a subsequent :G with !2 (motor not stopped)
    /// until the motor has actually halted. Mirrors GSServer's wait: 25 ms status
    /// polls, the stop re-issued every 5 polls, capped at 3.5 s.
    /// </summary>
    private async ValueTask StopAxisAndWaitAsync(char axisChar, CancellationToken cancellationToken)
    {
        await SendCommandAsync('K', axisChar, null, cancellationToken);

        var pollInterval = TimeSpan.FromMilliseconds(25);
        var timeout = TimeSpan.FromSeconds(3.5);
        var waited = TimeSpan.Zero;
        var polls = 0;
        while (waited < timeout)
        {
            var status = await QueryAxisStatusAsync(axisChar, cancellationToken);
            if (!status.IsRunning)
            {
                return;
            }
            if (++polls % 5 == 0)
            {
                await SendCommandAsync('K', axisChar, null, cancellationToken);
            }
            await TimeProvider.SleepAsync(pollInterval, cancellationToken);
            waited += pollInterval;
        }
        Logger.LogWarning("Skywatcher axis {Axis} did not reach full stop within {TimeoutSeconds:F1}s", axisChar, timeout.TotalSeconds);
    }

    private async ValueTask<AxisStatus> QueryAxisStatusAsync(char axis, CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync('f', axis, null, cancellationToken);
        if (SkywatcherProtocol.TryParseResponse(response, out var data) && data.Length >= 3)
        {
            // Real firmware replies 3 status nibbles (reference: GSServer GetAxisStatus):
            // nibble 0: bit0 = constant-speed mode (slew/tracking) vs GOTO, bit1 = reverse, bit2 = high speed
            // nibble 1: bit0 = running (0 = full stop)
            // nibble 2: bit0 = init done
            var n0 = SkywatcherProtocol.ParseHexNibble(data[0]);
            var n1 = SkywatcherProtocol.ParseHexNibble(data[1]);
            var n2 = SkywatcherProtocol.ParseHexNibble(data[2]);
            var isRunning = (n1 & 0x01) != 0;
            var isConstantSpeed = (n0 & 0x01) != 0; // tracking/MoveAxis rate, not a GOTO
            var isForward = (n0 & 0x02) == 0;
            var isInitDone = (n2 & 0x01) != 0;
            return new AxisStatus(isRunning, isRunning && isConstantSpeed, isForward, isInitDone);
        }
        return new AxisStatus(false, false, true, false);
    }

    #endregion

    #region Coordinate Conversion

    // A GEM reaches every sky position two ways: the straight solution, and the one with the RA axis
    // half a turn round and the Dec axis mirrored through home ("through the pole"). Which is
    // counterweight-DOWN depends on which side of the meridian the target is, and the encoders
    // record which one the mount is actually in: the Dec axis sits in the positive half-turn from
    // home for the straight solution and the negative one for the other. GSS keeps the two halves of
    // this apart -- Axes.RaDecToAxesXy CHOOSES (east of the meridian -> through the pole),
    // SkyServer.SideOfPier REPORTS (from the Dec axis) -- and so does this driver: SkyToSteps takes
    // the state as a parameter, StepsToRa/StepsToDec read it back off the Dec encoder. The first
    // cut ported only the reporting half, so every target took the straight solution: an eastern
    // goto landed counterweight-up, and a meridian "flip" re-slewed to identical steps.

    /// <summary>
    /// True when <paramref name="decSteps"/> is in the through-the-pole half: the Dec axis has been
    /// turned the negative way from home (GSS <c>SideOfPier</c>: app-space <c>|Y| &lt; 90.0000000001</c>
    /// is Normal, so home itself is Normal). Taken on the angle folded into one turn, so a Dec axis
    /// wound more than half a turn the positive way is read as the mirror it physically is.
    /// </summary>
    private bool IsThroughThePole(int decSteps) => _cprDec > 0 && FoldedDecAxisDegrees(decSteps) < 0.0;

    /// <summary>The Dec axis angle from home in (-180, 180], positive = the straight half.</summary>
    private double FoldedDecAxisDegrees(int decSteps) => FoldDegrees((double)decSteps / _cprDec * 360.0);

    /// <summary>Folds an angle into (-180, 180].</summary>
    private static double FoldDegrees(double degrees)
    {
        degrees = ((degrees + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        return degrees == -180.0 ? 180.0 : degrees;
    }

    /// <summary>
    /// Convert encoder steps to RA hours.
    /// Home position (steps=0) corresponds to HA=6h (counterweight-down, scope at pole),
    /// matching the GSServer GermanPolar convention where HomeAxisX=90°.
    /// North: HA = steps / CPR * 24 + 6; south: HA = 6 - steps / CPR * 24 (the axis
    /// mapping mirrors below the equator, GSS Axes.AxesAppToMount a[0] = 180 - a[0],
    /// matching the physically reversed tracking direction). Through the pole the same RA axis
    /// angle is looking 12 h away, so the Dec encoder decides which (GSS Axes.AxesXyToRaDec:
    /// <c>if (Y &gt; 90) RA += 180</c>). Then RA = LST - HA.
    /// </summary>
    private double StepsToRa(int raSteps, int decSteps)
    {
        if (_cprRa == 0) return 0.0;
        var transform = new Transform(TimeProvider);
        transform.RefreshDateTimeFromTimeProvider();
        transform.SiteLongitude = double.IsNaN(_siteLongitude) ? 0.0 : _siteLongitude;
        var lst = transform.LocalSiderealTime;
        var axisHours = (double)raSteps / _cprRa * 24.0;
        var ha = IsSouthernHemisphere ? 6.0 - axisHours : 6.0 + axisHours;
        if (IsThroughThePole(decSteps))
        {
            ha -= 12.0;
        }
        var ra = CoordinateUtils.ConditionRA(lst - ha);
        return ra;
    }

    /// <summary>
    /// Convert encoder steps to declination degrees.
    /// Home position (steps=0) corresponds to the site's celestial pole
    /// (counterweight-down), matching the GSServer GermanPolar convention where
    /// HomeAxisY=90°. North: Dec = 90 - steps / CPR * 360; south the mapping
    /// mirrors from the -90 pole: Dec = -90 + steps / CPR * 360. The mirror keeps
    /// the straight solution in positive step space in both hemispheres, so the pier-side rule in
    /// <see cref="GetSideOfPierAsync"/> needs no hemisphere branch; the through-the-pole solution
    /// is the same angle the negative way, so the magnitude is what carries the declination.
    /// </summary>
    private double StepsToDec(int steps)
    {
        if (_cprDec == 0) return 0.0;
        var axisDegrees = Math.Abs(FoldedDecAxisDegrees(steps));
        return IsSouthernHemisphere ? -90.0 + axisDegrees : 90.0 - axisDegrees;
    }

    /// <summary>
    /// Convert a sky position to encoder steps in the given axis solution. Inverse of
    /// <see cref="StepsToRa"/> / <see cref="StepsToDec"/>: the straight solution is
    /// steps = (HA - 6) / 24 * CPR and (90 - dec) / 360 * CPR north ((6 - HA) and (dec + 90) south);
    /// through the pole the RA axis is 12 h further round and the Dec axis is the same angle the
    /// negative way from home (GSS Axes.RaDecToAxesXy: <c>X += 180; Y = 180 - Y</c>).
    /// <paramref name="pointingState"/> is a CHOICE and is made by the caller -- a goto from the
    /// target's hour angle, a sync from the half the mount is already in -- because the two callers
    /// want different answers and nothing here can tell them apart.
    /// </summary>
    private (int RaSteps, int DecSteps) SkyToSteps(double ra, double dec, PointingState pointingState)
    {
        if (_cprRa == 0 || _cprDec == 0) return (0, 0);
        var throughThePole = pointingState is PointingState.ThroughThePole;

        var transform = new Transform(TimeProvider);
        transform.RefreshDateTimeFromTimeProvider();
        transform.SiteLongitude = double.IsNaN(_siteLongitude) ? 0.0 : _siteLongitude;
        var lst = transform.LocalSiderealTime;
        var ha = CoordinateUtils.ConditionHA(lst - ra);
        if (throughThePole)
        {
            ha += 12.0;
        }
        var axisHours = IsSouthernHemisphere ? 6.0 - ha : ha - 6.0;
        var raSteps = (int)Math.Round(axisHours / 24.0 * _cprRa);

        var axisDegrees = IsSouthernHemisphere ? dec + 90.0 : 90.0 - dec;
        if (throughThePole)
        {
            axisDegrees = -axisDegrees;
        }
        var decSteps = (int)Math.Round(axisDegrees / 360.0 * _cprDec);

        return (raSteps, decSteps);
    }

    #endregion
}
