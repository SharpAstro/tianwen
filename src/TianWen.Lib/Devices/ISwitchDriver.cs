using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// A device of numbered channels, each an output to switch, a level to set or a reading to take: a power box's 12 V ports,
/// USB ports and dew heaters, and the supply and weather it measures. Shaped after ASCOM's ISwitchV2 (index, name, range,
/// step, writable), so an ASCOM or Alpaca switch maps onto it channel for channel; <see cref="SwitchChannel.Kind"/> is what
/// ISwitchV2 leaves a client to guess from the range.
/// </summary>
/// <remarks>
/// <para><b>Switching an output is actuation, and an output can carry what a run is using</b> (a camera's 12 V, the USB port
/// its data runs through), so a caller asks <c>DeviceOwnershipGate</c> before <see cref="SetValueAsync"/>, as for every other
/// actuation; the driver does not, and does not know what is plugged in where.</para>
/// <para>A driver with no channels yet (the ASCOM and Alpaca switches, until they are mapped) lists none, and every index is
/// then out of range.</para>
/// </remarks>
public interface ISwitchDriver : IDeviceDriver
{
    /// <summary>The channels, in index order; fixed once connected.</summary>
    IReadOnlyList<SwitchChannel> Channels => [];

    /// <summary>
    /// The channel's value: 1 or 0 for an output, the level for a <see cref="SwitchChannelKind.Level"/>, the reading in
    /// <see cref="SwitchChannel.Unit"/> for a <see cref="SwitchChannelKind.Reading"/>, NaN for a reading whose sensor is
    /// absent.
    /// </summary>
    ValueTask<double> GetValueAsync(int channel, CancellationToken cancellationToken = default)
        => throw new ArgumentOutOfRangeException(nameof(channel), channel, $"{Name} has no switch channels");

    /// <summary>Sets a writable channel, returning once the device reports the new value.</summary>
    ValueTask SetValueAsync(int channel, double value, CancellationToken cancellationToken = default)
        => throw new ArgumentOutOfRangeException(nameof(channel), channel, $"{Name} has no switch channels");
}

/// <summary>What a switch channel is.</summary>
public enum SwitchChannelKind
{
    /// <summary>An output that is on (1) or off (0): a 12 V port, a USB port.</summary>
    Output,

    /// <summary>An output set to a level between <see cref="SwitchChannel.Min"/> and <see cref="SwitchChannel.Max"/>: a dew heater's power.</summary>
    Level,

    /// <summary>A measurement, never written: a voltage, a current, a temperature.</summary>
    Reading,
}

/// <summary>One channel of an <see cref="ISwitchDriver"/>.</summary>
/// <param name="Index">Its index, the argument of <see cref="ISwitchDriver.GetValueAsync"/> and <see cref="ISwitchDriver.SetValueAsync"/>.</param>
/// <param name="Name">The device's name for it (<c>DC2</c>, <c>USB A</c>).</param>
/// <param name="Description">What it is, in a sentence.</param>
/// <param name="Kind">Output, level or reading.</param>
/// <param name="Min">The lowest value.</param>
/// <param name="Max">The highest value.</param>
/// <param name="Step">The smallest change the device resolves.</param>
/// <param name="Unit">The value's unit (<c>V</c>, <c>%</c>), empty for an on/off output.</param>
/// <param name="CanWrite">Whether <see cref="ISwitchDriver.SetValueAsync"/> may be called: false for a reading and for an output the device keeps on.</param>
public readonly record struct SwitchChannel(
    int Index,
    string Name,
    string Description,
    SwitchChannelKind Kind,
    double Min,
    double Max,
    double Step,
    string Unit,
    bool CanWrite);
