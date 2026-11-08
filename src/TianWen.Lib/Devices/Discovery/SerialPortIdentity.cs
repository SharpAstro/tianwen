using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using SharpAstro.Serial;

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
    /// The key as a port-keyed device id ends in it, in place of the COM name, or null where the port has nothing better than
    /// its name:
    /// <list type="bullet">
    ///   <item><c>device</c> (#1090 step 2): the USB bridge's serial, <c>usb-0403-6001-A10MOSEFA</c> for
    ///   <c>usb:0403:6001:A10MOSEFA</c>, the characters a probe's <c>SafeName</c> also replaces (<c>_</c>, <c>/</c>, <c>:</c>)
    ///   made <c>-</c>. The same in any socket.</item>
    ///   <item><c>socket</c> (#2040): the USB socket, as <c>socket-</c> and eight hex digits of the location path's SHA-256 (a
    ///   Windows path is long and full of <c>#</c>, which a URI path cannot carry). The same while the device stays in that
    ///   socket, whatever COM number Windows hands it; the whole path stays in <see cref="DeviceQueryKey.PortKey"/>.</item>
    /// </list>
    /// Null for a key another listed port reports too (<see cref="SharedWithAnotherPort"/>): clone FT232 adapters ship one
    /// serial on every unit (<c>A50285BI</c>), which as an id would make two units one device in the hub's driver map, so those
    /// keep their COM ids, as before the key existed.
    /// </summary>
    public string? IdSuffix => SharedWithAnotherPort ? null : IdSuffixOf(KindName, Key);

    /// <summary>Whether a port-keyed device id ends in this port's key (<see cref="IdSuffix"/>) rather than its COM name.</summary>
    public bool NamesTheDeviceInItsId => IdSuffix is not null;

    /// <summary><see cref="IdSuffix"/> for a kind and key read back from a URI's <see cref="DeviceQueryKey.PortIdentity"/> and
    /// <see cref="DeviceQueryKey.PortKey"/>.</summary>
    public static string? IdSuffixOf(string? kindName, string? key) => (kindName, key) switch
    {
        ("device", { Length: > 0 } k) => k.Replace('_', '-').Replace('/', '-').Replace(':', '-'),
        ("socket", { Length: > 0 } k) => "socket-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(k)), 0, 4),
        _ => null,
    };

    /// <summary>
    /// The identity of <paramref name="info"/>, one of the ports <paramref name="listed"/> by one enumeration (the whole list,
    /// the ports a connected driver holds as well as the free ones: a held port still reports its key). Sharing is judged
    /// among THAT list only, so a unit unplugged since is no longer counted, and a unit that is the only one plugged in is
    /// keyed on its bridge's serial until a second one with the same serial appears.
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
