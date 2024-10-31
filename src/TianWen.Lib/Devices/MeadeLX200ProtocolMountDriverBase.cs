using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using TianWen.Lib.Astrometry;
using static TianWen.Lib.Astrometry.CoordinateUtils;
using static TianWen.Lib.Astrometry.Constants;

namespace TianWen.Lib.Devices;

internal record struct MountDeviceInfo(ISerialDevice SerialDevice);

/// <summary>
/// Abstract mount based on the Meade LX200 protocol.
/// Developed against LX85 Mount.
/// </summary>
/// <param name="device"></param>
/// <param name="external"></param>
internal abstract class MeadeLX200ProtocolMountDriverBase<TDevice>(TDevice device, IExternal external)
    : DeviceDriverBase<TDevice, MountDeviceInfo>(device, external), IMountDriver
    where TDevice : DeviceBase
{
    private static readonly Encoding _encoding = Encoding.Latin1;

    const int MOVING_STATE_NORMAL = 0;
    const int MOVING_STATE_PARKED = 1;
    const int MOVING_STATE_PULSE_GUIDING = 2;
    const int MOVING_STATE_SLEWING = 3;

    const double DEFAULT_GUIDE_RATE = SIDEREAL_RATE * 2d / 3d / 3600d;

    private ITimer? _slewTimer;
    private volatile PierSide _sideOfPierAfterLastGoto;
    private int _movingState = MOVING_STATE_NORMAL;
    private bool? _isSouthernHemisphere;
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

    public bool CanSetGuideRates => false;

    public bool CanPark => true;

    public bool CanSetPark => false;

    public bool CanUnpark => false;

    public bool CanSlew => false;

    public bool CanSlewAsync => true;

    public bool CanSync => true;

    public bool CanMoveAxis(TelescopeAxis axis) => false;

    public IReadOnlyList<AxisRate> AxisRates(TelescopeAxis axis) => [];

    public void MoveAxis(TelescopeAxis axis, double rate)
    {
        throw new InvalidOperationException("Moving axis directly is not supported");
    }

    public TrackingSpeed TrackingSpeed
    {
        get
        {
            SendAndReceive("GT"u8, out var response);
            if (double.TryParse(response, CultureInfo.InvariantCulture, out var trackingHz))
            {
                return trackingHz switch
                {
                    >= 59.9 and <= 60.1 => TrackingSpeed.Sidereal,
                    >= 57.3 and <= 58.9 => TrackingSpeed.Lunar,
                    _ => TrackingSpeed.None
                };
            }
            else
            {
                throw new InvalidOperationException($"Failed to convert GT response {_encoding.GetString(response)} to a tracking frequency");
            }
        }

        set
        {
            var speed = value switch
            {
                TrackingSpeed.Sidereal or TrackingSpeed.Solar => "TQ"u8,
                TrackingSpeed.Lunar => "TL"u8,
                _ => throw new ArgumentException($"Tracking speed {value} is not yet supported!", nameof(value))
            };

            Send(speed);
        }
    }

    public IReadOnlyCollection<TrackingSpeed> TrackingSpeeds => [TrackingSpeed.Sidereal, TrackingSpeed.Lunar];

    public EquatorialCoordinateType EquatorialSystem => EquatorialCoordinateType.Topocentric;

    public bool Tracking
    {
        get
        {
            var (_, tracking, _) = AlignmentDetails;
            return tracking;
        }

        set
        {
            if (IsPulseGuiding)
            {
                throw new InvalidOperationException($"Cannot set tracking={value} while pulse guiding");
            }

            if (IsSlewing)
            {
                throw new InvalidOperationException($"Cannot set tracking={value} while slewing");
            }

            Send(value ? "AP"u8 : "AL"u8);
        }
    }

    public AlignmentMode Alignment
    {
        get
        {
            var (mode, _, _) = AlignmentDetails;
            return mode;
        }
    }

    private (AlignmentMode Mode, bool Tracking, int AlignmentStars) AlignmentDetails
    {
        get
        {
            // TODO LX800 fixed GW response not being terminated, account for that
            SendAndReceive("GW"u8, out var response, count: 3);
            if (response is { Length: 3 })
            {
                var mode = response[0] switch
                {
                    (byte)'A' => AlignmentMode.AltAz,
                    (byte)'P' => AlignmentMode.Polar,
                    (byte)'G' => AlignmentMode.GermanPolar,
                    var invalid => throw new InvalidOperationException($"Invalid alginment mode {invalid} returned")
                };

                var tracking = response[1] == (byte)'T';

                var alignmentStars = response[2] switch
                {
                    (byte)'1' => 1,
                    (byte)'2' => 2,
                    _ => 0
                };

                return (mode, tracking, alignmentStars);
            }
            else
            {
                throw new InvalidOperationException($"Failed to parse :GW# response {_encoding.GetString(response)}");
            }
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
    private bool IsSlewingFromMount
    {
        get
        {
            Send("D"u8);

            if (TryReadTerminated(out var response))
            {
                return response is { Length: >= 1 } && response[0] is (byte)'|' or 0x7f;
            }
            else
            {
                return false;
            }
        }
    }

    public DateTime? UTCDate
    {
        get => DateTime.SpecifyKind(LocalDate.Add(LocalTime).Add(UtcCorrection), DateTimeKind.Utc);

        set
        {
            var offset = UtcCorrection;
            if (value is { Kind: DateTimeKind.Utc } utcDate)
            {
                Span<byte> buffer = stackalloc byte[2 + 8];
                var adjustedDateTime = utcDate - offset;

                "SL"u8.CopyTo(buffer);

                if (!adjustedDateTime.TryFormat(buffer[2..], out _, "HH:mm:ss", CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException($"Failed to convert {value} to HH:mm:ss");
                }

                SendAndReceive(buffer, out var slResponse);
                if (slResponse.SequenceEqual("1"u8))
                {
                    throw new ArgumentException($"Failed to set date to {value}, command was {_encoding.GetString(buffer)} with response {_encoding.GetString(slResponse)}", nameof(value));
                }

                "SC"u8.CopyTo(buffer);

                if (!adjustedDateTime.TryFormat(buffer[2..], out _, "MM/dd/yy", CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException($"Failed to convert {value} to MM/dd/yy");
                }

                SendAndReceive(buffer, out var scResponse);
                if (scResponse.SequenceEqual("1"u8))
                {
                    throw new ArgumentException($"Failed to set date to {value}, command was {_encoding.GetString(buffer)} with response {_encoding.GetString(scResponse)}", nameof(value));
                }

                //throwing away these two strings which represent
                //Updating Planetary Data#
                //                       #
                TimeIsSetByUs = TryReadTerminated(out _) && TryReadTerminated(out _);
            }
        }
    }

    private DateTime LocalDate
    {
        get
        {
            SendAndReceive("GC"u8, out var response);

            if (DateTime.TryParseExact(_encoding.GetString(response), "MM/dd/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
            {
                return date;
            }

            throw new InvalidOperationException($"Could not parse response {_encoding.GetString(response)} of GC (get local date)");
        }
    }

    private TimeSpan LocalTime
    {
        get
        {
            SendAndReceive("GL"u8, out var response);

            if (Utf8Parser.TryParse(response, out TimeSpan time, out _))
            {
                return time.Modulo24h();
            }

            throw new InvalidOperationException($"Could not parse response {_encoding.GetString(response)} of GL (get local time)");
        }
    }

    private TimeSpan UtcCorrection
    {
        get
        {
            // :GG# Get UTC offset time
            // Returns: sHH# or sHH.H#
            // The number of decimal hours to add to local time to convert it to UTC. If the number is a whole number the
            // sHH# form is returned, otherwise the longer form is returned.
            SendAndReceive("GG"u8, out var response);
            if (double.TryParse(response, out var offsetHours))
            {
                return TimeSpan.FromHours(offsetHours);
            }

            throw new InvalidOperationException($"Could not parse response {_encoding.GetString(response)} of GG (get UTC offset)");
        }
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
    private (bool IsSlewing, PierSide SideOfPier, bool HasFlipped, double LST) SideOfPierCheck(double ra)
    {
        if (!Connected)
        {
            return (false, PierSide.Unknown, false, double.NaN);
        }

        var isSlewing = IsSlewingFromMount;

        var (raSideOfPier, lst) = CalculateSideOfPier(ra);

        if (_sideOfPierAfterLastGoto is PierSide.Unknown)
        {
            _sideOfPierAfterLastGoto = IsSouthernHemisphere ? PierSide.East : PierSide.West;
        }

        return (isSlewing, isSlewing ? raSideOfPier : _sideOfPierAfterLastGoto, raSideOfPier != _sideOfPierAfterLastGoto && _sideOfPierAfterLastGoto is not PierSide.Unknown, lst);
    }

    public PierSide SideOfPier
    {
        get
        {
            var (_, sop, _, _) = SideOfPierCheck(RightAscension);

            return sop;
        }

        set => throw new InvalidOperationException("Setting side of pier is not supported");
    }

    public double SiderealTime
    {
        get
        {
            SendAndReceive("GS"u8, out var response);

            if (Utf8Parser.TryParse(response, out TimeSpan time, out _))
            {
                return time.Modulo24h().TotalHours;
            }

            throw new InvalidOperationException($"Could not parse response {_encoding.GetString(response)} of GS (get sidereal time)");
        }
    }

    public double RightAscension
    {
        get
        {
            var (ra, _) = GetRightAscensionWithPrecision(target: false);
            return ra;
        }
    }

    public double Declination
    {
        get
        {
            var (dec, _) = GetDeclinationWithPrecision(target: false);
            return dec;
        }
    }

    public double TargetRightAscension
    {
        get
        {
            var (ra, _) = GetRightAscensionWithPrecision(target: true);
            return ra;
        }

        set
        {
            if (value >= 24)
            {
                throw new ArgumentException("Target right ascension cannot greater or equal 24h", nameof(value));
            }

            if (value < 0)
            {
                throw new ArgumentException("Target right ascension cannot be less than 0h", nameof(value));
            }

            // :SrHH:MM.T#   for low precision  (24h)
            // :SrHH:MM:SS#  for high precision (24h)
            var (ra, highPrecision) = GetRightAscensionWithPrecision(target: false);

            // convert decimal hours to HH:MM.T (classic LX200 RA Notation) if low precision. T is the decimal part of minutes which is converted into seconds
            var targetHms = TimeSpan.FromHours(Math.Abs(value)).Round(highPrecision ? TimeSpanRoundingType.Second : TimeSpanRoundingType.TenthMinute).Modulo24h();

            const int offset = 2;
            Span<byte> buffer = stackalloc byte[2 + 2 + 2 + 2 + 1 + (highPrecision ? 1 : 0)];
            "Sr"u8.CopyTo(buffer);

            if (targetHms.Hours.TryFormat(buffer[offset..], out int hoursWritten, "00", CultureInfo.InvariantCulture)
                && offset + hoursWritten + 1 is int minOffset && minOffset < buffer.Length
                && targetHms.Minutes.TryFormat(buffer[minOffset..], out int minutesWritten, "00", CultureInfo.InvariantCulture)
            )
            {
                buffer[offset + hoursWritten] = (byte)':';
            }
            else
            {
                throw new ArgumentException($"Failed to convert value {value} to HM", nameof(value));
            }

            var secOffset = minOffset + minutesWritten + 1;
            if (highPrecision)
            {
                buffer[secOffset - 1] = (byte)':';
                if (!targetHms.Seconds.TryFormat(buffer[secOffset..], out _, "00", CultureInfo.InvariantCulture))
                {
                    throw new ArgumentException($"Failed to convert {value} to high precision seconds", nameof(value));
                }
            }
            else
            {
                buffer[secOffset - 1] = (byte)'.';
                if (!(targetHms.Seconds / 6).TryFormat(buffer[secOffset..], out _, "0", CultureInfo.InvariantCulture))
                {
                    throw new ArgumentException($"Failed to convert {value} to low precision tenth of minute", nameof(value));
                }
            }

            SendAndReceive(buffer, out var response, count: 1);

            if (!response.SequenceEqual("1"u8))
            {
                throw new InvalidOperationException($"Failed to set target right ascension to {HoursToHMS(value)}, using command {_encoding.GetString(buffer)}, response={_encoding.GetString(response)}");
            }

#if TRACE
            External.AppLogger.LogTrace("Set target right ascension to {TargetRightAscension}, current right ascension is {RightAscension}, high precision={HighPrecision}",
                HoursToHMS(value), HoursToHMS(ra), highPrecision);
#endif
        }
    }

    public double TargetDeclination
    {
        get
        {
            var (dec, _) = GetDeclinationWithPrecision(target: true);
            return dec;
        }

        set
        {
            if (value > 90)
            {
                throw new ArgumentException("Target declination cannot be greater than 90 degrees.", nameof(value));
            }

            if (value < -90)
            {
                throw new ArgumentException("Target declination cannot be lower than -90 degrees.", nameof(value));
            }

            // :SdsDD*MM#    for low precision
            // :SdsDD*MM:SS# for high precision
            var (dec, highPrecision) = GetDeclinationWithPrecision(target: false);

            var sign = Math.Sign(value);
            var signLength = sign is -1 ? 1 : 0;
            var degOffset = 2 + signLength;
            var minOffset = degOffset + 2 + 1;
            var targetDms = TimeSpan.FromHours(Math.Abs(value))
                .Round(highPrecision ? TimeSpanRoundingType.Second : TimeSpanRoundingType.Minute)
                .EnsureMax(TimeSpan.FromHours(90));

            Span<byte> buffer = stackalloc byte[minOffset + 2 + (highPrecision ? 3 : 0)];
            "Sd"u8.CopyTo(buffer);

            if (sign is -1)
            {
                buffer[degOffset - 1] = (byte)'-';
            }

            buffer[minOffset - 1] = (byte)'*';

            if (targetDms.Hours.TryFormat(buffer[degOffset..], out _, "00", CultureInfo.InvariantCulture)
                && targetDms.Minutes.TryFormat(buffer[minOffset..], out _, "00", CultureInfo.InvariantCulture)
            )
            {
                if (highPrecision)
                {
                    var secOffset = minOffset + 2 + 1;
                    buffer[secOffset - 1] = (byte)':';

                    if (!targetDms.Seconds.TryFormat(buffer[secOffset..], out _, "00", CultureInfo.InvariantCulture))
                    {
                        throw new ArgumentException($"Failed to convert value {value} to DMS (high precision)", nameof(value));
                    }
                }
            }
            else
            {
                throw new ArgumentException($"Failed to convert value {value} to DM", nameof(value));
            }

            SendAndReceive(buffer, out var response, count: 1);

            if (!response.SequenceEqual("1"u8))
            {
                throw new InvalidOperationException($"Failed to set target declination to {DegreesToDMS(value)}, using command {_encoding.GetString(buffer)}, response={_encoding.GetString(response)}");
            }

#if TRACE
            External.AppLogger.LogTrace("Set target declination to {TargetDeclination}, current declination is {Declination}, high precision={HighPrecision}",
                DegreesToDMS(value), DegreesToDMS(dec), highPrecision);
#endif
        }
    }

    private (double RightAscension, bool HighPrecision) GetRightAscensionWithPrecision(bool target)
    {
        SendAndReceive(target ? "Gr"u8 : "GR"u8, out var response);
        var ra = HmsOrHmTToHours(response, out var highPrecision);

        return (ra, highPrecision);
    }

    private (double Declination, bool HighPrecision) GetDeclinationWithPrecision(bool target)
    {
        SendAndReceive(target ? "Gd"u8 : "GD"u8, out var response);
        var dec = DMSToDegree(_encoding.GetString(response).Replace('\xdf', ':'));

        return (dec, response.Length >= 7);
    }


    /// <summary>
    /// convert a HH:MM.T (classic LX200 RA Notation) string to a double hours. T is the decimal part of minutes which is converted into seconds
    /// </summary>
    private static double HmsOrHmTToHours(ReadOnlySpan<byte> hmValue, out bool highPrecision)
    {
        var hm = _encoding.GetString(hmValue);
        var token = hm.Split('.');

        // is high precision
        highPrecision = token.Length != 2;
        if (highPrecision)
        {
            return HMSToHours(hm);
        }

        var seconds = short.Parse(token[1]) * 6;
        var hms = $"{token[0]}:{seconds}";
        return HMSToHours(hms);
    }

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
        get => GetLatOrLong("Gt"u8);

        set
        {
            if (value > 90)
            {
                throw new ArgumentException("Site latitude cannot be greater than 90 degrees.", nameof(value));
            }

            if (value < -90)
            {
                throw new ArgumentException("Site latitude cannot be lower than -90 degrees.", nameof(value));
            }

            var abs = Math.Abs(value);
            var dms = TimeSpan.FromHours(abs).Round(TimeSpanRoundingType.Minute).EnsureMax(TimeSpan.FromHours(90));

            var needsSign = value < 0;
            const int cmdLength = 2;
            var offset = cmdLength + (needsSign ? 1 : 0);

            Span<byte> buffer = stackalloc byte[offset + 1 + 2];
            "St"u8.CopyTo(buffer);

            if (needsSign)
            {
                buffer[cmdLength] = (byte)'-';
            }

            if (dms.Hours.TryFormat(buffer[offset..], out var degWritten, format: "00", provider: CultureInfo.InvariantCulture)
                && dms.Minutes.TryFormat(buffer[(offset + degWritten + 1)..], out _, format: "00", provider: CultureInfo.InvariantCulture)
            )
            {
                buffer[offset + degWritten] = (byte)'*';

                SendAndReceive(buffer, out var response);

                if (response.SequenceEqual("1"u8))
                {
                    External.AppLogger.LogInformation("Updated site latitude to {Degrees}", value);
                }
                else
                {
                    throw new InvalidOperationException($"Cannot update site latitude to {value} due to connectivity issue/command invalid: {_encoding.GetString(response)}");
                }
            }
            else
            {
                throw new InvalidOperationException($"Cannot update site latitude to {value} due to formatting error");
            }
        }
    }

    public double SiteLongitude
    {
        get => -1 * GetLatOrLong("Gg"u8);

        set
        {
            if (value > 180)
            {
                throw new ArgumentException("Site longitude cannot be greater than 180 degrees.", nameof(value));
            }

            if (value < -180)
            {
                throw new ArgumentException("Site longitude cannot be lower than -180 degrees.", nameof(value));
            }

            var abs = Math.Abs(value);
            var dms = TimeSpan.FromHours(abs)
                .Round(TimeSpanRoundingType.Minute)
                .EnsureRange(TimeSpan.FromHours(-180), TimeSpan.FromHours(+180));

            var adjustedDegrees = value > 0 ? 360 - dms.Hours : dms.Hours;

            const int offset = 2;
            Span<byte> buffer = stackalloc byte[offset + 3 + 1 + 2];
            "Sg"u8.CopyTo(buffer);

            if (adjustedDegrees.TryFormat(buffer[offset..], out var degWritten, format: "000", provider: CultureInfo.InvariantCulture)
                && dms.Minutes.TryFormat(buffer[(offset + degWritten + 1)..], out _, format: "00", provider: CultureInfo.InvariantCulture)
            )
            {
                buffer[offset + degWritten] = (byte)'*';

                SendAndReceive(buffer, out var response);

                if (response.SequenceEqual("1"u8))
                {
                    External.AppLogger.LogInformation("Updated site longitude to {Degrees}", value);
                }
                else
                {
                    throw new InvalidOperationException($"Cannot update site longitude to {value} due to connectivity issue/command invalid: {_encoding.GetString(response)}");
                }
            }
            else
            {
                throw new InvalidOperationException($"Cannot update site longitude to {value} due to formatting error");
            }
        }
    }

    private double GetLatOrLong(ReadOnlySpan<byte> command)
    {
        SendAndReceive(command, out var response);
        if (response is { Length: >= 5 })
        {
            var isNegative = response[0] is (byte)'-';
            var offset = isNegative ? 1 : 0;

            if (Utf8Parser.TryParse(response[offset..], out int degrees, out var consumed)
                && Utf8Parser.TryParse(response[(offset + consumed + 1)..], out int minutes, out _)
            )
            {
                var latOrLongNotAdjusted = (isNegative ? -1 : 1) * (degrees + minutes / 60d);
                // adjust s.th. 214 from mount becomes -214 and then becomes 146
                return latOrLongNotAdjusted >= -180 ? latOrLongNotAdjusted : latOrLongNotAdjusted + 360;
            }
        }

        throw new InvalidOperationException($"Failed to parse response {_encoding.GetString(response)} of {_encoding.GetString(command)}");
    }

    public override string? DriverInfo => $"{_telescopeName} ({_telescopeFW})";

    public override string? Description => $"{_telescopeName} driver based on the LX200 serial protocol v2010.10, firmware: {_telescopeFW}";

    public PierSide DestinationSideOfPier(double ra, double dec)
    {
        var (sideOfPier, _) = CalculateSideOfPier(ra);
        return sideOfPier;
    }

    private bool IsSouthernHemisphere => _isSouthernHemisphere ??= SiteLatitude < 0;

    private (PierSide SideOfPier, double SiderealTime) CalculateSideOfPier(double ra)
    {
        var lst = SiderealTime;
        var sideOfPier = ConditionHA(lst - ra) switch
        {
            0 => IsSouthernHemisphere ? PierSide.East : PierSide.West,
            > 0 => PierSide.East,
            < 0 => PierSide.West,
            _ => PierSide.Unknown
        };

        return (sideOfPier, lst);
    }

    public void Park()
    {
        Send("hP"u8);

        var previousState = Interlocked.Exchange(ref _movingState, MOVING_STATE_SLEWING);
#if TRACE
        External.AppLogger.LogTrace("Parking mount, previous state: {PreviousMovingState}", MovingStateDisplayName(previousState));
#endif
        StartSlewTimer(MOVING_STATE_PARKED);
    }

    public void PulseGuide(GuideDirection direction, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("Timespan must be greater than 0", nameof(duration));
        }

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
        var ms = (int)Math.Round(duration.TotalMilliseconds);

        if (!Tracking)
        {
            throw new InvalidOperationException("Cannot pulse guide when tracking is off");
        }

        if (ms.TryFormat(buffer, out _, "0000", CultureInfo.InvariantCulture))
        {
            Send(buffer);

            External.TimeProvider.CreateTimer(
                _ => _ = Interlocked.CompareExchange(ref _movingState, MOVING_STATE_NORMAL, MOVING_STATE_PULSE_GUIDING),
                null,
                duration,
                Timeout.InfiniteTimeSpan
            );
        }
        else
        {
            throw new ArgumentException($"Failed to create request for given duration={duration} message={_encoding.GetString(buffer)}", nameof(duration));
        }
    }

    /// <summary>
    /// Sets target coordinates to (<paramref name="ra"/>,<paramref name="dec"/>), using <see cref="EquatorialSystem"/>.
    /// </summary>
    /// <param name="ra"></param>
    /// <param name="dec"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void SlewRaDecAsync(double ra, double dec)
    {
        if (IsPulseGuiding)
        {
            throw new InvalidOperationException("Cannot slew while pulse-guiding");
        }

        if (IsSlewing)
        {
            throw new InvalidOperationException("Cannot slew while a slew is still ongoing");
        }

        if (AtPark)
        {
            throw new InvalidOperationException("Mount is parked");
        }

        TargetRightAscension = ra;
        TargetDeclination = dec;

        SendAndReceive("MS"u8, out var response, count: 1);

        if (response.SequenceEqual("0"u8))
        {
            var previousState = Interlocked.Exchange(ref _movingState, MOVING_STATE_SLEWING);
#if TRACE
            External.AppLogger.LogTrace("Slewing to {RA},{Dec}, previous state: {PreviousMovingState}", HoursToHMS(ra), DegreesToDMS(dec), MovingStateDisplayName(previousState));
#endif
            StartSlewTimer(MOVING_STATE_NORMAL);
        }
        else if (response.Length is 1 && byte.TryParse(response[0..1], out var reasonCode) && TryReadTerminated(out var reasonMessage))
        {
            var reason = reasonCode switch
            {
                1 => "below horizon limit",
                2 => "above hight limit",
                _ => $"unknown reason {reasonCode}: {_encoding.GetString(reasonMessage)}"
            };

            throw new InvalidOperationException($"Failed to slew to {HoursToHMS(ra)},{DegreesToDMS(dec)} due to {reason} message={reasonCode}{_encoding.GetString(reasonMessage)}");
        }
        else
        {
            throw new InvalidOperationException($"Failed to slew to {HoursToHMS(ra)},{DegreesToDMS(dec)} due to an unrecognized response: {_encoding.GetString(response)}");
        }
    }

    private void StartSlewTimer(int finalState)
    {
        // start timer deactivated to capture itself
        var timer = External.TimeProvider.CreateTimer(SlewTimerCallback, finalState, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _slewTimer, timer)?.Dispose();

        // activate timer
        timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    private void SlewTimerCallback(object? state)
    {
        bool continueRunning;
        var ra = RightAscension;
        var dec = Declination;
        double lst;
        PierSide sideOfPier;
        if (!double.IsNaN(ra) && !AtPark)
        {
            (continueRunning, sideOfPier, var hasFlipped, lst) = SideOfPierCheck(ra);

            if (hasFlipped)
            {
                _sideOfPierAfterLastGoto = sideOfPier;
            }
        }
        else
        {
            continueRunning = false;
            lst = SiderealTime;
            sideOfPier = PierSide.Unknown;
        }

        if (continueRunning)
        {
#if TRACE
            External.AppLogger.LogTrace("Still slewing hour angle={HourAngle} lst={LST} ra={Ra} dec={Dec} sop={SideOfPier}",
                HoursToHMS(ConditionHA(lst - ra)), HoursToHMS(lst), HoursToHMS(ra), DegreesToDMS(dec), sideOfPier);
#endif
        }
        else
        {
            if (_slewTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan) is var changeResult and not true)
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
                else
                {
#if TRACE
                    External.AppLogger.LogTrace("Slew complete hour angle={HourAngle} lst={LST} ra={Ra} dec={Dec} sop={SideOfPier}",
                        HoursToHMS(ConditionHA(lst - ra)), HoursToHMS(lst), HoursToHMS(ra), DegreesToDMS(dec), sideOfPier);
#endif
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
    public void AbortSlew()
    {
        if (IsPulseGuiding)
        {
            throw new InvalidOperationException("Cannot abort slewing while pulse guiding");
        }

        if (IsSlewing)
        {
            Send("Q"u8);
            StartSlewTimer(MOVING_STATE_NORMAL);
        }
    }

    /// <summary>
    /// Does not allow sync across the meridian
    /// </summary>
    /// <param name="ra"></param>
    /// <param name="dec"></param>
    /// <returns></returns>
    public void SyncRaDec(double ra, double dec)
    {
        var sideOfPier = SideOfPier;
        var (expectedSideOfPier, _) = CalculateSideOfPier(ra);
        if (sideOfPier is not PierSide.Unknown && sideOfPier != expectedSideOfPier)
        {
            throw new InvalidOperationException($"Cannot sync across meridian (current side of pier: {sideOfPier}) given {HoursToHMS(ra)},{DegreesToDMS(dec)}");
        }

        SendAndReceive("CM"u8, out var response);

        if (response is not { Length: > 0 })
        {
            throw new InvalidOperationException($"Failed to sync {HoursToHMS(ra)},{DegreesToDMS(dec)}");
        }
    }

    public void Unpark() => throw new InvalidOperationException("Unparking is not supported");

    protected override bool ConnectDevice(out int connectionId, out MountDeviceInfo connectedDeviceInfo)
    {
        try
        {
            connectionId = CONNECTION_ID_EXCLUSIVE;
            connectedDeviceInfo = new MountDeviceInfo(External.OpenSerialDevice(_device, 9600, _encoding, TimeSpan.FromMicroseconds(500)));

            DeviceConnectedEvent += Mount_DeviceConnectedEvent;

            return connectedDeviceInfo.SerialDevice?.IsOpen is true;
        }
        catch (Exception ex)
        {
            External.AppLogger.LogError(ex, "Error {ErrorMessage} when connecting to serial port {DeviceAddress}", ex.Message, _device.Address);

            connectedDeviceInfo = default;
            connectionId = CONNECTION_ID_UNKNOWN;

            return false;
        }
    }

    private void Mount_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        if (e.Connected)
        {
            SendAndReceive("GVP"u8, out var gvpBytes);
            _telescopeName = _encoding.GetString(gvpBytes);

            SendAndReceive("GVN"u8, out var gvnBytes);
            _telescopeFW = _encoding.GetString(gvnBytes);

            if (!TrySetHighPrecision())
            {
                External.AppLogger.LogWarning("Failed to set high precision via :U#");
            }
        }
    }

    private bool TrySetHighPrecision()
    {
        bool highPrecision;
        int tries = 0;
        do
        {
            (_, highPrecision) = GetRightAscensionWithPrecision(target: false);

            if (highPrecision)
            {
                return true;
            }
            else
            {
                Send("U"u8);
            }
        } while (!highPrecision && ++tries < 3);

        return false;
    }

    protected override bool DisconnectDevice(int connectionId)
    {
        if (connectionId == CONNECTION_ID_EXCLUSIVE)
        {
            DeviceConnectedEvent -= Mount_DeviceConnectedEvent;

            if (_deviceInfo.SerialDevice is { IsOpen: true } port)
            {
                return port.TryClose();
            }
            else if (_deviceInfo.SerialDevice is { })
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Send(ReadOnlySpan<byte> command)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Mount is not connected");
        }

        if (!CanUnpark && !CanSetPark && AtPark)
        {
            throw new InvalidOperationException("Mount is parked, but it is not possible to unpark it");
        }

        if (_deviceInfo.SerialDevice is not { } port || !port.IsOpen)
        {
            throw new InvalidOperationException("Serial port is closed");
        }

        Span<byte> raw = stackalloc byte[command.Length + 2];
        raw[0] = (byte)':';
        command.CopyTo(raw[1..]);
        raw[^1] = (byte)'#';

        if (!port.TryWrite(raw))
        {
            throw new InvalidOperationException($"Failed to send raw message {_encoding.GetString(raw)}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadTerminated(out ReadOnlySpan<byte> response)
    {
        if (_deviceInfo.SerialDevice is { } port)
        {
            return port.TryReadTerminated(out response, "#\0"u8);
        }

        response = default;
        return false;
    }

    private bool TryReadExactly(int count, out ReadOnlySpan<byte> response)
    {
        if (_deviceInfo.SerialDevice is { } port && port.TryReadExactly(count, out response))
        {
            return true;
        }

        response = default;
        return false;
    }

    private void SendAndReceive(ReadOnlySpan<byte> command, out ReadOnlySpan<byte> response, int? count = null)
    {
        Send(command);

        if (!(count is { } ? TryReadExactly(count.Value, out response) : TryReadTerminated(out response)))
        {
            throw new InvalidOperationException($"Failed to get response for message {_encoding.GetString(command)}");
        }
    }
    #endregion
}
