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

    /// <summary>
    /// Set once the mount has answered a command with an earlier command's reply (<see cref="SgpWire"/>): from then on each
    /// repeatable command goes twice, so a status read is the mount's state now and not at the previous poll. Never cleared
    /// while connected; when the mount is in step the extra send costs one more reply, about 25 ms.
    /// </summary>
    private volatile bool _outOfStep;

    private string _firmwareVersion = "Unknown";
    private bool _isNorthernHemisphere = true;
    private TrackingSpeed _trackingSpeed = TrackingSpeed.Sidereal;
    private int _slewSpeedIndex = 1; // 1-7
    private int _guideRateRA = 50; // 0-99, read from the mount at connect (50 = 0.50x sidereal is its default)

    /// <summary>The Dec half of the mount's guide rate as the mount holds it, two digits. There is no Dec motor, so it
    /// guides nothing and this driver never chooses it: a change of the RA rate sends it back as it was read (#1483).</summary>
    private string _guideRateDecField = "50";

    // RA-only mount: Dec is fixed at ±90 (pole) based on hemisphere
    // These can be updated via sync
    private double _dec = 90.0;
    private double _targetDec = 90.0;

    // RA is the last sync's and is held: the mount tracks whenever it is powered, so time does not move it. A MOVE does,
    // and nothing here follows that yet (docs/plans/slew-less-mount.md, P3): the session syncs after each solve instead.
    // A tracking-start anchor was written here on every sync and never read (#1483); it is gone.
    private double _ra = double.NaN;
    private double _targetRa = double.NaN;
    private volatile bool _isMoving; // RA axis moving via MoveAxis

    // Camera snap settings
    private CameraSnapSettings? _cameraSnapSettings;

    #region Capabilities

    public bool CanSetTracking => false;
    public bool CanSetSideOfPier => false;
    public bool CanPulseGuide => false; // SGP guiding requires ST-4 port, not serial commands

    /// <summary>False; there is no serial pulse to overlap.</summary>
    public bool CanPulseGuideSimultaneously => false;
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

    /// <summary>
    /// Always true while connected: the SkyGuider Pro has no tracking-off (a stop resumes tracking), so this asks the mount
    /// nothing. It used to read the status and answer false whenever the reply did not parse, which was every read during a
    /// move (the mount answers <c>:HRAS02...</c> while moving) and every stale reply (bench, 2026-11-05).
    /// </summary>
    public ValueTask<bool> IsTrackingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Connected);

    public async ValueTask SetTrackingAsync(bool tracking, CancellationToken cancellationToken)
    {
        if (tracking)
        {
            // A rate this driver has no name for (half-sidereal, read at connect) is not sent back: asking for tracking
            // with no speed chosen means the sidereal rate.
            await SetTrackingSpeedAsync(_trackingSpeed is TrackingSpeed.None ? TrackingSpeed.Sidereal : _trackingSpeed, cancellationToken);
        }
        else
        {
            // The SkyGuider Pro has no tracking-off: its rates are solar, lunar, half-sidereal and sidereal, and the stop
            // of a move resumes tracking. This used to return normally, so a caller that asked for the tracker to stop
            // was told it had while it kept turning (#1483). It refuses, as every member the mount has no command for
            // does. CanSetTracking says so beforehand, and the limit paths ask it and tell the operator to stop the
            // mount by hand.
            throw new InvalidOperationException("The SkyGuider Pro has no tracking-off command: it tracks whenever it is powered. Stop it at the mount.");
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

    /// <summary>
    /// Read from the mount's status (its first field: <c>01</c> at rest, <c>02</c> moving), so it stays true while the RA servo
    /// winds down after a stop (about 1.6 s at full speed, bench 2026-11-05) and never trusts the driver's own record of what
    /// it commanded. The record answers only when the status cannot be read.
    /// </summary>
    public async ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken)
    {
        if (await GetMountStatusAsync(cancellationToken) is { IsValid: true } status)
        {
            _isMoving = status.IsMoving;
        }

        return _isMoving;
    }

    public async ValueTask MoveAxisAsync(TelescopeAxis axis, double rate, CancellationToken cancellationToken)
    {
        if (axis != TelescopeAxis.Primary)
        {
            throw new InvalidOperationException("SGP only supports RA (Primary) axis movement");
        }

        if (rate == 0.0)
        {
            // Not cleared here: the servo is still turning, and IsSlewingAsync asks the mount.
            await SendCommandAsync(":MSMR2#", cancellationToken);
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

        // As every other command on a closed port (SendCommandAsync). It used to send nothing, return normally and
        // record the axis as moving (#1483).
        if (_deviceInfo.SerialDevice is not { IsOpen: true } port)
        {
            throw new InvalidOperationException("Serial port is not connected");
        }

        // Set speed + start move atomically (single lock)
        using (await port.WaitAsync(cancellationToken))
        {
            await ExchangeLockedAsync(port, $":MSMS{speedIndex}#", repeatable: true, cancellationToken);
            await ExchangeLockedAsync(port, dirCmd, repeatable: true, cancellationToken);
        }

        _slewSpeedIndex = speedIndex;
        _isMoving = true;
    }

    public ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
        => throw new InvalidOperationException("SGP does not support goto slewing");

    public async ValueTask AbortSlewAsync(CancellationToken cancellationToken)
        => await SendCommandAsync(":MSMR2#", cancellationToken);

    public ValueTask SyncRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        // SGP has no sync command: we update our internal model
        _ra = ra;
        _dec = dec;
        return ValueTask.CompletedTask;
    }

    #endregion

    #region Pulse Guide (not supported, use ST-4 guide port)

    public ValueTask StartPulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
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

        // The Dec half goes back as the mount held it at connect. It was always sent as 50, whatever the mount had,
        // while GetGuideRateDeclinationAsync answers 0 (#1483).
        await SendCommandAsync($":MSGR{percentage:D2}{_guideRateDecField}#", cancellationToken);
        _guideRateRA = percentage;
    }

    /// <summary>Zero: there is no Dec motor, so a Dec pulse moves nothing whatever Dec rate the mount stores.</summary>
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

    /// <summary>
    /// Computed from the hour angle, as on the LX200 base. The SkyGuider Pro is German equatorial
    /// (<see cref="AlignmentMode.GermanPolar"/>), and a flip on it is the session turning RA a half turn and the operator
    /// swinging Dec over the pole (<c>docs/plans/slew-less-mount.md</c>, the owner's design of 2026-10-11), so its pier
    /// side does change, and only the image can say when (<c>MeridianFlipVerification</c>). It was <c>None</c>, with a
    /// constant <c>Normal</c>, which hid every flip from the session and the guider (#1483).
    /// </summary>
    public PointingStateSource PointingStateSource => PointingStateSource.Computed;

    public async ValueTask<PointingState> GetSideOfPierAsync(CancellationToken cancellationToken)
        => double.IsNaN(_siteLongitude) ? PointingState.Unknown : SideOfPierAt(await GetHourAngleAsync(cancellationToken));

    public ValueTask SetSideOfPierAsync(PointingState pointingState, CancellationToken cancellationToken)
        => throw new InvalidOperationException("SGP does not support setting side of pier");

    public async ValueTask<PointingState> DestinationSideOfPierAsync(double ra, double dec, CancellationToken cancellationToken)
        => double.IsNaN(_siteLongitude)
            ? PointingState.Unknown
            : SideOfPierAt(CoordinateUtils.ConditionHA(await GetSiderealTimeAsync(cancellationToken) - ra));

    /// <summary>West of the meridian is <see cref="PointingState.Normal"/>, east is through the pole, as on the LX200 base.</summary>
    private static PointingState SideOfPierAt(double hourAngle) => hourAngle switch
    {
        >= 0 => PointingState.Normal,
        < 0 => PointingState.ThroughThePole,
        _ => PointingState.Unknown,
    };

    #endregion

    #region Site

    private double _siteLatitude = double.NaN;
    private double _siteLongitude = double.NaN;
    private double _siteElevation = double.NaN;

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

    private const int MaxSnapShutter = 9999;
    private const int MaxSnapField = 999;

    /// <summary>
    /// Triggers camera snap. MSCA format: :MSCA{y}{aaaa}{bbb}{ccc}#
    /// where y=1 (start flag), aaaa=shutter, bbb=interval, ccc=shot count.
    /// Units for shutter/interval are TBD (possibly seconds or 0.1s).
    /// Example: :MSCA10030005002# = shutter 0030, interval 005, 002 shots.
    /// </summary>
    public async ValueTask CameraSnapAsync(CameraSnapSettings settings, CancellationToken cancellationToken)
    {
        // TODO: confirm units; using seconds for now based on example (0030 = 30s shutter, 005 = 5s interval)
        var shutter = (int)settings.ShutterTime.TotalSeconds;
        var interval = (int)settings.Interval.TotalSeconds;
        // The wire has four digits for the shutter and three each for the interval and the count. A larger or a negative
        // value was formatted anyway, into a command of another length that the mount reads as other fields (#1483).
        if (shutter is < 0 or > MaxSnapShutter)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.ShutterTime, $"The SkyGuider Pro's shutter time is 0 to {MaxSnapShutter} s");
        }
        if (interval is < 0 or > MaxSnapField)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.Interval, $"The SkyGuider Pro's interval is 0 to {MaxSnapField} s");
        }
        if (settings.ShotCount is < 0 or > MaxSnapField)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.ShotCount, $"The SkyGuider Pro's shot count is 0 to {MaxSnapField}");
        }
        var cmd = $":MSCA1{shutter:D4}{interval:D3}{settings.ShotCount:D3}#";

        // The one command that is never sent twice: it starts the shutter sequence.
        await SendCommandAsync(cmd, cancellationToken, repeatable: false);
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
            // TODO: confirm units; using seconds for now
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

    protected override async Task<(bool Success, int ConnectionId, SgpDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        ISerialConnection? serialDevice;
        try
        {
            if (await _device.ConnectSerialDeviceAsync(External, Logger, TimeProvider, SGP_BAUD_RATE, _encoding, cancellationToken) is { IsOpen: true } openedConnection)
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
            return (true, CONNECTION_ID_EXCLUSIVE, new SgpDeviceInfo(serialDevice));
        }
        else
        {
            return (false, CONNECTION_ID_UNKNOWN, default(SgpDeviceInfo));
        }
    }

    protected override async ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A connect is all or nothing (#1483). Each of the three reads below used to be taken as it came: a port
            // with no SkyGuider Pro on it connected with firmware "Unknown", and a status that could not be read was a
            // northern mount tracking at the sidereal rate.
            var fwResponse = await SendAndReceiveAsync(":MRSVE#", cancellationToken);
            if (fwResponse is null || SgpFirmwareRegex().Match(fwResponse) is not { Success: true } firmware)
            {
                Logger.LogError("No SkyGuider Pro answered on {DeviceUri}: its firmware query was answered {Reply}",
                    _device.DeviceUri, fwResponse is null ? "with nothing" : $"'{fwResponse}'");
                return false;
            }
            _firmwareVersion = firmware.Groups[1].Value;

            var status = await GetMountStatusAsync(cancellationToken);
            if (!status.IsValid)
            {
                Logger.LogError("The SkyGuider Pro on {DeviceUri} did not report its status, so its hemisphere and tracking rate are not known",
                    _device.DeviceUri);
                return false;
            }
            _isMoving = status.IsMoving;
            _isNorthernHemisphere = status.IsNorthern;
            _dec = _isNorthernHemisphere ? 90.0 : -90.0;
            _targetDec = _dec;

            _trackingSpeed = status.TrackingRate switch
            {
                0 => TrackingSpeed.Solar,
                1 => TrackingSpeed.Lunar,
                3 => TrackingSpeed.Sidereal,
                // Half-sidereal (2) has no name among TrackingSpeed. It was called sidereal, so a session on a mount
                // left at half rate believed it was tracking the stars. It is none of ours until a speed is set.
                _ => TrackingSpeed.None,
            };
            if (_trackingSpeed is TrackingSpeed.None)
            {
                Logger.LogWarning("The SkyGuider Pro on {DeviceUri} tracks at a rate this driver has no name for (status digit {Rate}; 2 is half-sidereal). Set a tracking speed before imaging.",
                    _device.DeviceUri, status.TrackingRate);
            }

            // The guide rates the mount holds: the RA half was assumed to be 50 % until set, and the Dec half was
            // overwritten with 50 by every change of the RA half.
            var rates = await SendAndReceiveAsync(":MGGR#", cancellationToken);
            if (rates is null || GuideRateRegex().Match(rates) is not { Success: true } guide)
            {
                Logger.LogError("The SkyGuider Pro on {DeviceUri} did not report its guide rates: answered {Reply}",
                    _device.DeviceUri, rates is null ? "with nothing" : $"'{rates}'");
                return false;
            }
            _guideRateRA = int.Parse(guide.Groups["Ra"].Value, CultureInfo.InvariantCulture);
            _guideRateDecField = guide.Groups["Dec"].Value;

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
                // Taken so no exchange is under way when the port closes, and NOT given back (#1483's item 8, which
                // read this as a leak): the real connection disposes its semaphore as it closes
                // (SerialConnection.TryCloseAsync), so a release after the close throws ObjectDisposedException. The
                // LX200 and SkyWatcher bases close the same way. What is wrong is the transport's: a caller already
                // waiting for the port when it closes is never woken (#2322).
                await port.WaitAsync(cancellationToken);
                return await port.TryCloseAsync();
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

    /// <summary>The reply to a query, matched to it by <see cref="SgpWire"/>; null when the mount answered nothing usable.</summary>
    private async ValueTask<string?> SendAndReceiveAsync(string command, CancellationToken cancellationToken)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true } port)
        {
            return null;
        }

        using var @lock = await port.WaitAsync(cancellationToken);
        return await ExchangeLockedAsync(port, command, repeatable: true, cancellationToken);
    }

    private async ValueTask SendCommandAsync(string command, CancellationToken cancellationToken, bool repeatable = true)
    {
        if (_deviceInfo.SerialDevice is not { IsOpen: true } port)
        {
            throw new InvalidOperationException("Serial port is not connected");
        }

        using var @lock = await port.WaitAsync(cancellationToken);
        await ExchangeLockedAsync(port, command, repeatable, cancellationToken);
    }

    /// <summary>One exchange through <see cref="SgpWire"/>, the caller holding the port's lock; notes a mount out of step.</summary>
    private async ValueTask<string?> ExchangeLockedAsync(ISerialConnection port, string command, bool repeatable, CancellationToken cancellationToken)
    {
        var (reply, sawStale) = await SgpWire.ExchangeAsync(port, command, repeatable, sendTwice: _outOfStep, Logger, cancellationToken);
        if (sawStale && !_outOfStep)
        {
            _outOfStep = true;
            Logger.LogInformation("SGP answered {Command} with an earlier command's reply; sending each query twice from now on", command);
        }

        return reply;
    }

    private readonly record struct MountStatus(bool IsValid, bool IsMoving, int TrackingRate, int Speed, bool IsNorthern);

    private async ValueTask<MountStatus> GetMountStatusAsync(CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync(":MGAS#", cancellationToken);
        if (response is not null && MountStatusRegex().Match(response) is { Success: true } match)
        {
            return new MountStatus(
                IsValid: true,
                IsMoving: match.Groups["Motion"].Value == "2",
                int.Parse(match.Groups["TrackingRate"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["Speed"].Value, CultureInfo.InvariantCulture),
                match.Groups["Hemisphere"].Value == "1"
            );
        }

        return new MountStatus(IsValid: false, IsMoving: false, -1, 0, _isNorthernHemisphere);
    }

    /// <summary>
    /// Matches :HRAS0{Motion}{TrackingRate}{Speed}0{Hemisphere}1{Unknown}, Motion 1 at rest and 2 moving.
    /// Examples (bench, 2026-11-05): :HRAS013900105 at rest, :HRAS023900105 moving.
    /// </summary>
    [GeneratedRegex(@"^:HRAS0(?<Motion>[12])(?<TrackingRate>\d)(?<Speed>\d)0(?<Hemisphere>\d)1\d\d$", RegexOptions.CultureInvariant)]
    private static partial Regex MountStatusRegex();

    [GeneratedRegex(@"^:RMRVE12(\d{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex SgpFirmwareRegex();

    /// <summary>Matches :HRGR{nn}{mm}, the RA and Dec guide rates as percentages of sidereal. Example (bench, 2026-11-05): :HRGR5050.</summary>
    [GeneratedRegex(@"^:HRGR(?<Ra>\d{2})(?<Dec>\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex GuideRateRegex();

    #endregion
}
