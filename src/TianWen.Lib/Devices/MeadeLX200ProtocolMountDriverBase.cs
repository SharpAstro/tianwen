using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Connections;
using static TianWen.Lib.Astrometry.Constants;
using static TianWen.Lib.Astrometry.CoordinateUtils;

namespace TianWen.Lib.Devices;

internal record struct MountDeviceInfo(ISerialConnection SerialDevice);

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

    const double DEFAULT_GUIDE_RATE = SIDEREAL_RATE * 2d / 3d / 3600d;

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

    public ValueTask MoveAxisAsync(TelescopeAxis axis, double rate, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Moving axis directly is not supported");
    }

    private static readonly ReadOnlyMemory<byte> GTCommand = "GT"u8.ToArray();
    public async ValueTask<TrackingSpeed> GetTrackingSpeedAsync(CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync(GTCommand, cancellationToken);
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
            throw new InvalidOperationException($"Failed to convert GT response {response} to a tracking frequency");
        }
    }

    private readonly ReadOnlyMemory<byte> TQCommand = "TQ"u8.ToArray();
    private readonly ReadOnlyMemory<byte> TLCommand = "TL"u8.ToArray();
    public ValueTask SetTrackingSpeedAsync(TrackingSpeed value, CancellationToken cancellationToken)
    {
        var speed = value switch
        {
            TrackingSpeed.Sidereal or TrackingSpeed.Solar => TQCommand,
            TrackingSpeed.Lunar => TLCommand,
            _ => throw new ArgumentException($"Tracking speed {value} is not yet supported!", nameof(value))
        };

        return SendWithoutResponseAsync(speed, cancellationToken);
    }

    public IReadOnlyList<TrackingSpeed> TrackingSpeeds => [TrackingSpeed.Sidereal, TrackingSpeed.Lunar];

    public EquatorialCoordinateType EquatorialSystem => EquatorialCoordinateType.Topocentric;

    public async ValueTask<bool> IsTrackingAsync(CancellationToken cancellationToken)
    {
        var (_, tracking, _) = await AlignmentDetailsAsync(cancellationToken);
        return tracking;
    }

    private readonly ReadOnlyMemory<byte> APCommand = "AP"u8.ToArray();
    private readonly ReadOnlyMemory<byte> ALCommand = "AL"u8.ToArray();
    public async ValueTask SetTrackingAsync(bool tracking, CancellationToken cancellationToken)
    {
        if (await IsPulseGuidingAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Cannot set tracking={tracking} while pulse guiding");
        }

        if (await IsSlewingAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Cannot set tracking={tracking} while slewing");
        }

        await SendWithoutResponseAsync(tracking ? APCommand : ALCommand, cancellationToken);
    }

    public async ValueTask<AlignmentMode> GetAlignmentAsync(CancellationToken cancellationToken)
    {
        var (mode, _, _) = await AlignmentDetailsAsync(cancellationToken);
        return mode;
    }

    private static readonly ReadOnlyMemory<byte> GWCommand = "GW"u8.ToArray();
    private async ValueTask<(AlignmentMode Mode, bool Tracking, int AlignmentStars)> AlignmentDetailsAsync(CancellationToken cancellationToken)
    {
        // TODO LX800 fixed GW response not being terminated, account for that
        var response = await SendAndReceiveExactlyAsync(GWCommand, 3, cancellationToken);
        if (response is { Length: 3 })
        {
            var mode = response[0] switch
            {
                'A' => AlignmentMode.AltAz,
                'P' => AlignmentMode.Polar,
                'G' => AlignmentMode.GermanPolar,
                var invalid => throw new InvalidOperationException($"Invalid alginment mode {invalid} returned")
            };

            var tracking = response[1] == 'T';

            var alignmentStars = response[2] switch
            {
                '1' => 1,
                '2' => 2,
                _ => 0
            };

            return (mode, tracking, alignmentStars);
        }
        else
        {
            throw new InvalidOperationException($"Failed to parse :GW# response {response}");
        }
    }

    public ValueTask<bool> AtHomeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<bool> AtParkAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);

    private static readonly ReadOnlyMemory<byte> DCommand = "D"u8.ToArray();
    /// <summary>
    /// Uses :D# to check if mount is slewing (use this to update slewing state)
    /// </summary>
    public async ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken)
    {
        using var response = ArrayPoolHelper.Rent<byte>(10);

        var bytesRead = await SendAndReceiveRawAsync(DCommand, response, cancellationToken);
        var isSlewing = bytesRead is >= 1 && response[0] is (byte)'|' or 0x7f;

        return isSlewing;
    }

    public async ValueTask<DateTime?> TryGetUTCDateFromMountAsync(CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            return null;
        }

        var localDate = await GetLocalDateAsync(cancellationToken);
        var localTime = await GetLocalTimeAsync(cancellationToken);
        var utcOffset = await GetUtcCorrectionAsync(cancellationToken);
        return DateTime.SpecifyKind(localDate.Add(localTime).Add(utcOffset), DateTimeKind.Utc);
    }

    public async ValueTask SetUTCDateAsync(DateTime value, CancellationToken cancellationToken)
    {
        var utcOffset = await GetUtcCorrectionAsync(cancellationToken);
        if (!(value is { Kind: DateTimeKind.Utc } utcDate) || _deviceInfo.SerialDevice is not { IsOpen: true } port)
        {
            return;
        }

        using var buffer = ArrayPoolHelper.Rent<byte>(2 + 8);
        try
        {
            var adjustedDateTime = utcDate - utcOffset;

            // acquire lock in this method directly as we might potentially send out two read commands
            await port.WaitAsync(cancellationToken);

            "SL"u8.CopyTo(buffer);

            if (!adjustedDateTime.TryFormat(buffer.AsSpan(2), out _, "HH:mm:ss", CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException($"Failed to convert {value} to HH:mm:ss");
            }

            await SendAsync(port, buffer, cancellationToken);

            var slResponse = await port.TryReadTerminatedAsync(Terminators, cancellationToken);
            if (slResponse is "1")
            {
                throw new ArgumentException($"Failed to set date to {value}, command was {_encoding.GetString(buffer)} with response {slResponse}", nameof(value));
            }

            "SC"u8.CopyTo(buffer);

            if (!adjustedDateTime.TryFormat(buffer.AsSpan(2), out _, "MM/dd/yy", CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException($"Failed to convert {value} to MM/dd/yy");
            }

            await SendAsync(port, buffer, cancellationToken);

            var scResponse = await port.TryReadTerminatedAsync(Terminators, cancellationToken);
            if (scResponse is "1")
            {
                throw new ArgumentException($"Failed to set date to {value}, command was {_encoding.GetString(buffer)} with response {scResponse}", nameof(value));
            }

            //throwing away these two strings which represent
            //Updating Planetary Data#
            //                       #
            TimeIsSetByUs = await port.TryReadTerminatedAsync(Terminators, cancellationToken) is not null
                && await port.TryReadTerminatedAsync(Terminators, cancellationToken) is not null;
        }
        finally
        {
            port.Release();
        }
    }

    private static readonly ReadOnlyMemory<byte> GCCommand = "GC"u8.ToArray();
    private async ValueTask<DateTime> GetLocalDateAsync(CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync(GCCommand, cancellationToken);

        if (DateTime.TryParseExact(response, "MM/dd/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
        {
            return date;
        }

        throw new InvalidOperationException($"Could not parse response {response} of GC (get local date)");
    }

    private static readonly ReadOnlyMemory<byte> GLCommand = "GL"u8.ToArray();
    private async ValueTask<TimeSpan> GetLocalTimeAsync(CancellationToken cancellationToken)
    {
        using var response = ArrayPoolHelper.Rent<byte>(10);

        var bytesRead = await SendAndReceiveRawAsync(GLCommand, response, cancellationToken);

        if (bytesRead > 0 && Utf8Parser.TryParse(response.AsSpan(0, bytesRead), out TimeSpan time, out _))
        {
            return time.Modulo24h();
        }

        throw new InvalidOperationException($"Could not parse response {_encoding.GetString(response)} of GL (get local time)");
    }

    private static readonly ReadOnlyMemory<byte> GGCommand = "GG"u8.ToArray();
    private async ValueTask<TimeSpan> GetUtcCorrectionAsync(CancellationToken cancellationToken)
    {
        // :GG# Get UTC offset time
        // Returns: sHH# or sHH.H#
        // The number of decimal hours to add to local time to convert it to UTC. If the number is a whole number the
        // sHH# form is returned, otherwise the longer form is returned.
        var response = await SendAndReceiveAsync(GGCommand, cancellationToken);
        if (double.TryParse(response, out var offsetHours))
        {
            return TimeSpan.FromHours(offsetHours);
        }

        throw new InvalidOperationException($"Could not parse response {response} of GG (get UTC offset)");
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
    ///   <term>PointingState</term>
    ///   <description>is the calculated side of pier from the mount position (if slewing),
    /// or the last known value after slewing (as the mount auto-flips)</description>
    /// </item>
    /// </list>
    /// </summary>
    /// <param name="ra">current RA</param>
    /// <returns>Side of pier calculation result</returns>
    private async ValueTask<(PointingState PointingState, double LST)> CheckPointingStateAsync(double ra, CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            return (PointingState.Unknown, double.NaN);
        }

        var isSlewing = await IsSlewingAsync(cancellationToken);

        var (raSideOfPier, lst) = await CalculateSideOfPierAsync(ra, cancellationToken);

        return (isSlewing ? raSideOfPier : PointingState.Normal, lst);
    }

    public async ValueTask<PointingState> GetSideOfPierAsync(CancellationToken cancellationToken)
    {
        var (pointingState, _) = await CheckPointingStateAsync(await GetRightAscensionAsync(cancellationToken), cancellationToken);

        return pointingState;
    }

    public ValueTask SetSideOfPierAsync(PointingState pointingState, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Setting side of pier is not supported");

    private static readonly ReadOnlyMemory<byte> GSCommand = "GS"u8.ToArray();
    public async ValueTask<double> GetSiderealTimeAsync(CancellationToken cancellationToken)
    {
        using var response = ArrayPoolHelper.Rent<byte>(10);

        var bytesRead = await SendAndReceiveRawAsync(GSCommand, response, cancellationToken);

        if (bytesRead > 0 && Utf8Parser.TryParse(response.AsSpan(0, bytesRead), out TimeSpan time, out _))
        {
            return time.Modulo24h().TotalHours;
        }

        throw new InvalidOperationException($"Could not parse response {_encoding.GetString(response)} of GS (get sidereal time)");
    }

    public async ValueTask<double> GetRightAscensionAsync(CancellationToken cancellationToken)
    {
        var (ra, _) = await GetRightAscensionWithPrecisionAsync(target: false, cancellationToken);
        return ra;
    }

    public async ValueTask<double> GetDeclinationAsync(CancellationToken cancellationToken)
    {
        var (dec, _) = await GetDeclinationWithPrecisionAsync(target: false, cancellationToken);
        return dec;
    }

    public async ValueTask<double> GetTargetRightAscensionAsync(CancellationToken cancellationToken)
    {
        var (ra, _) = await GetRightAscensionWithPrecisionAsync(target: true, cancellationToken);
        return ra;
    }

    private async ValueTask SetTargetRightAscensionAsync(double value, CancellationToken cancellationToken)
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
        var (ra, highPrecision) = await GetRightAscensionWithPrecisionAsync(target: false, cancellationToken);

        // convert decimal hours to HH:MM.T (classic LX200 RA Notation) if low precision. T is the decimal part of minutes which is converted into seconds
        var targetHms = TimeSpan.FromHours(Math.Abs(value)).Round(highPrecision ? TimeSpanRoundingType.Second : TimeSpanRoundingType.TenthMinute).Modulo24h();

        const int offset = 2;
        using var buffer = ArrayPoolHelper.Rent<byte>(2 + 2 + 2 + 2 + 1 + (highPrecision ? 1 : 0));

        "Sr"u8.CopyTo(buffer);

        if (targetHms.Hours.TryFormat(buffer.AsSpan(offset), out int hoursWritten, "00", CultureInfo.InvariantCulture)
            && offset + hoursWritten + 1 is int minOffset && minOffset < buffer.Length
            && targetHms.Minutes.TryFormat(buffer.AsSpan(minOffset), out int minutesWritten, "00", CultureInfo.InvariantCulture)
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
            if (!targetHms.Seconds.TryFormat(buffer.AsSpan(secOffset), out _, "00", CultureInfo.InvariantCulture))
            {
                throw new ArgumentException($"Failed to convert {value} to high precision seconds", nameof(value));
            }
        }
        else
        {
            buffer[secOffset - 1] = (byte)'.';
            if (!(targetHms.Seconds / 6).TryFormat(buffer.AsSpan(secOffset), out _, "0", CultureInfo.InvariantCulture))
            {
                throw new ArgumentException($"Failed to convert {value} to low precision tenth of minute", nameof(value));
            }
        }

        var response = await SendAndReceiveExactlyAsync(buffer, 1, cancellationToken);

        if (response != "1")
        {
            throw new InvalidOperationException($"Failed to set target right ascension to {HoursToHMS(value)}, using command {_encoding.GetString(buffer)}, response={response}");
        }
#if TRACE
        External.AppLogger.LogTrace("Set target right ascension to {TargetRightAscension}, current right ascension is {RightAscension}, high precision={HighPrecision}",
            HoursToHMS(value), HoursToHMS(ra), highPrecision);
#endif
    }

    public async ValueTask<double> GetTargetDeclinationAsync(CancellationToken cancellationToken)
    {
        var (dec, _) = await GetDeclinationWithPrecisionAsync(target: true, cancellationToken);
        return dec;
    }

    private async ValueTask SetTargetDeclinationAsync(double targetDec, CancellationToken cancellationToken)
    {
        if (targetDec > 90)
        {
            throw new ArgumentException("Target declination cannot be greater than 90 degrees.", nameof(targetDec));
        }

        if (targetDec < -90)
        {
            throw new ArgumentException("Target declination cannot be lower than -90 degrees.", nameof(targetDec));
        }

        // :SdsDD*MM#    for low precision
        // :SdsDD*MM:SS# for high precision
        var (dec, highPrecision) = await GetDeclinationWithPrecisionAsync(target: false, cancellationToken);

        var sign = Math.Sign(targetDec);
        var signLength = sign is -1 ? 1 : 0;
        var degOffset = 2 + signLength;
        var minOffset = degOffset + 2 + 1;
        var targetDms = TimeSpan.FromHours(Math.Abs(targetDec))
            .Round(highPrecision ? TimeSpanRoundingType.Second : TimeSpanRoundingType.Minute)
            .EnsureMax(TimeSpan.FromHours(90));

        using var buffer = ArrayPoolHelper.Rent<byte>(minOffset + 2 +(highPrecision ? 3 : 0));

        "Sd"u8.CopyTo(buffer);

        if (sign is -1)
        {
            buffer[degOffset - 1] = (byte)'-';
        }

        buffer[minOffset - 1] = (byte)'*';

        if (targetDms.Hours.TryFormat(buffer.AsSpan(degOffset), out _, "00", CultureInfo.InvariantCulture)
            && targetDms.Minutes.TryFormat(buffer.AsSpan(minOffset), out _, "00", CultureInfo.InvariantCulture)
        )
        {
            if (highPrecision)
            {
                var secOffset = minOffset + 2 + 1;
                buffer[secOffset - 1] = (byte)':';

                if (!targetDms.Seconds.TryFormat(buffer.AsSpan(secOffset), out _, "00", CultureInfo.InvariantCulture))
                {
                    throw new ArgumentException($"Failed to convert value {targetDec} to DMS (high precision)", nameof(targetDec));
                }
            }
        }
        else
        {
            throw new ArgumentException($"Failed to convert value {targetDec} to DM", nameof(targetDec));
        }

        var response = await SendAndReceiveExactlyAsync(buffer, 1, cancellationToken);

        if (response is not "1")
        {
            throw new InvalidOperationException($"Failed to set target declination to {DegreesToDMS(targetDec)}, using command {_encoding.GetString(buffer)}, response={response}");
        }

#if TRACE
        External.AppLogger.LogTrace("Set target declination to {TargetDeclination}, current declination is {Declination}, high precision={HighPrecision}",
            DegreesToDMS(targetDec), DegreesToDMS(dec), highPrecision);
#endif
    }

    private readonly ReadOnlyMemory<byte> GrCommand = "Gr"u8.ToArray();
    private readonly ReadOnlyMemory<byte> GRCommand = "GR"u8.ToArray();
    private async ValueTask<(double RightAscension, bool HighPrecision)> GetRightAscensionWithPrecisionAsync(bool target, CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync(target ? GrCommand : GRCommand, cancellationToken);

        if (response is not { })
        {
            throw new InvalidOperationException($"No response received for right ascension query target={target}");
        }
        var ra = HmsOrHmTToHours(response, out var highPrecision);

        return (ra, highPrecision);
    }

    private readonly ReadOnlyMemory<byte> GdCommand = "Gd"u8.ToArray();
    private readonly ReadOnlyMemory<byte> GDCommand = "GD"u8.ToArray();
    private async ValueTask<(double Declination, bool HighPrecision)> GetDeclinationWithPrecisionAsync(bool target, CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync(target ? GdCommand : GDCommand, cancellationToken);

        if (response is not { })
        {
            throw new InvalidOperationException($"No response received for declination query target={target}");
        }
        var dec = DMSToDegree(response.Replace('\xdf', ':'));

        return (dec, response.Length >= 7);
    }


    /// <summary>
    /// convert a HH:MM.T (classic LX200 RA Notation) string to a double hours. T is the decimal part of minutes which is converted into seconds
    /// </summary>
    private static double HmsOrHmTToHours(string hm, out bool highPrecision)
    {
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

    public ValueTask<double> GetSiteElevationAsync(CancellationToken cancellationToken) => ValueTask.FromResult(double.NaN);

    public ValueTask SetSiteElevationAsync(double elevation, CancellationToken cancellationToken)
    {
        // todo: store this somewhere

        return ValueTask.CompletedTask;
    }

    private readonly ReadOnlyMemory<byte> GtCommand = "Gt"u8.ToArray();
    public ValueTask<double> GetSiteLatitudeAsync(CancellationToken cancellationToken)
    {
        return GetLatOrLongAsync(GtCommand, cancellationToken);
    }

    public async ValueTask SetSiteLatitudeAsync(double latitude, CancellationToken cancellationToken)
    {
        if (latitude > 90)
        {
            throw new ArgumentException("Site latitude cannot be greater than 90 degrees.", nameof(latitude));
        }

        if (latitude < -90)
        {
            throw new ArgumentException("Site latitude cannot be lower than -90 degrees.", nameof(latitude));
        }

        var abs = Math.Abs(latitude);
        var dms = TimeSpan.FromHours(abs).Round(TimeSpanRoundingType.Minute).EnsureMax(TimeSpan.FromHours(90));

        var needsSign = latitude < 0;
        const int cmdLength = 2;
        var offset = cmdLength + (needsSign ? 1 : 0);

        using var buffer = ArrayPoolHelper.Rent<byte>(offset + 1 + 2);

        "St"u8.CopyTo(buffer);

        if (needsSign)
        {
            buffer[cmdLength] = (byte)'-';
        }

        if (dms.Hours.TryFormat(buffer.AsSpan(offset), out var degWritten, format: "00", provider: CultureInfo.InvariantCulture)
            && dms.Minutes.TryFormat(buffer.AsSpan(offset + degWritten + 1), out _, format: "00", provider: CultureInfo.InvariantCulture)
        )
        {
            buffer[offset + degWritten] = (byte)'*';

            var response = await SendAndReceiveAsync(buffer, cancellationToken);

            if (response is "1")
            {
                External.AppLogger.LogInformation("Updated site latitude to {Degrees}", latitude);
            }
            else
            {
                throw new InvalidOperationException($"Cannot update site latitude to {latitude} due to connectivity issue/command invalid: {response}");
            }
        }
        else
        {
            throw new InvalidOperationException($"Cannot update site latitude to {latitude} due to formatting error");
        }
    }

    private static readonly ReadOnlyMemory<byte> GgCommand = "Gg"u8.ToArray();
    public async ValueTask<double> GetSiteLongitudeAsync(CancellationToken cancellationToken)
    {
        return -1 * await GetLatOrLongAsync(GgCommand, cancellationToken);
    }

    public async ValueTask SetSiteLongitudeAsync(double value, CancellationToken cancellationToken)
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
        using var buffer = ArrayPoolHelper.Rent<byte>(offset + 3 + 1 + 2);
        "Sg"u8.CopyTo(buffer);

        if (adjustedDegrees.TryFormat(buffer.AsSpan(offset), out var degWritten, format: "000", provider: CultureInfo.InvariantCulture)
            && dms.Minutes.TryFormat(buffer.AsSpan(offset + degWritten + 1), out _, format: "00", provider: CultureInfo.InvariantCulture)
        )
        {
            buffer[offset + degWritten] = (byte)'*';

            var response = await SendAndReceiveAsync(buffer, cancellationToken);

            if (response is "1")
            {
                External.AppLogger.LogInformation("Updated site longitude to {Degrees}", value);
            }
            else
            {
                throw new InvalidOperationException($"Cannot update site longitude to {value} due to connectivity issue/command invalid: {response}");
            }
        }
        else
        {
            throw new InvalidOperationException($"Cannot update site longitude to {value} due to formatting error");
        }
    }

    private async ValueTask<double> GetLatOrLongAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken)
    {
        using var response = ArrayPoolHelper.Rent<byte>(10);
        if (await SendAndReceiveRawAsync(command, response, cancellationToken) >= 5)
        {
            var isNegative = response[0] is (byte)'-';
            var offset = isNegative ? 1 : 0;

            if (Utf8Parser.TryParse(response.AsSpan(offset), out int degrees, out var consumed)
                && Utf8Parser.TryParse(response.AsSpan(offset + consumed + 1), out int minutes, out _)
            )
            {
                var latOrLongNotAdjusted = (isNegative ? -1 : 1) * (degrees + minutes / 60d);
                // adjust s.th. 214 from mount becomes -214 and then becomes 146
                return latOrLongNotAdjusted >= -180 ? latOrLongNotAdjusted : latOrLongNotAdjusted + 360;
            }
        }

        throw new InvalidOperationException($"Failed to parse response of {_encoding.GetString(command.Span)}");
    }

    public override string? DriverInfo => $"{_telescopeName} ({_telescopeFW})";

    public override string? Description => $"{_telescopeName} driver based on the LX200 serial protocol v2010.10, firmware: {_telescopeFW}";

    public async ValueTask<PointingState> DestinationSideOfPierAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        var (sideOfPier, _) = await CalculateSideOfPierAsync(ra, cancellationToken);
        return sideOfPier;
    }

    private async ValueTask<bool> IsSouthernHemisphereAsync(CancellationToken cancellationToken)
        => _isSouthernHemisphere ??= await GetSiteLatitudeAsync(cancellationToken) < 0;

    public async ValueTask<double> GetHourAngleAsync(CancellationToken cancellationToken)
    {
        var lst = await GetSiderealTimeAsync(cancellationToken);
        var ra = await GetRightAscensionAsync(cancellationToken);
        return ConditionHA(lst - ra);
    }

    private async ValueTask<(PointingState PointingState, double SiderealTime)> CalculateSideOfPierAsync(double ra, CancellationToken cancellationToken)
    {
        var lst = await GetSiderealTimeAsync(cancellationToken);
        var pointingState = ConditionHA(lst - ra) switch
        {
            >= 0 => PointingState.Normal,
            < 0 => PointingState.ThroughThePole,
            _ => PointingState.Unknown
        };

        return (pointingState, lst);
    }

    private readonly ReadOnlyMemory<byte> ParkCommand = "hP"u8.ToArray();
    public async ValueTask ParkAsync(CancellationToken cancellationToken = default)
    {
        await SendWithoutResponseAsync(ParkCommand, cancellationToken);
    }

    public async ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("Timespan must be greater than 0", nameof(duration));
        }

        using var buffer = ArrayPoolHelper.Rent<byte>(2 + 1 + 4);

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

        if (!await IsTrackingAsync(cancellationToken))
        {
            throw new InvalidOperationException("Cannot pulse guide when tracking is off");
        }

        if (ms.TryFormat(buffer.AsSpan(3), out _, "0000", CultureInfo.InvariantCulture))
        {
            await SendWithoutResponseAsync(buffer, cancellationToken);
        }
        else
        {
            throw new ArgumentException($"Failed to create request for given duration={duration} message={_encoding.GetString(buffer)}", nameof(duration));
        }
    }

    private static readonly ReadOnlyMemory<byte> SlewCommand = "MS"u8.ToArray();
    /// <summary>
    /// Sets target coordinates to (<paramref name="ra"/>,<paramref name="dec"/>), using <see cref="EquatorialSystem"/>.
    /// </summary>
    /// <param name="ra"></param>
    /// <param name="dec"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public async ValueTask BeginSlewRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        if (await IsPulseGuidingAsync(cancellationToken))
        {
            throw new InvalidOperationException("Cannot slew while pulse-guiding");
        }

        if (await IsSlewingAsync(cancellationToken))
        {
            throw new InvalidOperationException("Cannot slew while a slew is still ongoing");
        }

        if (await AtParkAsync(cancellationToken))
        {
            throw new InvalidOperationException("Mount is parked");
        }

        await SetTargetRightAscensionAsync(ra, cancellationToken);
        await SetTargetDeclinationAsync(dec, cancellationToken);

        // acquire lock in this method directly as we might potentially send out two read commands
        if (_deviceInfo.SerialDevice is { IsOpen: true } port)
        {
            await port.WaitAsync(cancellationToken);
            try
            {
                await SendAsync(port, SlewCommand, cancellationToken);
                var response = await port.TryReadExactlyAsync(1, cancellationToken);

                if (response is "0")
                {
#if TRACE
                    External.AppLogger.LogTrace("Slewing to {RA},{Dec}", HoursToHMS(ra), DegreesToDMS(dec));
#endif
                }
                else if (response is { Length: 1 }
                    && byte.TryParse(response[0..1], out var reasonCode)
                    && (await port.TryReadTerminatedAsync(Terminators, cancellationToken) is { } reasonMessage)
                )
                {
                    var reason = reasonCode switch
                    {
                        1 => "below horizon limit",
                        2 => "above hight limit",
                        _ => $"unknown reason {reasonCode}: {reasonMessage}"
                    };

                    throw new InvalidOperationException($"Failed to slew to {HoursToHMS(ra)},{DegreesToDMS(dec)} due to {reason} message={reasonCode}{reasonMessage}");
                }
                else
                {
                    throw new InvalidOperationException($"Failed to slew to {HoursToHMS(ra)},{DegreesToDMS(dec)} due to an unrecognized response: {response}");
                }
            }
            finally
            { 
                port.Release();
            }
        }
        else
        {
            throw new InvalidOperationException("Serial port is not connected");
        }
    }

    private readonly ReadOnlyMemory<byte> QCommand = "Q"u8.ToArray();
    /// <summary>
    /// Returns true if mount is not slewing or an ongoing slew was aborted successfully.
    /// Does not disable pulse guiding
    /// TODO: Verify :Q# stops pulse guiding as well
    /// </summary>
    /// <returns></returns>
    public async ValueTask AbortSlewAsync(CancellationToken cancellationToken)
    {
        if (await IsPulseGuidingAsync(cancellationToken))
        {
            throw new InvalidOperationException("Cannot abort slewing while pulse guiding");
        }

        if (await IsSlewingAsync(cancellationToken))
        {
            await SendWithoutResponseAsync(QCommand, cancellationToken);
            // StartSlewTimer(MOVING_STATE_NORMAL);
        }
    }

    private static readonly ReadOnlyMemory<byte> CMCommand = "CM"u8.ToArray();
    /// <summary>
    /// Does not allow sync across the meridian
    /// </summary>
    /// <param name="ra"></param>
    /// <param name="dec"></param>
    /// <returns></returns>
    public async ValueTask SyncRaDecAsync(double ra, double dec, CancellationToken cancellationToken)
    {
        var pointingState = await GetSideOfPierAsync(cancellationToken);
        if (pointingState is PointingState.Unknown or PointingState.ThroughThePole)
        {
            throw new InvalidOperationException($"Cannot sync across meridian (current side of pier: {pointingState}) given {HoursToHMS(ra)},{DegreesToDMS(dec)}");
        }

        var response = await SendAndReceiveAsync(CMCommand, cancellationToken);

        if (response is not { Length: > 0 })
        {
            throw new InvalidOperationException($"Failed to sync {HoursToHMS(ra)},{DegreesToDMS(dec)}");
        }
    }

    public ValueTask UnparkAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Unparking is not supported");

    protected override Task<(bool Success, int ConnectionId, MountDeviceInfo DeviceInfo)> DoConnectDeviceAsync(CancellationToken cancellationToken)
    {
        ISerialConnection? serialDevice;
        try
        {
            if (_device.ConnectSerialDevice(External, encoding: _encoding, ioTimeout: TimeSpan.FromMilliseconds(500)) is { IsOpen: true } openedConnection)
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
            External.AppLogger.LogError(ex, "Error when connecting to serial port {DeviceUri}", _device.DeviceUri);
        }

        if (serialDevice is not null)
        {
            return Task.FromResult((true, CONNECTION_ID_EXCLUSIVE, new MountDeviceInfo(serialDevice)));
        }
        else
        {
            return Task.FromResult((false, CONNECTION_ID_UNKNOWN, default(MountDeviceInfo)));
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
            else
            {
                return false;
            }
        }
        return false;
    }

    private static readonly ReadOnlyMemory<byte> GVPCommand = "GVP"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> GVNCommand = "GVN"u8.ToArray();
    protected override async ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var name = await SendAndReceiveAsync(GVPCommand, cancellationToken);

            var fw = await SendAndReceiveAsync(GVNCommand, cancellationToken);

            if (name is not { } || fw is not { })
            {
                return false;
            }

            _telescopeName = name;
            _telescopeFW = fw;

            if (!await TrySetHighPrecisionAsync(cancellationToken))
            {
                External.AppLogger.LogWarning("Failed to set high precision via :U#");
            }

            return true;
        }
        catch (Exception e)
        {
            External.AppLogger.LogError(e, "Failed to initialize mount");

            return false;
        }
    }

    private static readonly ReadOnlyMemory<byte> UCommand = "U"u8.ToArray();
    private async ValueTask<bool> TrySetHighPrecisionAsync(CancellationToken cancellationToken)
    {
        bool highPrecision;
        int tries = 0;
        do
        {
            (_, highPrecision) = await GetRightAscensionWithPrecisionAsync(target: false, cancellationToken);

            if (highPrecision)
            {
                return true;
            }
            else
            {
               await SendWithoutResponseAsync(UCommand, cancellationToken);
            }
        } while (!highPrecision && ++tries < 3);

        return false;
    }

    #region Serial I/O
    private static readonly ReadOnlyMemory<byte> Terminators = "#\0"u8.ToArray();

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private async ValueTask<string?> SendAndReceiveAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken)
    {
        if (_deviceInfo.SerialDevice is { IsOpen: true } port)
        {
            await port.WaitAsync(cancellationToken);
            try
            {
                await SendAsync(port, command, cancellationToken);

                return await port.TryReadTerminatedAsync(Terminators, cancellationToken);
            }
            finally
            {
                port.Release();
            }
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private async ValueTask<int> SendAndReceiveRawAsync(ReadOnlyMemory<byte> command, Memory<byte> response, CancellationToken cancellationToken)
    {
        if (_deviceInfo.SerialDevice is { IsOpen: true } port)
        {
            await port.WaitAsync(cancellationToken);
            try
            {
                await SendAsync(port, command, cancellationToken);

                return await port.TryReadTerminatedRawAsync(response, Terminators, cancellationToken);
            }
            finally
            {
                port.Release();
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private async ValueTask<string?> SendAndReceiveExactlyAsync(ReadOnlyMemory<byte> command, int count, CancellationToken cancellationToken)
    {
        if (_deviceInfo.SerialDevice is { IsOpen: true } port)
        {
            await port.WaitAsync(cancellationToken);
            try
            {
                await SendAsync(port, command, cancellationToken);

                return await port.TryReadExactlyAsync(count, cancellationToken);
            }
            finally
            {
                port.Release();
            }
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private async ValueTask SendWithoutResponseAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken)
    {
        if (_deviceInfo.SerialDevice is { IsOpen: true } port)
        {
            await port.WaitAsync(cancellationToken);
            try
            {
                await SendAsync(port, command, cancellationToken);
            }
            finally
            {
                port.Release();
            }
        }
        else
        {
            throw new InvalidOperationException("Mount is not connected");
        }
    }

    /// <summary>
    /// Assumes that port is checked for open state, and a lock is acquired.
    /// </summary>
    /// <param name="port"></param>
    /// <param name="command"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private async ValueTask SendAsync(ISerialConnection port, ReadOnlyMemory<byte> command, CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Mount is not connected");
        }

        if (!CanUnpark && !CanSetPark && await AtParkAsync(cancellationToken))
        {
            throw new InvalidOperationException("Mount is parked, but it is not possible to unpark it");
        }

        using var raw = ArrayPoolHelper.Rent<byte>(command.Length + 2);

        raw[0] = (byte)':';
        command.Span.CopyTo(raw.AsSpan(1));
        raw[^1] = (byte)'#';

        if (!await port.TryWriteAsync(raw, cancellationToken))
        {
            throw new InvalidOperationException($"Failed to send raw message {_encoding.GetString(raw)}");
        }
    }
    #endregion
}
