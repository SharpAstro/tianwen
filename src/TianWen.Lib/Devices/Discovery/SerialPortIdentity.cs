using SharpAstro.Serial;
using System;
using System.Collections.Generic;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// A serial port's own identity as the OS reported it when ports were last enumerated (#1090): how stable it is
/// (<paramref name="Kind"/>) and the key that names it (<paramref name="Key"/>, Serial.Lib's <c>SerialPortInfo.IdentityKey</c>).
/// Discovery writes both onto every serial device's URI (<see cref="DeviceQueryKey.PortIdentity"/>,
/// <see cref="DeviceQueryKey.PortKey"/>), so a client can tell a device that survives a re-plug in another socket from one
/// that does not.
/// </summary>
/// <param name="Kind">How stable the port is.</param>
/// <param name="Key">The key that names it.</param>
/// <param name="SharedWithAnotherPort">
/// Another port listed in the SAME enumeration reports the same <paramref name="Key"/> (see <see cref="Of"/>). A key that
/// names two ports names neither, so it is never put into a device id (<see cref="NamesTheDeviceInItsId"/>).
/// </param>
/// <remarks>
/// Measured on the bench (2026-11-05): an FTDI FT232R HBX cable is <c>device</c> (<c>usb:0403:6001:A10MOSEFA</c>), the same in any
/// socket; the SkyGuider Pro's own Prolific PL2303 and the Gemini hub's CH340 report no serial number and are <c>socket</c>.
/// </remarks>
public readonly record struct SerialPortIdentity(SerialIdentityKind Kind, string Key, bool SharedWithAnotherPort = false)
{
    /// <summary>The value written to <see cref="DeviceQueryKey.PortIdentity"/>: <c>device</c>, <c>socket</c> or <c>name</c>.</summary>
    public string KindName => Kind switch
    {
        SerialIdentityKind.Device => "device",
        SerialIdentityKind.Socket => "socket",
        _ => "name",
    };

    /// <summary>
    /// Whether a device id that ends in its port ends in this port's key instead (#1090 step 2): the port reports a USB serial
    /// number (<see cref="SerialIdentityKind.Device"/>) and no other listed port reports the same one. A clone adapter that
    /// ships one serial on every unit (<c>A50285BI</c> on cheap FT232 copies) gives two units one key, which as an id would be
    /// one device; those keep their port ids, so two such units stay two devices, as they were before the key existed.
    /// </summary>
    public bool NamesTheDeviceInItsId => Kind is SerialIdentityKind.Device && !SharedWithAnotherPort;

    /// <summary>
    /// The key as a device id ends in it (#1090 step 2): <c>usb-0403-6001-A10MOSEFA</c> for <c>usb:0403:6001:A10MOSEFA</c>, the
    /// characters a probe's <c>SafeName</c> also replaces (<c>_</c>, <c>/</c>, <c>:</c>) made <c>-</c>. Only a port that
    /// <see cref="NamesTheDeviceInItsId"/> is ever put into an id, since only that one survives a move to another socket.
    /// </summary>
    public string IdSuffix => IdSuffixOf(Key);

    /// <summary><see cref="IdSuffix"/> for a key read back from a URI's <see cref="DeviceQueryKey.PortKey"/>.</summary>
    public static string IdSuffixOf(string key) => key.Replace('_', '-').Replace('/', '-').Replace(':', '-');

    /// <summary>
    /// The identity of <paramref name="info"/>, one of the ports <paramref name="listed"/> by one enumeration (the whole list,
    /// the ports a connected driver holds as well as the free ones: a held port still reports its key). Sharing is judged
    /// among THAT list only, so a unit unplugged since is no longer counted, and a unit that is the only one plugged in is
    /// keyed on its cable until a second one with the same serial appears.
    /// </summary>
    public static SerialPortIdentity Of(SerialPortInfo info, IReadOnlyList<SerialPortInfo> listed)
    {
        var key = info.IdentityKey;
        foreach (var other in listed)
        {
            if (!string.Equals(other.PortName, info.PortName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.IdentityKey, key, StringComparison.Ordinal))
            {
                return new SerialPortIdentity(info.Identity, key, SharedWithAnotherPort: true);
            }
        }

        return new SerialPortIdentity(info.Identity, key);
    }
}
