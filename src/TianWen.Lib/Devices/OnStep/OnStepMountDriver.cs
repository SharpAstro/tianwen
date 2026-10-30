using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.OnStep;

/// <summary>
/// OnStep / OnStepX mount driver. Extends the LX200 base with OnStep's richer
/// command set: <c>:GU#</c> bundled status, <c>:Gm#</c> hardware pier-side,
/// <c>:hR#</c> unpark, <c>:hQ#</c> set-park, <c>:Te#</c>/<c>:Td#</c> tracking
/// enable/disable with response, and the <c>:TK#</c>/<c>:TS#</c> King/Solar
/// tracking rates.
///
/// Generic over <typeparamref name="TDevice"/> to mirror the LX200 base class
/// pattern: lets <see cref="Fake.FakeOnStepMountDriver"/> subclass with
/// <c>FakeDevice</c> while production uses <see cref="OnStepDevice"/>.
/// </summary>
internal class OnStepMountDriver<TDevice>(TDevice device, IServiceProvider serviceProvider)
    : MeadeLX200ProtocolMountDriverBase<TDevice>(device, serviceProvider)
    where TDevice : DeviceBase
{
    // OnStep advertises native park / unpark / set-park
    public override bool CanSetPark => true;
    public override bool CanUnpark => true;

    // OnStep supports Sidereal, Lunar, Solar (`:TS#`) and King (`:TK#`) tracking rates.
    public override IReadOnlyList<TrackingSpeed> TrackingSpeeds =>
        [TrackingSpeed.Sidereal, TrackingSpeed.Lunar, TrackingSpeed.Solar, TrackingSpeed.King];

    private static readonly ReadOnlyMemory<byte> TQCommand = "TQ"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> TLCommand = "TL"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> TSCommand = "TS"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> TKCommand = "TK"u8.ToArray();
    public override ValueTask SetTrackingSpeedAsync(TrackingSpeed value, CancellationToken cancellationToken)
    {
        var speed = value switch
        {
            TrackingSpeed.Sidereal => TQCommand,
            TrackingSpeed.Lunar => TLCommand,
            TrackingSpeed.Solar => TSCommand,
            TrackingSpeed.King => TKCommand,
            _ => throw new ArgumentException($"Tracking speed {value} is not supported by OnStep", nameof(value))
        };

        return SendWithoutResponseAsync(speed, cancellationToken);
    }

    /// <summary>
    /// The move rates offered: from a quarter of sidereal, the slowest OnStep's own steps go, to the mount's own top speed,
    /// read on connect (<see cref="ReadFastestMoveAsync"/>), or 48x where it cannot be read. A move asks for its rate
    /// exactly (<see cref="MoveRateCommandFor"/>), so anything between is run as asked.
    /// </summary>
    internal const double SlowestMoveSidereal = 0.25, FallbackFastestMoveSidereal = 48;

    private double _fastestMoveDegPerSec = MountJog.RateDegPerSec(FallbackFastestMoveSidereal);

    public override bool CanMoveAxis(TelescopeAxis axis) => axis is TelescopeAxis.Primary or TelescopeAxis.Seconary;

    public override IReadOnlyList<AxisRate> AxisRates(TelescopeAxis axis) => CanMoveAxis(axis)
        ? [new AxisRate(MountJog.RateDegPerSec(SlowestMoveSidereal), Volatile.Read(ref _fastestMoveDegPerSec))]
        : [];

    private static readonly ReadOnlyMemory<byte> GXE6Command = "GXE6"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> GX92Command = "GX92"u8.ToArray();

    /// <summary>How long the two optional configuration reads wait: a firmware that does not know them is not a lost reply.</summary>
    private static readonly TimeSpan ConfigurationReplyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The mount's top slew speed in degrees a second, as the ASCOM OnStep driver works it out on connect (3.18):
    /// <c>1000000 / :GX92# / :GXE6# / 240</c>, the configured top rate (microseconds a step) over the steps a second. It
    /// was 1.88 on the TeeSeek, N.I.N.A.'s jog speed. Null when either is not answered or makes no sense.
    /// </summary>
    internal static double? FastestMoveDegPerSec(string? gxe6, string? gx92)
        => double.TryParse(gxe6, NumberStyles.Float, CultureInfo.InvariantCulture, out var stepsPerSecond) && stepsPerSecond > 0
            && double.TryParse(gx92, NumberStyles.Float, CultureInfo.InvariantCulture, out var microsecondsPerStep) && microsecondsPerStep >= 1
            && 1_000_000.0 / (int)microsecondsPerStep / stepsPerSecond / 240.0 is var degPerSec && degPerSec > 0 && double.IsFinite(degPerSec)
                ? degPerSec
                : null;

    private async ValueTask ReadFastestMoveAsync(CancellationToken cancellationToken)
    {
        var gxe6 = await SendAndReceiveAsync(GXE6Command, cancellationToken, ConfigurationReplyTimeout);
        var gx92 = gxe6 is null ? null : await SendAndReceiveAsync(GX92Command, cancellationToken, ConfigurationReplyTimeout);
        if (FastestMoveDegPerSec(gxe6, gx92) is { } fastest)
        {
            Volatile.Write(ref _fastestMoveDegPerSec, fastest);
            Logger.LogInformation("{Device} moves an axis at up to {Rate:F2} degrees a second ({Sidereal:F0}x sidereal)",
                _device.DisplayName, fastest, fastest / MountJog.RateDegPerSec(1));
        }
        else
        {
            Logger.LogInformation("{Device} did not give its top speed (:GXE6# {Gxe6}, :GX92# {Gx92}); moves stay at or below {Sidereal}x sidereal",
                _device.DisplayName, gxe6 ?? "nothing", gx92 ?? "nothing", FallbackFastestMoveSidereal);
        }
    }

    /// <summary>
    /// <c>:RAd.dddddddd#</c> or <c>:REd.dddddddd#</c>, the axis's rate in degrees a second, as the ASCOM OnStep driver
    /// sets it (<c>MoveAxis</c>, 3.18). The step rates <c>:R0#</c> to <c>:R9#</c> did not move the TeeSeek's 4.24 at all.
    /// </summary>
    protected override ReadOnlyMemory<byte> MoveRateCommandFor(TelescopeAxis axis, double rateDegPerSec)
        => System.Text.Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{(axis is TelescopeAxis.Primary ? "RA" : "RE")}{rateDegPerSec:0.00000000}"));

    /// <summary>
    /// Connects as the LX200 base does, then sets the mount's clock from ours. OnStep has no clock of its own to trust:
    /// the TeeSeek's sat 11 hours off (the site's UTC offset), so a goto to Venus high in the west was refused as below
    /// the horizon. A clock that cannot be set is logged, not a failed connect: the mount can still be read and moved.
    /// </summary>
    protected override async ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        if (!await base.InitDeviceAsync(cancellationToken))
        {
            return false;
        }

        try
        {
            await SetUTCDateAsync(TimeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogError(ex, "{Device}: could not set the mount's clock; its gotos and limits are wrong until it is", _device.DisplayName);
        }

        try
        {
            await ReadFastestMoveAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "{Device}: could not read its top speed; moves stay at or below {Sidereal}x sidereal", _device.DisplayName, FallbackFastestMoveSidereal);
        }
        return true;
    }

    private static readonly ReadOnlyMemory<byte> SCCommandPrefix = "SC"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> SLCommandPrefix = "SL"u8.ToArray();

    /// <summary>
    /// Sets OnStep's date and time as the ASCOM OnStep driver does (<c>UTCDate</c>, 3.18): the mount's OWN UTC offset is
    /// read (<c>:GG#</c>, Meade's hours ADDED to local time to give UTC) and left as it is, the local time it implies is
    /// sent as <c>:SC</c> then <c>:SL</c>, each acknowledged by one byte, <c>1</c> valid and <c>0</c> invalid, as the
    /// Meade document has it. Only UTC matters to gotos and limits, and an offset someone set on the handset is theirs.
    /// Whatever follows <c>:SC</c>'s ack (Meade's "Updating Planetary Data" lines) is discarded. Then the clock is read
    /// back, and a mount more than a minute out is a warning.
    /// </summary>
    public override async ValueTask SetUTCDateAsync(DateTime value, CancellationToken cancellationToken)
    {
        if (value.Kind is not DateTimeKind.Utc)
        {
            throw new ArgumentException($"The mount's clock is set from a UTC time, not {value.Kind}", nameof(value));
        }

        var mountOffset = await GetUtcCorrectionAsync(cancellationToken);
        var local = value - mountOffset;

        await SetAndCheckAsync(SCCommandPrefix, local.ToString("MM/dd/yy", CultureInfo.InvariantCulture), "date", cancellationToken);
        if (_deviceInfo.SerialDevice is { IsOpen: true } port)
        {
            using var @lock = await port.WaitAsync(cancellationToken);
            await TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            port.DiscardInBuffer();
        }
        await SetAndCheckAsync(SLCommandPrefix, local.ToString("HH:mm:ss", CultureInfo.InvariantCulture), "time", cancellationToken);
        TimeIsSetByUs = true;

        if (await TryGetUTCDateFromMountAsync(cancellationToken) is { } mountUtc
            && Math.Abs((mountUtc - value).TotalSeconds) > 60)
        {
            Logger.LogWarning("{Device}'s clock reads {MountUtc:u} UTC right after being set to {Utc:u}", _device.DisplayName, mountUtc, value);
        }
        else
        {
            Logger.LogInformation("{Device}'s clock set to {Utc:u} UTC (its local {Local:HH:mm:ss}, its UTC offset {Offset} h)", _device.DisplayName, value, local, mountOffset.TotalHours);
        }
    }

    private async ValueTask SetAndCheckAsync(ReadOnlyMemory<byte> prefix, string argument, string what, CancellationToken cancellationToken)
    {
        var command = new byte[prefix.Length + argument.Length];
        prefix.Span.CopyTo(command);
        System.Text.Encoding.ASCII.GetBytes(argument, command.AsSpan(prefix.Length));

        var ack = await SendAndReceiveExactlyAsync(command, 1, cancellationToken);
        if (ack is not "1")
        {
            throw new InvalidOperationException($"{_device.DisplayName} refused the {what} :{System.Text.Encoding.ASCII.GetString(command)}#, answering {ack ?? "nothing"}");
        }
    }

    /// <summary>
    /// OnStep answers a refused <c>:MS#</c> with the digit alone, no message: waiting for Meade's <c>#</c>-terminated
    /// message held the port's lock for good, and the mount read "busy" until the node restarted.
    /// </summary>
    protected override ValueTask<string?> ReadSlewRefusalMessageAsync(ISerialConnection port, CancellationToken cancellationToken)
        => ValueTask.FromResult<string?>(null);

    /// <summary>OnStep's <c>:MS#</c> codes, from its command reference.</summary>
    protected override string DescribeSlewRefusal(byte reasonCode, string? reasonMessage) => reasonCode switch
    {
        1 => "below the horizon limit",
        2 => "above the overhead limit",
        3 => "the controller is in standby",
        4 => "the mount is parked",
        5 => "a goto is already in progress",
        6 => "outside the limits (Dec, under-pole or meridian)",
        7 => "a hardware fault",
        8 => "the mount is already moving",
        _ => $"an unspecified error ({reasonCode})"
    };

    private static readonly ReadOnlyMemory<byte> TeCommand = "Te"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> TdCommand = "Td"u8.ToArray();
    public override async ValueTask SetTrackingAsync(bool tracking, CancellationToken cancellationToken)
    {
        if (await IsPulseGuidingAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Cannot set tracking={tracking} while pulse guiding");
        }

        if (await IsSlewingAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Cannot set tracking={tracking} while slewing");
        }

        // OnStep :Te# / :Td# return "0" or "1". 1 = success, 0 = failure.
        var response = await SendAndReceiveExactlyAsync(tracking ? TeCommand : TdCommand, 1, cancellationToken);
        if (response is not "1")
        {
            throw new InvalidOperationException($"OnStep refused tracking={tracking}, response={response ?? "<null>"}");
        }
    }

    public override async ValueTask<bool> IsTrackingAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        return status.IsTracking;
    }

    public override async ValueTask<bool> IsSlewingAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        return status.IsGotoInProgress;
    }

    public override async ValueTask<bool> AtParkAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        return status.IsParked;
    }

    private static readonly ReadOnlyMemory<byte> GmCommand = "Gm"u8.ToArray();
    /// <summary><c>:Gm#</c> is the controller's own answer, not a derivation.</summary>
    public override PointingStateSource PointingStateSource => PointingStateSource.Measured;

    public override async ValueTask<PointingState> GetSideOfPierAsync(CancellationToken cancellationToken)
    {
        // OnStep :Gm# returns single char: 'N' (none/parked/unaligned), 'E' (pier east), 'W' (pier west).
        var response = await SendAndReceiveAsync(GmCommand, cancellationToken);

        return response switch
        {
            "E" => PointingState.Normal,           // counterweight east, OTA looking west of meridian
            "W" => PointingState.ThroughThePole,   // counterweight west, OTA looking east of meridian
            _ => PointingState.Unknown
        };
    }

    private static readonly ReadOnlyMemory<byte> ParkCommand = "hP"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> UnparkCommand = "hR"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> SetParkCommand = "hQ"u8.ToArray();

    /// <summary>
    /// Park polling cadence: picked to match the base IsSlewingAsync ~250 ms cadence
    /// so a Park then poll-loop does not flood the serial line.
    /// </summary>
    private static readonly TimeSpan ParkPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Park-completion timeout. OnStep parks typically complete within 10-20 s for a
    /// nearby home position; allow generous headroom for far-from-home parks.
    /// </summary>
    private static readonly TimeSpan ParkTimeout = TimeSpan.FromSeconds(60);

    public override async ValueTask ParkAsync(CancellationToken cancellationToken = default)
    {
        // OnStep :hP# returns "1" = accepted, "0" = refused.
        var ack = await SendAndReceiveExactlyAsync(ParkCommand, 1, cancellationToken);
        if (ack is not "1")
        {
            throw new InvalidOperationException($"OnStep refused park request, response={ack ?? "<null>"}");
        }

        // The :hP# ack only means "park initiated" (the mount transitions to 'I' in :GU#).
        // Poll :GU# until the mount reports 'P' (parked) or 'F' (failed) or we time out.
        await WaitForParkStateAsync(parkedExpected: true, cancellationToken);
    }

    public override async ValueTask UnparkAsync(CancellationToken cancellationToken)
    {
        var ack = await SendAndReceiveExactlyAsync(UnparkCommand, 1, cancellationToken);
        if (ack is not "1")
        {
            throw new InvalidOperationException($"OnStep refused unpark request, response={ack ?? "<null>"}");
        }

        await WaitForParkStateAsync(parkedExpected: false, cancellationToken);
    }

    /// <summary>
    /// Sets the current position as the mount's park position via :hQ#.
    /// </summary>
    public override async ValueTask SetParkAsync(CancellationToken cancellationToken)
    {
        var ack = await SendAndReceiveExactlyAsync(SetParkCommand, 1, cancellationToken);
        if (ack is not "1")
        {
            throw new InvalidOperationException($"OnStep refused set-park request, response={ack ?? "<null>"}");
        }
    }

    /// <summary>
    /// Polls :GU# until the parked flag matches <paramref name="parkedExpected"/>.
    /// Throws on park-failed ('F') or timeout. Cooperates with FakeTimeProvider via
    /// <see cref="ITimeProvider.SleepAsync"/>.
    /// </summary>
    private async ValueTask WaitForParkStateAsync(bool parkedExpected, CancellationToken cancellationToken)
    {
        var startTicks = TimeProvider.GetTimestamp();
        var timeoutTicks = startTicks + (long)(ParkTimeout.TotalSeconds * TimeProvider.TimestampFrequency);

        while (true)
        {
            var status = await GetStatusAsync(cancellationToken);

            if (status.IsParkFailed)
            {
                throw new InvalidOperationException("OnStep reported park failure (':GU#' contained 'F')");
            }

            if (status.IsParked == parkedExpected)
            {
                Logger.LogInformation("OnStep park transition complete: parked={Parked}", parkedExpected);
                return;
            }

            if (TimeProvider.GetTimestamp() > timeoutTicks)
            {
                throw new TimeoutException(
                    $"OnStep did not reach parked={parkedExpected} within {ParkTimeout.TotalSeconds:F0}s. Last status='{status.RawFlags}'");
            }

            await TimeProvider.SleepAsync(ParkPollInterval, cancellationToken);
        }
    }

    private static readonly ReadOnlyMemory<byte> HomeCommand = "hC"u8.ToArray();

    /// <summary>The longest a return home is waited for, the ASCOM OnStep driver's (<c>FindHome</c>, 3.18).</summary>
    private static readonly TimeSpan HomeTimeout = TimeSpan.FromSeconds(240);

    public override bool CanFindHome => true;

    /// <summary>
    /// <c>:hC#</c>, which answers nothing, then <c>:GU#</c> until it carries <c>H</c> (at home), as the ASCOM OnStep
    /// driver does it. A park failure (<c>F</c>) or the timeout ends it with an error.
    /// </summary>
    public override async ValueTask FindHomeAsync(CancellationToken cancellationToken)
    {
        if (await AtParkAsync(cancellationToken))
        {
            throw new InvalidOperationException($"{_device.DisplayName} is parked; unpark it before sending it home");
        }

        await SendWithoutResponseAsync(HomeCommand, cancellationToken);

        var timeoutTicks = TimeProvider.GetTimestamp() + (long)(HomeTimeout.TotalSeconds * TimeProvider.TimestampFrequency);
        while (true)
        {
            await TimeProvider.SleepAsync(ParkPollInterval, cancellationToken);
            var status = await GetStatusAsync(cancellationToken);
            if (status.IsAtHome)
            {
                Logger.LogInformation("{Device} is at home", _device.DisplayName);
                return;
            }
            if (status.IsParkFailed)
            {
                throw new InvalidOperationException($"{_device.DisplayName} reported a failure going home (':GU#' {status.RawFlags})");
            }
            if (TimeProvider.GetTimestamp() > timeoutTicks)
            {
                throw new TimeoutException($"{_device.DisplayName} did not reach home within {HomeTimeout.TotalSeconds:F0} s; last status '{status.RawFlags}'");
            }
        }
    }

    private static readonly ReadOnlyMemory<byte> GX44Command = "GX44"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> GX45Command = "GX45"u8.ToArray();

    /// <summary>
    /// Reads raw encoder counts for the given mechanical axis via OnStep's
    /// <c>:GX44#</c> (axis 1 / RA / Az) and <c>:GX45#</c> (axis 2 / Dec / Alt).
    /// Mirrors the Skywatcher <c>:j1\r</c> / <c>:j2\r</c> behaviour so the neural
    /// guider's PE-phase feature engineering works identically across mount types.
    /// Returns <c>null</c> for the tertiary axis (not present on OnStep).
    /// </summary>
    public async ValueTask<long?> GetAxisPositionAsync(TelescopeAxis axis, CancellationToken cancellationToken)
    {
        var command = axis switch
        {
            TelescopeAxis.Primary => GX44Command,
            TelescopeAxis.Seconary => GX45Command,
            _ => default(ReadOnlyMemory<byte>)
        };

        if (command.IsEmpty)
        {
            return null;
        }

        var response = await SendAndReceiveAsync(command, cancellationToken);
        if (response is null)
        {
            return null;
        }

        return long.TryParse(response, NumberStyles.Integer, CultureInfo.InvariantCulture, out var counts) ? counts : null;
    }

    private static readonly ReadOnlyMemory<byte> GUCommand = "GU"u8.ToArray();
    /// <summary>
    /// Reads the OnStep :GU# bundled status word. Cheap (one round trip) and the
    /// authoritative source for tracking / slewing / parking / pulse-guide state.
    /// </summary>
    private async ValueTask<OnStepStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var response = await SendAndReceiveAsync(GUCommand, cancellationToken)
            ?? throw new InvalidOperationException("OnStep :GU# returned no response");
        return new OnStepStatus(response);
    }

    /// <summary>
    /// Decoded view over the OnStep :GU# status word. The response is a free-form
    /// concatenated string of single-character flags; we use case-sensitive
    /// substring tests because OnStep deliberately uses upper/lower case to
    /// distinguish opposite states (e.g. 'P' parked vs 'p' not parked,
    /// 'N' no goto vs 'n' not tracking).
    /// </summary>
    private readonly struct OnStepStatus(string flags)
    {
        public string RawFlags { get; } = flags;

        // OnStep emits 'n' when not tracking; otherwise the tracking-rate symbol
        // ('(' lunar, 'O' solar, 'k' king, none for sidereal) appears.
        public bool IsTracking => !RawFlags.Contains('n', StringComparison.Ordinal);

        // 'N' = no goto in progress; absence means a slew is happening.
        public bool IsGotoInProgress => !RawFlags.Contains('N', StringComparison.Ordinal);

        public bool IsParked => RawFlags.Contains('P', StringComparison.Ordinal);
        public bool IsAtHome => RawFlags.Contains('H', StringComparison.Ordinal);
        public bool IsParking => RawFlags.Contains('I', StringComparison.Ordinal);
        public bool IsParkFailed => RawFlags.Contains('F', StringComparison.Ordinal);
    }
}
