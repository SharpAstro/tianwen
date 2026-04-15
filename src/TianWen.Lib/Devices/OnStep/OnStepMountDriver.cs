using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.OnStep;

/// <summary>
/// OnStep / OnStepX mount driver. Extends the LX200 base with OnStep's richer
/// command set: <c>:GU#</c> bundled status, <c>:Gm#</c> hardware pier-side,
/// <c>:hR#</c> unpark, <c>:hQ#</c> set-park, <c>:Te#</c>/<c>:Td#</c> tracking
/// enable/disable with response, and the <c>:TK#</c>/<c>:TS#</c> King/Solar
/// tracking rates.
///
/// Generic over <typeparamref name="TDevice"/> to mirror the LX200 base class
/// pattern — lets <see cref="Fake.FakeOnStepMountDriver"/> subclass with
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
    /// Park polling cadence — picked to match the base IsSlewingAsync ~250 ms cadence
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
    public async ValueTask SetParkAsync(CancellationToken cancellationToken = default)
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
        public bool IsParking => RawFlags.Contains('I', StringComparison.Ordinal);
        public bool IsParkFailed => RawFlags.Contains('F', StringComparison.Ordinal);
    }
}
