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
    private bool _isTracking;
    private bool _isParked;

    // Guide state
    private int _guideSpeedIndex = 2; // default 0.5x sidereal

    // Site
    private double _siteLatitude = double.NaN;
    private double _siteLongitude = double.NaN;
    private double _siteElevation = double.NaN;

    // Snap port
    private volatile bool _snapActive;
    internal bool IsSnapActive => _snapActive;

    #region Capabilities

    public bool CanSetTracking => true;
    public bool CanSetSideOfPier => false;
    public bool CanPulseGuide => true;
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
        => ValueTask.FromResult(AlignmentMode.GermanPolar);

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
            if (_cprRa == 0 || _tmrFreq == 0)
            {
                return;
            }

            // Set tracking mode: slow speed, forward direction, tracking
            await SendCommandAsync('G', '1', "030", cancellationToken); // tracking, forward, low speed
            // Compute sidereal rate T1 preset
            var siderealDegPerSec = SIDEREAL_RATE / 3600.0;
            var t1 = SkywatcherProtocol.ComputeT1Preset(_tmrFreq, _cprRa, siderealDegPerSec, false, _highSpeedRatio);
            await SendCommandAsync('I', '1', SkywatcherProtocol.EncodeUInt24(t1), cancellationToken);
            await SendCommandAsync('J', '1', null, cancellationToken);
            _isTracking = true;
        }
        else
        {
            await SendCommandAsync('K', '1', null, cancellationToken); // decelerate stop RA
            _isTracking = false;
        }
    }

    #endregion

    #region Position

    public async ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync('j', '1', null, cancellationToken);
        if (SkywatcherProtocol.TryParseResponse(response, out var data) && data.Length >= 6)
        {
            _posRa = SkywatcherProtocol.DecodePosition(data.AsSpan(0, 6));
        }
        return StepsToRa(_posRa);
    }

    public async ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken)
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

    #endregion

    #region Slew

    public async ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken)
    {
        var statusRa = await QueryAxisStatusAsync('1', cancellationToken);
        var statusDec = await QueryAxisStatusAsync('2', cancellationToken);
        _isSlewingRa = statusRa.IsRunning && !statusRa.IsTracking;
        _isSlewingDec = statusDec.IsRunning && !statusDec.IsTracking;
        var result = _isSlewingRa || _isSlewingDec;
        if (result)
        {
            Logger.LogDebug("IsSlewingAsync=true: RA(running={RaRun},tracking={RaTrk}) Dec(running={DecRun},tracking={DecTrk})",
                statusRa.IsRunning, statusRa.IsTracking, statusDec.IsRunning, statusDec.IsTracking);
        }
        return result;
    }

    public async ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        if (_cprRa == 0 || _cprDec == 0)
        {
            throw new InvalidOperationException("Mount not initialized");
        }

        // Stop tracking first
        await SendCommandAsync('K', '1', null, cancellationToken);
        await SendCommandAsync('K', '2', null, cancellationToken);

        var targetRaSteps = RaToSteps(ra);
        var targetDecSteps = DecToSteps(dec);

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
            return;
        }

        var direction = delta > 0 ? '0' : '1'; // 0=forward, 1=reverse
        var absDelta = Math.Abs(delta);

        // Use high-speed goto mode
        await SendCommandAsync('G', axis, $"00{direction}", cancellationToken); // goto, high speed, direction
        await SendCommandAsync('H', axis, SkywatcherProtocol.EncodeUInt24((uint)absDelta), cancellationToken); // step count
        await SendCommandAsync('J', axis, null, cancellationToken); // start
    }

    public async ValueTask AbortSlewAsync(CancellationToken cancellationToken)
    {
        // Instant stop both axes
        await SendCommandAsync('L', '1', null, cancellationToken);
        await SendCommandAsync('L', '2', null, cancellationToken);
        _isSlewingRa = false;
        _isSlewingDec = false;
    }

    public async ValueTask SyncRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        var raSteps = RaToSteps(ra);
        var decSteps = DecToSteps(dec);

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

        var direction = rate > 0 ? '0' : '1';
        var absRate = Math.Abs(rate);

        // Determine if high speed
        var siderealDegPerSec = SIDEREAL_RATE / 3600.0;
        var highSpeed = absRate > siderealDegPerSec * 2; // high speed above 2x sidereal

        // Set motion mode: slewing, direction, speed tier
        var speedChar = highSpeed ? '0' : '1'; // 0=high speed, 1=low speed
        await SendCommandAsync('G', axisChar, $"01{direction}", cancellationToken); // slew mode
        var cpr = axisChar == '1' ? _cprRa : _cprDec;
        var t1 = SkywatcherProtocol.ComputeT1Preset(_tmrFreq, cpr, absRate, highSpeed, _highSpeedRatio);
        await SendCommandAsync('I', axisChar, SkywatcherProtocol.EncodeUInt24(t1), cancellationToken);
        await SendCommandAsync('J', axisChar, null, cancellationToken);
    }

    #endregion

    #region Pulse Guide

    public async ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
    {
        var (axisChar, isForward) = direction switch
        {
            GuideDirection.East => ('1', false),  // RA reverse
            GuideDirection.West => ('1', true),   // RA forward
            GuideDirection.North => ('2', true),  // Dec forward
            GuideDirection.South => ('2', false), // Dec reverse
            _ => throw new ArgumentException($"Unknown guide direction {direction}", nameof(direction))
        };

        var dirChar = isForward ? '0' : '1';
        var guideFraction = SkywatcherProtocol.GuideSpeedFraction(_guideSpeedIndex);
        var siderealDegPerSec = SIDEREAL_RATE / 3600.0;
        var guideSpeed = siderealDegPerSec * guideFraction;
        var cpr = axisChar == '1' ? _cprRa : _cprDec;

        // Set tracking mode with guide speed
        await SendCommandAsync('G', axisChar, $"03{dirChar}", cancellationToken); // tracking, low speed, direction
        var t1 = SkywatcherProtocol.ComputeT1Preset(_tmrFreq, cpr, guideSpeed, false, _highSpeedRatio);
        await SendCommandAsync('I', axisChar, SkywatcherProtocol.EncodeUInt24(t1), cancellationToken);
        await SendCommandAsync('J', axisChar, null, cancellationToken);

        // Wait for duration
        await TimeProvider.SleepAsync(duration, cancellationToken);

        // Stop guide axis (restore tracking on RA, stop on Dec)
        await SendCommandAsync('K', axisChar, null, cancellationToken);
        if (axisChar == '1' && _isTracking)
        {
            // Restart sidereal tracking on RA
            await SetTrackingAsync(true, cancellationToken);
        }
    }

    public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(false);

    #endregion

    #region Guide Rates

    public ValueTask<double> GetGuideRateRightAscensionAsync(CancellationToken cancellationToken)
    {
        var fraction = SkywatcherProtocol.GuideSpeedFraction(_guideSpeedIndex);
        return ValueTask.FromResult(fraction * SIDEREAL_RATE / 3600.0);
    }

    public async ValueTask SetGuideRateRightAscensionAsync(double value, CancellationToken cancellationToken)
    {
        // Map to nearest guide speed index (0-4)
        var siderealDegPerSec = SIDEREAL_RATE / 3600.0;
        var fraction = value / siderealDegPerSec;
        _guideSpeedIndex = fraction switch
        {
            >= 0.875 => 0,   // 1.0x
            >= 0.625 => 1,   // 0.75x
            >= 0.375 => 2,   // 0.5x
            >= 0.1875 => 3,  // 0.25x
            _ => 4            // 0.125x
        };
        // Send :P to set the ST-4 autoguide port speed on the mount hardware
        await SendCommandAsync('P', '1', _guideSpeedIndex.ToString(), cancellationToken);
    }

    public ValueTask<double> GetGuideRateDeclinationAsync(CancellationToken cancellationToken)
        => GetGuideRateRightAscensionAsync(cancellationToken); // same guide speed for both axes

    public async ValueTask SetGuideRateDeclinationAsync(double value, CancellationToken cancellationToken)
    {
        await SetGuideRateRightAscensionAsync(value, cancellationToken);
        // RA setter already updates _guideSpeedIndex and sends :P to axis 1;
        // also send to axis 2 for the Dec ST-4 port
        await SendCommandAsync('P', '2', _guideSpeedIndex.ToString(), cancellationToken);
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

    public async ValueTask<PointingState> GetSideOfPierAsync(CancellationToken cancellationToken)
    {
        // Pier side is determined from the Dec encoder position (the physical orientation of
        // the telescope), not from HA (which only tells you where the target is in the sky).
        // Following GSServer GermanPolar convention:
        //   Raw mount Dec axis = pos / CPR * 360 + 90  (home encoder 0x800000 = 90°)
        //   App-space = 180 - raw
        //   Normal (counterweight down) when |app-space| < 90, i.e., 0 < raw < 180
        //   This simplifies to: 0 < pos < CPR/2  →  Normal
        var response = await SendAndReceiveAsync('j', '2', null, cancellationToken);
        if (SkywatcherProtocol.TryParseResponse(response, out var data) && data.Length >= 6 && _cprDec > 0)
        {
            var pos = SkywatcherProtocol.DecodePosition(data.AsSpan(0, 6));
            return pos > 0 && pos < _cprDec / 2 ? PointingState.Normal : PointingState.ThroughThePole;
        }
        return PointingState.Normal;
    }

    public ValueTask SetSideOfPierAsync(PointingState pointingState, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Skywatcher does not support setting side of pier directly");

    public ValueTask<PointingState> DestinationSideOfPierAsync(double ra, double dec, CancellationToken cancellationToken)
    {
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
    {
        if (double.IsNaN(_siteLatitude))
        {
            if (double.TryParse(_device.Query.QueryValue(DeviceQueryKey.Latitude), out var lat))
            {
                _siteLatitude = lat;
            }
        }
        return ValueTask.FromResult(_siteLatitude);
    }

    public ValueTask SetSiteLatitudeAsync(double latitude, CancellationToken cancellationToken)
    {
        _siteLatitude = latitude;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> GetSiteLongitudeAsync(CancellationToken cancellationToken)
    {
        if (double.IsNaN(_siteLongitude))
        {
            if (double.TryParse(_device.Query.QueryValue(DeviceQueryKey.Longitude), out var lon))
            {
                _siteLongitude = lon;
            }
        }
        return ValueTask.FromResult(_siteLongitude);
    }

    public ValueTask SetSiteLongitudeAsync(double longitude, CancellationToken cancellationToken)
    {
        _siteLongitude = longitude;
        return ValueTask.CompletedTask;
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

    protected override Task<(bool Success, int ConnectionId, SkywatcherDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        ISerialConnection? serialDevice;
        try
        {
            if (_device.ConnectSerialDevice(External, encoding: _encoding, logger: Logger) is { IsOpen: true } openedConnection)
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
            return Task.FromResult((true, CONNECTION_ID_EXCLUSIVE, new SkywatcherDeviceInfo(serialDevice)));
        }
        else
        {
            return Task.FromResult((false, CONNECTION_ID_UNKNOWN, default(SkywatcherDeviceInfo)));
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

            // Set ST-4 autoguide port speed to match our guide speed index
            await SendCommandAsync('P', '1', _guideSpeedIndex.ToString(), cancellationToken);
            await SendCommandAsync('P', '2', _guideSpeedIndex.ToString(), cancellationToken);

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

    private async ValueTask SendCommandAsync(char cmd, char axis, string? data, CancellationToken cancellationToken)
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
        // Read and discard acknowledgment
        await port.TryReadTerminatedAsync(CrTerminator, cancellationToken);
    }

    private record struct AxisStatus(bool IsRunning, bool IsTracking, bool IsInitDone);

    private async ValueTask<AxisStatus> QueryAxisStatusAsync(char axis, CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync('f', axis, null, cancellationToken);
        if (SkywatcherProtocol.TryParseResponse(response, out var data) && data.Length >= 6)
        {
            // Status bytes: 3 bytes (6 hex chars)
            // Byte 0: bit0=running, bit1=blocked
            // Byte 1: bit0=init done
            // Byte 2: bit0=level (0=tracking/1=slewing)
            var raw = SkywatcherProtocol.DecodeUInt24(data.AsSpan(0, 6));
            var byte0 = (int)(raw & 0xFF);
            var byte1 = (int)((raw >> 8) & 0xFF);
            var byte2 = (int)((raw >> 16) & 0xFF);
            var isRunning = (byte0 & 0x01) != 0;
            var isInitDone = (byte1 & 0x01) != 0;
            var isTracking = (byte2 & 0x01) == 0; // 0=tracking rate, 1=slewing rate
            return new AxisStatus(isRunning, isTracking, isInitDone);
        }
        return new AxisStatus(false, false, false);
    }

    #endregion

    #region Coordinate Conversion

    /// <summary>
    /// Convert encoder steps to RA hours.
    /// Home position (steps=0) corresponds to HA=6h (counterweight-down, scope at pole),
    /// matching the GSServer GermanPolar convention where HomeAxisX=90°.
    /// HA = steps / CPR * 24 + 6, then RA = LST - HA.
    /// </summary>
    private double StepsToRa(int steps)
    {
        if (_cprRa == 0) return 0.0;
        var transform = new Transform(TimeProvider);
        transform.RefreshDateTimeFromTimeProvider();
        transform.SiteLongitude = double.IsNaN(_siteLongitude) ? 0.0 : _siteLongitude;
        var lst = transform.LocalSiderealTime;
        var ha = (double)steps / _cprRa * 24.0 + 6.0;
        var ra = CoordinateUtils.ConditionRA(lst - ha);
        return ra;
    }

    /// <summary>
    /// Convert RA hours to encoder steps.
    /// Inverse of <see cref="StepsToRa"/>: steps = (HA - 6) / 24 * CPR.
    /// </summary>
    private int RaToSteps(double ra)
    {
        if (_cprRa == 0) return 0;
        var transform = new Transform(TimeProvider);
        transform.RefreshDateTimeFromTimeProvider();
        transform.SiteLongitude = double.IsNaN(_siteLongitude) ? 0.0 : _siteLongitude;
        var lst = transform.LocalSiderealTime;
        var ha = CoordinateUtils.ConditionHA(lst - ra);
        return (int)Math.Round((ha - 6.0) / 24.0 * _cprRa);
    }

    /// <summary>
    /// Convert encoder steps to declination degrees.
    /// Home position (steps=0) corresponds to Dec=90° (pole, counterweight-down),
    /// matching the GSServer GermanPolar convention where HomeAxisY=90°.
    /// Dec = 90 - steps / CPR * 360.
    /// </summary>
    private double StepsToDec(int steps)
    {
        if (_cprDec == 0) return 0.0;
        return 90.0 - (double)steps / _cprDec * 360.0;
    }

    /// <summary>
    /// Convert declination degrees to encoder steps.
    /// Inverse of <see cref="StepsToDec"/>: steps = (90 - dec) / 360 * CPR.
    /// </summary>
    private int DecToSteps(double dec)
    {
        if (_cprDec == 0) return 0;
        return (int)Math.Round((90.0 - dec) / 360.0 * _cprDec);
    }

    #endregion
}
