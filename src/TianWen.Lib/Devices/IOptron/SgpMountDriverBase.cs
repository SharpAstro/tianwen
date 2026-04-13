using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Connections;
using static TianWen.Lib.Astrometry.Constants;

namespace TianWen.Lib.Devices.IOptron;

internal record struct SgpDeviceInfo(ISerialConnection SerialDevice);

/// <summary>
/// Abstract base driver for the iOptron SkyGuider Pro (SGP).
/// RA-only single-axis equatorial tracker with custom serial protocol at 28800 baud.
/// </summary>
internal abstract partial class SgpMountDriverBase<TDevice>(TDevice device, IServiceProvider serviceProvider)
    : DeviceDriverBase<TDevice, SgpDeviceInfo>(device, serviceProvider), IMountDriver
    where TDevice : DeviceBase
{
    internal const int SGP_BAUD_RATE = IOptronDevice.SGP_BAUD_RATE;

    private static readonly Encoding _encoding = Encoding.ASCII;
    private static readonly ReadOnlyMemory<byte> HashTerminator = "#"u8.ToArray();

    private string _firmwareVersion = "Unknown";
    private bool _isNorthernHemisphere = true;
    private TrackingSpeed _trackingSpeed = TrackingSpeed.Sidereal;
    private int _slewSpeedIndex = 1; // 1-7
    private int _guideRateRA = 50; // 0-99, default 50 = 0.50x sidereal

    // RA-only mount: Dec is fixed at ±90 (pole) based on hemisphere
    // These can be updated via sync
    private double _dec = 90.0;
    private double _targetDec = 90.0;

    // RA is estimated from sidereal tracking start
    private double _ra = double.NaN;
    private double _targetRa = double.NaN;
    private long _trackingStartTicks;
    private double _raAtTrackingStart = double.NaN;
    private volatile bool _isMoving; // RA axis moving via MoveAxis

    // Camera snap settings
    private CameraSnapSettings? _cameraSnapSettings;

    #region Capabilities

    public bool CanSetTracking => false;
    public bool CanSetSideOfPier => false;
    public bool CanPulseGuide => false; // SGP guiding requires ST-4 port, not serial commands
    public bool CanSetRightAscensionRate => false;
    public bool CanSetDeclinationRate => false;
    public bool CanSetGuideRates => true; // via :MSGR command
    public bool CanPark => false;
    public bool CanSetPark => false;
    public bool CanUnpark => false;
    public bool CanSlew => false; // no goto
    public bool CanSlewAsync => false; // no goto
    public bool CanSync => true;
    public bool CanCameraSnap => true;

    public bool CanMoveAxis(TelescopeAxis axis) => axis == TelescopeAxis.Primary;

    /// <summary>
    /// SGP base guide rate in degrees/second (1x speed = sidereal rate).
    /// </summary>
    private const double SGP_GUIDE_RATE_DEG_PER_SEC = SIDEREAL_RATE / 3600.0;

    /// <summary>
    /// Speed multipliers for slew speed indices 1-7.
    /// </summary>
    private static readonly int[] _speedMultipliers = [1, 2, 8, 16, 64, 128, 144];

    private static readonly AxisRate[] _raAxisRates = Array.ConvertAll(
        _speedMultipliers, m => new AxisRate(SGP_GUIDE_RATE_DEG_PER_SEC * m));

    public IReadOnlyList<AxisRate> AxisRates(TelescopeAxis axis) => axis == TelescopeAxis.Primary
        ? _raAxisRates
        : [];

    #endregion

    #region Tracking

    public IReadOnlyList<TrackingSpeed> TrackingSpeeds => [TrackingSpeed.Sidereal, TrackingSpeed.Lunar, TrackingSpeed.Solar];

    public EquatorialCoordinateType EquatorialSystem => EquatorialCoordinateType.Topocentric;

    public ValueTask<AlignmentMode> GetAlignmentAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(AlignmentMode.GermanPolar);

    public ValueTask<TrackingSpeed> GetTrackingSpeedAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_trackingSpeed);

    public async ValueTask SetTrackingSpeedAsync(TrackingSpeed value, CancellationToken cancellationToken)
    {
        var cmd = value switch
        {
            TrackingSpeed.Solar => ":MSTR0#",
            TrackingSpeed.Lunar => ":MSTR1#",
            TrackingSpeed.Sidereal => ":MSTR3#",
            _ => throw new ArgumentException($"Tracking speed {value} is not supported by SGP", nameof(value))
        };

        await SendCommandAsync(cmd, cancellationToken);
        _trackingSpeed = value;
    }

    public async ValueTask<bool> IsTrackingAsync(CancellationToken cancellationToken)
    {
        var status = await GetMountStatusAsync(cancellationToken);
        return status.TrackingRate >= 0;
    }

    public async ValueTask SetTrackingAsync(bool tracking, CancellationToken cancellationToken)
    {
        if (tracking)
        {
            await SetTrackingSpeedAsync(_trackingSpeed, cancellationToken);
            _trackingStartTicks = TimeProvider.GetTimestamp();
            _raAtTrackingStart = _ra;
        }
        else
        {
            // SGP doesn't have an explicit tracking-off command;
            // stopping movement resumes tracking. This is a no-op.
        }
    }

    #endregion

    #region Position

    public ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(double.IsNaN(_ra) ? 0.0 : _ra);

    public ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_dec);

    public ValueTask<double> GetTargetRightAscensionAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(double.IsNaN(_targetRa) ? 0.0 : _targetRa);

    public ValueTask<double> GetTargetDeclinationAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_targetDec);

    public ValueTask<double> GetSiderealTimeAsync(CancellationToken cancellationToken)
    {
        var transform = new Transform(TimeProvider);
        transform.RefreshDateTimeFromTimeProvider();
        transform.SiteLongitude = _siteLongitude;
        return ValueTask.FromResult(transform.LocalSiderealTime);
    }

    public async ValueTask<double> GetHourAngleAsync(CancellationToken cancellationToken)
    {
        var lst = await GetSiderealTimeAsync(cancellationToken);
        var ra = await GetRightAscensionAsync(cancellationToken);
        return CoordinateUtils.ConditionHA(lst - ra);
    }

    #endregion

    #region Slew & Move Axis

    public ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_isMoving);

    public async ValueTask MoveAxisAsync(TelescopeAxis axis, double rate, CancellationToken cancellationToken)
    {
        if (axis != TelescopeAxis.Primary)
        {
            throw new InvalidOperationException("SGP only supports RA (Primary) axis movement");
        }

        if (rate == 0.0)
        {
            await SendCommandAsync(":MSMR2#", cancellationToken);
            _isMoving = false;
            return;
        }

        // Map rate (deg/sec) to nearest slew speed index by finding closest multiplier
        var absRate = Math.Abs(rate);
        var speedIndex = 1;
        for (var i = 0; i < _speedMultipliers.Length; i++)
        {
            if (absRate >= SGP_GUIDE_RATE_DEG_PER_SEC * _speedMultipliers[i])
            {
                speedIndex = i + 1;
            }
        }

        // Direction: positive = west, negative = east
        var dirCmd = rate > 0 ? ":MSMR1#" : ":MSMR0#";

        // Set speed + start move atomically (single lock)
        if (_deviceInfo.SerialDevice is { IsOpen: true } port)
        {
            using var @lock = await port.WaitAsync(cancellationToken);
            await WriteAsync(port, $":MSMS{speedIndex}#", cancellationToken);
            await port.TryReadTerminatedAsync(HashTerminator, cancellationToken);
            await WriteAsync(port, dirCmd, cancellationToken);
            await port.TryReadTerminatedAsync(HashTerminator, cancellationToken);
        }

        _slewSpeedIndex = speedIndex;
        _isMoving = true;
    }

    public ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
        => throw new InvalidOperationException("SGP does not support goto slewing");

    public async ValueTask AbortSlewAsync(CancellationToken cancellationToken)
    {
        await SendCommandAsync(":MSMR2#", cancellationToken);
        _isMoving = false;
    }

    public ValueTask SyncRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        // SGP has no sync command — we update our internal model
        _ra = ra;
        _dec = dec;
        _raAtTrackingStart = ra;
        _trackingStartTicks = TimeProvider.GetTimestamp();
        return ValueTask.CompletedTask;
    }

    #endregion

    #region Pulse Guide (not supported — use ST-4 guide port)

    public ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
        => throw new InvalidOperationException("SGP does not support serial pulse guiding. Use the ST-4 guide port instead.");

    public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(false);

    #endregion

    #region Guide Rates

    public ValueTask<double> GetGuideRateRightAscensionAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(_guideRateRA / 100.0 * SIDEREAL_RATE / 3600.0);

    public async ValueTask SetGuideRateRightAscensionAsync(double value, CancellationToken cancellationToken)
    {
        var percentage = (int)Math.Round(value / (SIDEREAL_RATE / 3600.0) * 100.0);
        if (percentage is < 0 or > 99)
        {
            throw new ArgumentException($"Guide rate {value} out of range (0-99% of sidereal)", nameof(value));
        }

        await SendCommandAsync($":MSGR{percentage:D2}50#", cancellationToken);
        _guideRateRA = percentage;
    }

    public ValueTask<double> GetGuideRateDeclinationAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(0.0);

    public ValueTask SetGuideRateDeclinationAsync(double value, CancellationToken cancellationToken)
        => throw new InvalidOperationException("SGP does not support Dec guide rate");

    #endregion

    #region Rates (not supported)

    public ValueTask<double> GetRightAscensionRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(0.0);

    public ValueTask SetRightAscensionRateAsync(double value, CancellationToken cancellationToken)
        => throw new InvalidOperationException("SGP does not support setting RA rate offset");

    public ValueTask<double> GetDeclinationRateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(0.0);

    public ValueTask SetDeclinationRateAsync(double value, CancellationToken cancellationToken)
        => throw new InvalidOperationException("SGP does not support setting Dec rate offset");

    #endregion

    #region Pier Side

    public ValueTask<PointingState> GetSideOfPierAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(PointingState.Normal);

    public ValueTask SetSideOfPierAsync(PointingState pointingState, CancellationToken cancellationToken)
        => throw new InvalidOperationException("SGP does not support setting side of pier");

    public ValueTask<PointingState> DestinationSideOfPierAsync(double ra, double dec, CancellationToken cancellationToken)
        => ValueTask.FromResult(PointingState.Normal);

    #endregion

    #region Site

    private double _siteLatitude = double.NaN;
    private double _siteLongitude = double.NaN;
    private double _siteElevation = double.NaN;

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

    #region Hemisphere

    public async ValueTask SetHemisphereAsync(bool isNorth, CancellationToken cancellationToken)
    {
        await SendCommandAsync(isNorth ? ":MSHE1#" : ":MSHE0#", cancellationToken);
        _isNorthernHemisphere = isNorth;
        _dec = isNorth ? 90.0 : -90.0;
    }

    #endregion

    #region Time

    public bool TimeIsSetByUs => true;

    public ValueTask<DateTime?> TryGetUTCDateFromMountAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<DateTime?>(TimeProvider.GetUtcNow().UtcDateTime);

    public ValueTask SetUTCDateAsync(DateTime dateTime, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    #endregion

    #region Park (not supported)

    public ValueTask<bool> AtHomeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
    public ValueTask<bool> AtParkAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
    public ValueTask ParkAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("SGP does not support parking");
    public ValueTask UnparkAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("SGP does not support unparking");

    #endregion

    #region Camera Snap

    /// <summary>
    /// Triggers camera snap. MSCA format: :MSCA{y}{aaaa}{bbb}{ccc}#
    /// where y=1 (start flag), aaaa=shutter, bbb=interval, ccc=shot count.
    /// Units for shutter/interval are TBD (possibly seconds or 0.1s).
    /// Example: :MSCA10030005002# = shutter 0030, interval 005, 002 shots.
    /// </summary>
    public async ValueTask CameraSnapAsync(CameraSnapSettings settings, CancellationToken cancellationToken)
    {
        // TODO: confirm units — using seconds for now based on example (0030 = 30s shutter, 005 = 5s interval)
        var shutter = (int)settings.ShutterTime.TotalSeconds;
        var interval = (int)settings.Interval.TotalSeconds;
        var cmd = $":MSCA1{shutter:D4}{interval:D3}{settings.ShotCount:D3}#";

        await SendCommandAsync(cmd, cancellationToken);
        _cameraSnapSettings = settings;
    }

    /// <summary>
    /// Parses MGCS response: :HRCSxyaaaabbbcccdddppkkkkk#
    /// x=unknown, y=flag, aaaa=shutter, bbb=interval, ccc=shot count,
    /// ddd/pp/kkkkk=unknown (not sent in MSCA).
    /// </summary>
    public async ValueTask<CameraSnapSettings?> GetCameraSnapSettingsAsync(CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync(":MGCS#", cancellationToken);
        if (response is not null && CameraSettingsRegex().IsMatch(response))
        {
            var match = CameraSettingsRegex().Match(response);
            // TODO: confirm units — using seconds for now
            var shutterTime = TimeSpan.FromSeconds(int.Parse(match.Groups["Shutter"].Value, CultureInfo.InvariantCulture));
            var interval = TimeSpan.FromSeconds(int.Parse(match.Groups["Interval"].Value, CultureInfo.InvariantCulture));
            var shotCount = int.Parse(match.Groups["ShotCount"].Value, CultureInfo.InvariantCulture);
            return new CameraSnapSettings(shutterTime, interval, shotCount);
        }

        return _cameraSnapSettings;
    }

    /// <summary>
    /// Matches :HRCSxyaaaabbbcccdddppkkkkk
    /// Example: :HRCS0100300050020000000000
    /// </summary>
    [GeneratedRegex(@"^:HRCS\d\d(?<Shutter>\d{4})(?<Interval>\d{3})(?<ShotCount>\d{3})\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex CameraSettingsRegex();

    #endregion

    #region Driver Info

    public override string? DriverInfo => $"iOptron SkyGuider Pro (FW {_firmwareVersion})";
    public override string? Description => "iOptron SkyGuider Pro RA-only equatorial tracker";

    #endregion

    #region Connection

    protected override Task<(bool Success, int ConnectionId, SgpDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        ISerialConnection? serialDevice;
        try
        {
            if (_device.ConnectSerialDevice(External, Logger, TimeProvider, SGP_BAUD_RATE, _encoding) is { IsOpen: true } openedConnection)
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
            return Task.FromResult((true, CONNECTION_ID_EXCLUSIVE, new SgpDeviceInfo(serialDevice)));
        }
        else
        {
            return Task.FromResult((false, CONNECTION_ID_UNKNOWN, default(SgpDeviceInfo)));
        }
    }

    protected override async ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var fwResponse = await SendAndReceiveAsync(":MRSVE#", cancellationToken);
            if (fwResponse is not null && SgpFirmwareRegex().IsMatch(fwResponse))
            {
                _firmwareVersion = SgpFirmwareRegex().Match(fwResponse).Groups[1].Value;
            }

            var status = await GetMountStatusAsync(cancellationToken);
            _isNorthernHemisphere = status.IsNorthern;
            _dec = _isNorthernHemisphere ? 90.0 : -90.0;
            _targetDec = _dec;

            _trackingSpeed = status.TrackingRate switch
            {
                0 => TrackingSpeed.Solar,
                1 => TrackingSpeed.Lunar,
                3 => TrackingSpeed.Sidereal,
                _ => TrackingSpeed.Sidereal
            };

            await GetSiteLatitudeAsync(cancellationToken);
            await GetSiteLongitudeAsync(cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize SGP mount");
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

    private async ValueTask<string?> SendAndReceiveAsync(string command, CancellationToken cancellationToken)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true } port)
        {
            return null;
        }

        using var @lock = await port.WaitAsync(cancellationToken);
        await WriteAsync(port, command, cancellationToken);
        return await port.TryReadTerminatedAsync(HashTerminator, cancellationToken);
    }

    private async ValueTask SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true } port)
        {
            throw new InvalidOperationException("Serial port is not connected");
        }

        using var @lock = await port.WaitAsync(cancellationToken);
        await WriteAsync(port, command, cancellationToken);
        await port.TryReadTerminatedAsync(HashTerminator, cancellationToken);
    }

    private static async ValueTask WriteAsync(ISerialConnection port, string command, CancellationToken cancellationToken)
    {
        if (!await port.TryWriteAsync(command, cancellationToken))
        {
            throw new InvalidOperationException($"Failed to write command {command}");
        }
    }

    private record struct MountStatus(int TrackingRate, int Speed, bool IsNorthern);

    private async ValueTask<MountStatus> GetMountStatusAsync(CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync(":MGAS#", cancellationToken);
        if (response is not null && MountStatusRegex().IsMatch(response))
        {
            var match = MountStatusRegex().Match(response);
            return new MountStatus(
                int.Parse(match.Groups["TrackingRate"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["Speed"].Value, CultureInfo.InvariantCulture),
                match.Groups["Hemisphere"].Value == "1"
            );
        }

        return new MountStatus(-1, 0, _isNorthernHemisphere);
    }

    /// <summary>
    /// Matches :HRAS01{TrackingRate}{Speed}0{Hemisphere}1{Unknown}
    /// Example: :HRAS013501105
    /// </summary>
    [GeneratedRegex(@"^:HRAS01(?<TrackingRate>\d)(?<Speed>\d)0(?<Hemisphere>\d)1\d\d$", RegexOptions.CultureInvariant)]
    private static partial Regex MountStatusRegex();

    [GeneratedRegex(@"^:RMRVE12(\d{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex SgpFirmwareRegex();

    #endregion
}
