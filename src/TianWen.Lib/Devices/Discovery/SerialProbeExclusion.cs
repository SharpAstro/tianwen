using SharpAstro.Serial;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// Which ports a discovery does not probe at all, because what is at their far end can never be an instrument: Windows' own
/// incoming Bluetooth serial port (no remote device), and a paired Bluetooth device whose class says it is a headset, a phone,
/// a computer, a keyboard or mouse, a wearable, a toy or a health monitor. A headset's serial channel takes every write and
/// answers none, so it looked like a mount at the wrong baud: every probe, then the retry at twice the budget, about 31 s of
/// every discovery on a machine with a pair of headphones paired (2026-09-30, "S42", major class Audio/Video).
/// </summary>
/// <remarks>
/// A miscellaneous or uncategorised device is probed: a serial module (an HC-05 on a mount) reports one of those. A port a
/// profile has pinned is verified whatever it is (<see cref="SerialProbeService"/>'s first stage), so no assigned device is
/// ever skipped by this.
/// </remarks>
internal static class SerialProbeExclusion
{
    /// <summary>Why <paramref name="port"/> is not probed, or null when it is.</summary>
    public static string? ReasonNotToProbe(SerialPortInfo port)
    {
        return port.Bluetooth switch
        {
            null => null,
            { IsIncoming: true } => "Windows' incoming Bluetooth serial port: no device is at its far end to answer",
            { MajorClass: { } major } device when NeverAnInstrument(major) =>
                $"a Bluetooth {Describe(major)} ({device.Name ?? device.AddressText}), which is never an instrument",
            _ => null,
        };
    }

    private static bool NeverAnInstrument(BluetoothMajorClass major)
    {
        return major is BluetoothMajorClass.AudioVideo or BluetoothMajorClass.Phone or BluetoothMajorClass.Computer
            or BluetoothMajorClass.Peripheral or BluetoothMajorClass.Wearable or BluetoothMajorClass.Toy or BluetoothMajorClass.Health;
    }

    private static string Describe(BluetoothMajorClass major)
    {
        return major switch
        {
            BluetoothMajorClass.AudioVideo => "audio device",
            BluetoothMajorClass.Peripheral => "keyboard, mouse or pen",
            BluetoothMajorClass.Health => "health monitor",
            _ => major.ToString().ToLowerInvariant(),
        };
    }
}
