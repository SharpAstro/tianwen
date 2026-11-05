using SharpAstro.Serial;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// A serial port's own identity as the OS reported it when ports were last enumerated (#1090): how stable it is
/// (<paramref name="Kind"/>) and the key that names it (<paramref name="Key"/>, Serial.Lib's <c>SerialPortInfo.IdentityKey</c>).
/// Discovery writes both onto every serial device's URI (<see cref="DeviceQueryKey.PortIdentity"/>,
/// <see cref="DeviceQueryKey.PortKey"/>), so a client can tell a device that survives a re-plug in another socket from one
/// that does not.
/// </summary>
/// <remarks>
/// Measured on the bench (2026-11-05): an FTDI FT232R HBX cable is <c>device</c> (<c>usb:0403:6001:A10MOSEFA</c>), the same in any
/// socket; the SkyGuider Pro's own Prolific PL2303 and the Gemini hub's CH340 report no serial number and are <c>socket</c>.
/// </remarks>
public readonly record struct SerialPortIdentity(SerialIdentityKind Kind, string Key)
{
    /// <summary>The value written to <see cref="DeviceQueryKey.PortIdentity"/>: <c>device</c>, <c>socket</c> or <c>name</c>.</summary>
    public string KindName => Kind switch
    {
        SerialIdentityKind.Device => "device",
        SerialIdentityKind.Socket => "socket",
        _ => "name",
    };

    /// <summary>
    /// The key as a device id ends in it (#1090 step 2): <c>usb-0403-6001-A10MOSEFA</c> for <c>usb:0403:6001:A10MOSEFA</c>, the
    /// characters a probe's <c>SafeName</c> also replaces (<c>_</c>, <c>/</c>, <c>:</c>) made <c>-</c>. Only a <c>device</c> port's
    /// key is ever put into an id, since only that one survives a move to another socket.
    /// </summary>
    public string IdSuffix => IdSuffixOf(Key);

    /// <summary><see cref="IdSuffix"/> for a key read back from a URI's <see cref="DeviceQueryKey.PortKey"/>.</summary>
    public static string IdSuffixOf(string key) => key.Replace('_', '-').Replace('/', '-').Replace(':', '-');

    /// <summary>The identity of a port Serial.Lib described.</summary>
    public static SerialPortIdentity Of(SerialPortInfo info) => new(info.Identity, info.IdentityKey);
}
