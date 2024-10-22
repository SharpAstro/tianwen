using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using TianWen.Lib.Astrometry;
using static TianWen.Lib.Astrometry.CoordinateUtils;
using static TianWen.Lib.Astrometry.SOFA.Constants;

namespace TianWen.Lib.Devices.Meade;

/// <summary>
/// Mount based on the Meade LX200 protocol.
/// Developed against LX85 Mount.
/// </summary>
/// <param name="device"></param>
/// <param name="external"></param>
internal class MeadeLX200BasedMount(MeadeDevice device, IExternal external) : DeviceDriverBase<MeadeDevice, MountDeviceInfo>(device, external), IMountDriver
{
    private static readonly Encoding _encoding = Encoding.Latin1;

    const int MOVING_STATE_NORMAL = 0;
    const int MOVING_STATE_PARKED = 1;
    const int MOVING_STATE_PULSE_GUIDING = 2;
    const int MOVING_STATE_SLEWING = 3;

    const double DEFAULT_GUIDE_RATE = SIDEREAL_RATE * 2d/3d / 3600d;

    private ITimer? _slewTimer;
    private volatile PierSide _sideOfPierAfterLastGoto = PierSide.Unknown;
    private int _movingState = MOVING_STATE_NORMAL;
    private string _telescopeName = "Unknown";
    private string _telescopeFW = "Unknown";

    public bool CanSetTracking => true;

    public bool CanSetSideOfPier => false;

    /// <summary>
    /// TODO: Determine based on firmware/patchlevel
    /// </summary>
    public bool CanPulseGuide => true;

    public bool CanSetRightAscensionRate => false;

    public bool CanSetDeclinationRate => false;

    /// <summary>
    /// 
    /// </summary>
    public bool CanSetGuideRates => false;

    public bool CanPark => true;

    public bool CanSetPark => false;

    public bool CanUnpark => false;

    public bool CanSlew => true;

    public bool CanSlewAsync => true;

    public bool CanSync => true;

    public TrackingSpeed TrackingSpeed
    {
        get
        {
            if (TrySendAndReceive("GT"u8, out var response) && double.TryParse(response, CultureInfo.InvariantCulture, out var trackingHz))
            {
                return trackingHz switch
                {
                    >= 59.9 and <= 60.1 => TrackingSpeed.Sidereal,
                    >= 57.3 and <= 58.9 => TrackingSpeed.Lunar,
                    _ => TrackingSpeed.None
                };
            }

            return TrackingSpeed.None;
        }

        set
        {
            var speed = value switch
            {
                TrackingSpeed.Sidereal or TrackingSpeed.Solar => "TQ"u8,
                TrackingSpeed.Lunar => "TL"u8,
                _ => throw new ArgumentException($"Tracking speed {value} is not yet supported!")
            };

            if (!Connected || AtPark || !TrySend(speed))
            {
                throw new InvalidOperationException($"Failed to set tracking speed to {value}");
            }
        }        
    }

    public IReadOnlyCollection<TrackingSpeed> TrackingSpeeds => [TrackingSpeed.Sidereal, TrackingSpeed.Lunar];

    public EquatorialCoordinateType EquatorialSystem => EquatorialCoordinateType.Topocentric;

    public bool Tracking
    {
        get => TryGetAlignment(out _, out var tracking, out _) && tracking;
        set
        {
            if ((IsPulseGuiding || IsSlewing) && !TrySend(value ? "AP"u8 : "AL"u8))
            {
                throw new InvalidOperationException($"Failed to set tracking={value}");
            }
        }
    }

    public AlignmentMode? Alignment => TryGetAlignment(out var alignmentMode, out _, out _) ? alignmentMode : null;

    private bool TryGetAlignment(out AlignmentMode mode, out bool tracking, out int alignmentStars)
    {
        if (TrySendAndReceive("GW"u8, out var gwBytes) && gwBytes is { Length: 3 })
        {
            mode = gwBytes[0] switch
            {
                (byte)'A' => AlignmentMode.AltAz,
                (byte)'P' => AlignmentMode.Polar,
                (byte)'G' => AlignmentMode.GermanPolar,
                var invalid => throw new InvalidOperationException($"Invalid alginment mode {invalid} returned")
            };

            tracking = gwBytes[1] == (byte)'T';

            alignmentStars = gwBytes[2] switch
            {
                (byte)'1' => 1,
                (byte)'2' => 2,
                _ => 0
            };

            return true;
        }
        else
        {
            mode = (AlignmentMode)(-1);
            tracking = false;
            alignmentStars = 0;
            return false;
        }
    }

    public bool AtHome => false;

    public bool AtPark => CheckMovingState(MOVING_STATE_PARKED);

    public bool IsPulseGuiding => CheckMovingState(MOVING_STATE_PULSE_GUIDING);

    public bool IsSlewing => CheckMovingState(MOVING_STATE_SLEWING);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CheckMovingState(int movingState) => Connected && Interlocked.CompareExchange(ref _movingState, movingState, movingState) == movingState;

    /// <summary>
    /// Uses :D# to check if mount is slewing (use this to update slewing state)
    /// </summary>
    private bool IsSlewingFromMount => TrySendAndReceive("D"u8, out var response) && response is { Length: >= 1 } &&  response[0] is (byte)'|' or 0x7f;

    public DateTime? UTCDate
    {
        get => TryGetUtcCorrection(out var offset) && TryGetLocalDate(out var date) && TryGetLocalTime(out var time)
                ? new DateTimeOffset(date.Add(time), offset).UtcDateTime
                : null;
        set
        {
            if (value is { Kind: DateTimeKind.Utc } utcDate && TryGetUtcCorrection(out var offset))
            {
                Span<byte> buffer = stackalloc byte[2 + 8];
                var adjustedDateTime = utcDate - offset;

                "SL"u8.CopyTo(buffer);

                if (!adjustedDateTime.TryFormat(buffer[2..], out _, "HH:mm:ss", CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException($"Failed to convert {value} to HH:mm:ss");
                }

                if (!TrySendAndReceive(buffer, out var slResponse) && slResponse.SequenceEqual("1"u8))
                {
                    throw new ArgumentException($"Failed to set date to {value}", nameof(value));
                }

                "SC"u8.CopyTo(buffer);

                if (!adjustedDateTime.TryFormat(buffer[2..], out _, "MM/dd/yy", CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException($"Failed to convert {value} to MM/dd/yy");
                }

                if (!TrySendAndReceive(buffer, out var scResponse) && scResponse.SequenceEqual("1"u8))
                {
                    throw new ArgumentException($"Failed to set date to {value}", nameof(value));
                }

                //throwing away these two strings which represent
                //Updating Planetary Data#
                //                       #
                TimeIsSetByUs = TryReadTerminated(out _) && TryReadTerminated(out _);
            }
        }
    }

    private bool TryGetLocalDate(out DateTime date)
    {
        if (TrySendAndReceive("GC"u8, out var dateBytes)
            && DateTime.TryParseExact(_encoding.GetString(dateBytes), "MM/dd/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
        )
        {
            return true;
        }

        date = DateTime.MinValue;
        return false;
    }

    private bool TryGetLocalTime(out TimeSpan time)
    {
        if (TrySendAndReceive("GL"u8, out var timeBytes)
            && Utf8Parser.TryParse(timeBytes, out time, out _)
        )
        {
            return true;
        }

        time = TimeSpan.Zero;
        return false;
    }

    private bool TryGetUtcCorrection(out TimeSpan offset)
    {
        // :GG# Get UTC offset time
        // Returns: sHH# or sHH.H#
        // The number of decimal hours to add to local time to convert it to UTC. If the number is a whole number the
        // sHH# form is returned, otherwise the longer form is returned.
        if (TrySendAndReceive("GG"u8, out var offsetStr) && double.TryParse(offsetStr, out var offsetHours))
        {
            offset = TimeSpan.FromHours(offsetHours);
            return true;
        }

        offset = TimeSpan.Zero;
        return false;
    }

    public bool TimeIsSetByUs { get; private set; }

    /// <summary>
    /// Calculates side of per (as we do not have this information from the mount itself),
    /// based on the last slew and the moving RA axis (when slewing).
    /// 
    /// TODO: This only works for GEM mounts
    /// <list type="table">
    /// <listheader>
    ///   <term>Property</term>
    ///   <description>Description</description>
    /// </listheader>
    /// <item>
    ///   <term>IsSlewing</term>
    ///   <description>The actual slewing state from the mount (not our local value)</description>
    /// </item>
    /// <item>
    ///   <term>SideOfPier</term>
    ///   <description>is the calculated side of pier from the mount position (if slewing),
    /// or the last known value after slewing (as the mount auto-flips)</description>
    /// </item>
    /// <item>
    ///   <term>HasFlipped</term>
    ///   <description>Indicates if a meridian flip occured during the active slew</description>
    /// </item>
    /// </list>
    /// </summary>
    /// <param name="ra">current RA</param>
    /// <returns>Side of pier calculation result</returns>
    private (bool IsSlewing, PierSide SideOfPier, bool HasFlipped) SideOfPierCheck(double ra)
    {
        if (!Connected)
        {
            return (false, PierSide.Unknown, false);
        }

        var isSlewing = IsSlewingFromMount;

        var raSideOfPier = CalculateSideOfPier(ra);

        return (isSlewing, isSlewing ? raSideOfPier : _sideOfPierAfterLastGoto, raSideOfPier != _sideOfPierAfterLastGoto && _sideOfPierAfterLastGoto is not PierSide.Unknown);
    }

    public PierSide SideOfPier
    {
        get
        {
            var (_, sop, _) = SideOfPierCheck(RightAscension);

            return sop;
        }

        set => throw new InvalidOperationException("Setting side of pier is not supported");
    }

    public double SiderealTime
    {
        get
        {
            if (TrySendAndReceive("GS"u8, out var timeStr) && Utf8Parser.TryParse(timeStr, out TimeSpan time, out _))
            {
                return time.TotalHours;
            }

            return double.NaN;
        }
    }

    public double RightAscension => TrySendAndReceive("GR"u8, out var raBytes) ? HmToHours(raBytes) : double.NaN;

    /// <summary>
    /// convert a HH:MM.T (classic LX200 RA Notation) string to a double hours. T is the decimal part of minutes which is converted into seconds
    /// </summary>
    private static double HmToHours(ReadOnlySpan<byte> hmValue)
    {
        var hm = _encoding.GetString(hmValue);
        var token = hm.Split('.');
        if (token.Length != 2)
        {
            return HMSToHours(hm);
        }

        var seconds = short.Parse(token[1]) * 6;
        var hms = $"{token[0]}:{seconds}";
        return HMSToHours(hms);
    }

    public double Declination => TrySendAndReceive("GD"u8, out var decBytes) ? DMSToDegree(_encoding.GetString(decBytes)) : double.NaN;

    public double RightAscensionRate { get => 0.0d; set => throw new InvalidOperationException("Setting right ascension rate is not supported"); }

    public double DeclinationRate { get => 0.0d; set => throw new InvalidOperationException("Setting declination rate is not supported"); }

    /// <summary>
    /// Defaults to 0.67% of sidereal rate.
    /// TODO: Support :RgSS.S# to set guide rate on AutoStar II
    /// </summary>
    public double GuideRateRightAscension
    {
        get => DEFAULT_GUIDE_RATE;
        set => throw new InvalidOperationException("Setting right ascension guide rate is not apported");
    }

    /// <summary>
    /// Defaults to 0.67% of sidereal rate.
    /// TODO: Support :RgSS.S# to set guide rate on AutoStar II
    /// </summary>
    public double GuideRateDeclination
    {
        get => DEFAULT_GUIDE_RATE;
        set => throw new InvalidOperationException("Setting declination guide rate is not apported");
    }

    public double SiteElevation { get; set; } = double.NaN;

    public double SiteLatitude
    {
        get => TryGetLatOrLong("Gt"u8, out var latitude) ? latitude : double.NaN;

        set => SetLatOrLong("latitude", "St"u8, value, "00", -1);
    }

    public double SiteLongitude
    {
        get => TryGetLatOrLong("Gg"u8, out var longitude) ? -longitude : double.NaN;

        set => SetLatOrLong("latitude", "Sg"u8, value, "000", +1);
    }

    private bool TryGetLatOrLong(ReadOnlySpan<byte> command, out double latOrLong)
    {
        if (TrySendAndReceive(command, out var bytes) && bytes is { Length: >= 5 })
        {
            var isNegative = bytes[0] is (byte)'-';
            var offset = isNegative ? 1 : 0;

            if (Utf8Parser.TryParse(bytes[offset..], out int degrees, out var consumed)
                && Utf8Parser.TryParse(bytes[(offset+consumed+1)..], out int minutes, out _)
            )
            {
                var latOrLongNotAdjusted = (isNegative ? -1 : 1) * (degrees + (minutes / 60d));
                // adjust s.th. 214 from mount becomes -214 and then becomes 146
                latOrLong = latOrLongNotAdjusted >= -180 ? latOrLongNotAdjusted : latOrLongNotAdjusted + 360;
                return true;
            }
        }

        latOrLong = double.NaN;
        return false;
    }

    private void SetLatOrLong(string property, ReadOnlySpan<byte> command, double value, ReadOnlySpan<char> degreeFormat, int emitSignWhen)
    {
        var abs = Math.Abs(value);
        var degrees = (int)Math.Truncate(abs);
        var min = (int)Math.Round((abs - degrees) * 60);

        if (min >= 60)
        {
            min -= 60;
            degrees++;
        }

        Span<byte> buffer = stackalloc byte[8];
        command.CopyTo(buffer);

        int offset;
        if (Math.Sign(value) == emitSignWhen)
        {
            offset = 3;
            buffer[2] = (byte)'-';
        }
        else
        {
            offset = 2;
        }

        if (degrees.TryFormat(buffer[offset..], out var degWritten, format: degreeFormat, provider: CultureInfo.InvariantCulture)
            && min.TryFormat(buffer[(offset + degWritten + 1)..], out _, format: "00", provider: CultureInfo.InvariantCulture)
        )
        {
            buffer[offset + degWritten] = (byte)'*';

            if (!Connected)
            {
                throw new InvalidOperationException("Mount is not connected");
            }
            else if (AtPark)
            {
                throw new InvalidOperationException("Mount is parked, so cannot update site location");
            }
            else if (TrySendAndReceive(buffer, out var response) && response.SequenceEqual("1"u8))
            {
                External.AppLogger.LogInformation("Updated site {Property} to {Degrees}", property, value);
            }
            else
            {
                throw new InvalidOperationException($"Cannot update site {property} to {value} due to connectivity issue/command invalid: {_encoding.GetString(response)}");
            }
        }
        else
        {
            throw new InvalidOperationException($"Cannot update site {property} to {value} due to formatting error");
        }
    }

    public override string? DriverInfo => $"{_telescopeName} ({_telescopeFW})";

    public override string? Description => $"{_telescopeName} driver based on the LX200 serial protocol v2010.10, firmware: {_telescopeFW}";

    public PierSide DestinationSideOfPier(double ra, double dec) => CalculateSideOfPier(ra);

    private PierSide CalculateSideOfPier(double ra)
        => ConditionHA(SiderealTime - ra) > 0
            ? PierSide.East
            : PierSide.West;

    public bool Park()
    {
        if (TrySend("hP"u8))
        {
            var previousState = Interlocked.Exchange(ref _movingState, MOVING_STATE_SLEWING);
#if TRACE
            External.AppLogger.LogTrace("Parking mount, previous state: {PreviousMovingState}", MovingStateDisplayName(previousState));
#endif
            StartSlewTimer(MOVING_STATE_PARKED);
            return true;
        }
        else
        {
            External.AppLogger.LogError("Failed to park mount, current state is {MovingState}", MovingStateDisplayName(_movingState));

            return false;
        }    
    }

    public bool PulseGuide(GuideDirection direction, TimeSpan duration)
    {
        Span<byte> buffer = stackalloc byte[7];
        "Mg"u8.CopyTo(buffer);
        buffer[2] = direction switch
        {
            GuideDirection.North => (byte)'n',
            GuideDirection.South => (byte)'s',
            GuideDirection.West => (byte)'w',
            GuideDirection.East => (byte)'e',
            _ => throw new ArgumentException($"Invalid guide direction {direction}", nameof(direction))
        };
        var ms = Math.Round(duration.TotalMilliseconds);
 
        if (ms.TryFormat(buffer, out _, "0000", CultureInfo.InvariantCulture) && Tracking && TrySend(buffer))
        {
            External.TimeProvider.CreateTimer(
                _ => _ = Interlocked.CompareExchange(ref _movingState, MOVING_STATE_NORMAL, MOVING_STATE_PULSE_GUIDING),
                null,
                duration,
                Timeout.InfiniteTimeSpan
            );
        }

        return false;
    }

    public bool SlewRaDecAsync(double ra, double dec)
    {
        if (!IsPulseGuiding && !IsSlewing && !AtPark && TrySendAndReceive("INVALID"u8, out var response) && response is { Length: > 0 })
        {
            var previousState = Interlocked.Exchange(ref _movingState, MOVING_STATE_SLEWING);
#if TRACE
            External.AppLogger.LogTrace("Slewing to {RA}, {Dec}, previous state: {PreviousMovingState}", HoursToHMS(ra), DegreesToDMS(dec), MovingStateDisplayName(previousState));
#endif
            StartSlewTimer(MOVING_STATE_NORMAL);

            return true;
        }

        return false;
    }

    private void StartSlewTimer(int finalState)
    {
        // start timer deactivated to capture itself
        var timer = External.TimeProvider.CreateTimer(SlewTimerCallback, finalState, TimeSpan.FromMicroseconds(-1), TimeSpan.Zero);
        Interlocked.Exchange(ref _slewTimer, timer)?.Dispose();

        // activate timer
        timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    private void SlewTimerCallback(object? state)
    {
        bool continueRunning;
        var ra = RightAscension;
        if (!double.IsNaN(ra) && !AtPark)
        {
            (continueRunning, var sideOfPier, var hasFlipped) = SideOfPierCheck(ra);

            if (hasFlipped)
            {
                _sideOfPierAfterLastGoto = sideOfPier;
            }
        }
        else
        {
            continueRunning = false;
        }

        if (!continueRunning)
        {
            if (_slewTimer?.Change(TimeSpan.FromMicroseconds(-1), TimeSpan.Zero) is var changeResult and not true)
            {
                External.AppLogger.LogWarning("Failed to stop slewing timer has instance: {HasInstance}", changeResult is not null);
            }

            if (state is int finalMovingState)
            {
                var previousState = Interlocked.CompareExchange(ref _movingState, finalMovingState, MOVING_STATE_SLEWING);

                if (previousState != MOVING_STATE_SLEWING && previousState != finalMovingState)
                {
                    External.AppLogger.LogWarning("Expected moving state to be slewing, but was: {PreviousMovingState}", MovingStateDisplayName(previousState));
                }
            }
        }
    }

    private static string MovingStateDisplayName(int previousState) => previousState switch
    {
        MOVING_STATE_NORMAL => "normal",
        MOVING_STATE_PULSE_GUIDING => "pulse guiding (abnormal)",
        MOVING_STATE_SLEWING => "slewing (abnormal)",
        _ => $"unknown ({previousState})"
    };

    /// <summary>
    /// Returns true if mount is not slewing or an ongoing slew was aborted successfully.
    /// Does not disable pulse guiding
    /// TODO: Verify :Q# stops pulse guiding as well
    /// </summary>
    /// <returns></returns>
    public bool AbortSlew()
    {
        if (IsPulseGuiding || !IsSlewing)
        {
            return false;
        }

        if (TrySend("Q"u8))
        {
            _ = Interlocked.CompareExchange(ref _movingState, MOVING_STATE_NORMAL, MOVING_STATE_SLEWING);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Does not allow sync across the meridian
    /// </summary>
    /// <param name="ra"></param>
    /// <param name="dec"></param>
    /// <returns></returns>
    public bool SyncRaDec(double ra, double dec) => SideOfPier == CalculateSideOfPier(ra) && TrySendAndReceive("CM"u8, out var response) && response is { Length: > 0 };

    public bool Unpark() => throw new InvalidOperationException("Unparking is not supported");

    protected override bool ConnectDevice(out int connectionId, out MountDeviceInfo connectedDeviceInfo)
    {
        var deviceId = _device.DeviceId;
        string port;
        
        if (deviceId.StartsWith("tty"))
        {
            port = $"/dev/{deviceId}";
        }
        else
        {
            port = deviceId;
        }

        try
        {
            connectionId = CONNECTION_ID_EXCLUSIVE;
            connectedDeviceInfo = new MountDeviceInfo(new StreamBasedSerialPort(port, 9600, External.AppLogger, _encoding));

            DeviceConnectedEvent += MeadeLX85Mount_DeviceConnectedEvent;

            return connectedDeviceInfo.Port?.IsOpen is true;
        }
        catch (Exception ex)
        {
            External.AppLogger.LogError(ex, "Error {ErrorMessage} when connecting to mount on serial port {SerialPort}", ex.Message, port);

            connectedDeviceInfo = default;
            connectionId = CONNECTION_ID_UNKNOWN;
            
            return false;
        }
    }

    private void MeadeLX85Mount_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected)
        {
            if (TrySendAndReceive("GVP"u8, out var gvpBytes))
            {
                _telescopeName = _encoding.GetString(gvpBytes);
            }

            if (TrySendAndReceive("GVN"u8, out var gvnBytes))
            {
                _telescopeFW = _encoding.GetString(gvnBytes);
            }
        }
    }

    protected override bool DisconnectDevice(int connectionId)
    {
        if (connectionId == CONNECTION_ID_EXCLUSIVE)
        {
            DeviceConnectedEvent -= MeadeLX85Mount_DeviceConnectedEvent;

            if (_deviceInfo.Port is { IsOpen: true } port)
            {
                return port.TryClose();
            }
            else if (_deviceInfo.Port is { })
            {
                return true;
            }
            else
            {
                return false;
            }
        }
        return false;
    }

    #region Serial I/O
    private bool TrySend(ReadOnlySpan<byte> command)
    {
        if (!Connected || (!CanUnpark && !CanSetPark && AtPark))
        {
            return false;
        }

        Span<byte> raw = stackalloc byte[command.Length + 2];

        command.CopyTo(raw[1..]);
        raw[^1] = (byte)'#';

        return _deviceInfo.Port?.TryWrite(raw) ?? false;
    }

    private bool TryReadTerminated(out ReadOnlySpan<byte> response)
    {
        if (_deviceInfo.Port is { } port)
        {
            return port.TryReadTerminated(out response, '#');
        }

        response = default;
        return false;
    }

    private bool TryReadExactly(int count, out ReadOnlySpan<byte> response)
    {
        if (_deviceInfo.Port is { } port)
        {
            return port.TryReadExactly(count, out response);
        }

        response = default;
        return false;
    }

    private bool TrySendAndReceive(ReadOnlySpan<byte> command, out ReadOnlySpan<byte> response)
    {
        // TODO LX800 fixed this, account for that
        if (TrySend(command) && (command.SequenceEqual("GW"u8) ? TryReadExactly(3, out response) : TryReadTerminated(out response)))
        {
            return true;
        }
        else
        {
            response = null;
            return false;
        }
    }
    #endregion
}

internal record struct MountDeviceInfo(StreamBasedSerialPort? Port);