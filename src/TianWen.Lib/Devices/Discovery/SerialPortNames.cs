using System;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// Helpers for working with serial port names as they appear across different
/// surfaces: <see cref="System.IO.Ports.SerialPort.GetPortNames"/> returns plain
/// <c>COM5</c>; <see cref="IExternal.EnumerateAvailableSerialPorts"/> returns the
/// protocol-prefixed <c>serial:COM5</c>; profile URIs may store either form (or a
/// sentinel like <c>wifi</c> / <c>wpd</c> / <c>SkyWatcher</c> for non-OS ports).
/// This class normalises the various forms to the canonical enumerated form so
/// that filtering by equality (with ordinal-ignore-case) is reliable.
/// </summary>
internal static class SerialPortNames
{
    /// <summary>
    /// Returns true if <paramref name="raw"/> looks like an OS serial port name
    /// and sets <paramref name="normalized"/> to the canonical <c>serial:…</c>
    /// form. Returns false for sentinel values (<c>wifi</c>, <c>wpd</c>, fake
    /// names) that don't map to a real port.
    /// </summary>
    public static bool TryNormalize(string? raw, out string normalized)
    {
        if (raw is null || raw.Length == 0)
        {
            normalized = string.Empty;
            return false;
        }

        // Already prefixed with "serial:"
        if (raw.StartsWith(ISerialConnection.SerialProto, StringComparison.Ordinal))
        {
            normalized = raw;
            return true;
        }

        // Windows "COMn"
        if (raw.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && raw.Length > 3)
        {
            normalized = ISerialConnection.SerialProto + raw;
            return true;
        }

        // Unix "/dev/ttyUSB0" or bare "ttyUSB0"
        var tail = raw.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tail.Length > 0 && tail[^1].StartsWith("tty", StringComparison.Ordinal))
        {
            normalized = ISerialConnection.SerialProto + raw;
            return true;
        }

        // Sentinel / unknown → not an OS port.
        normalized = string.Empty;
        return false;
    }
}
