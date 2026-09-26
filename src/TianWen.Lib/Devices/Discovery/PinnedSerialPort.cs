using System;
using System.Collections.Generic;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// One entry in the pinned-port set reported by <see cref="IPinnedSerialPortsProvider"/>.
/// Pairs a serial port (in the canonical <c>serial:…</c> form) with the full device URI
/// the active profile expects to find there, so discovery can run a targeted verification
/// probe before falling back to general probing.
/// </summary>
/// <param name="Port">Serial port in the canonical enumerated form (e.g. <c>serial:COM5</c>).</param>
/// <param name="ExpectedUri">Full device URI from the active profile; scheme (DeviceType),
/// host (device source name), and path (deviceId) are all used to pick a verification probe
/// and confirm identity after the handshake.</param>
public sealed record PinnedSerialPort(string Port, Uri ExpectedUri)
{
    /// <summary>
    /// Every pinned port in <paramref name="data"/>: every URI slot (mount, guider, optional guider camera and focuser,
    /// weather, and every OTA's camera, cover, focuser and filter wheel) whose <c>?port=</c> normalises to a real OS
    /// port (<see cref="SerialPortNames.TryNormalize"/>). Sentinel values (<c>wifi</c>, <c>wpd</c>, fake-mount names)
    /// are ignored, so they cannot block discovery. The one walk, for the GUI's provider and the node's.
    /// </summary>
    public static IReadOnlyList<PinnedSerialPort> In(ProfileData data)
    {
        var list = new List<PinnedSerialPort>();
        AddIfPort(list, data.Mount);
        AddIfPort(list, data.Guider);
        AddIfPort(list, data.GuiderCamera);
        AddIfPort(list, data.GuiderFocuser);
        AddIfPort(list, data.Weather);

        foreach (var ota in data.OTAs)
        {
            AddIfPort(list, ota.Camera);
            AddIfPort(list, ota.Cover);
            AddIfPort(list, ota.Focuser);
            AddIfPort(list, ota.FilterWheel);
        }

        return list;
    }

    private static void AddIfPort(List<PinnedSerialPort> list, Uri? uri)
    {
        if (uri is null)
        {
            return;
        }
        var raw = uri.QueryValue(DeviceQueryKey.Port);
        if (SerialPortNames.TryNormalize(raw, out var normalized))
        {
            list.Add(new PinnedSerialPort(normalized, uri));
        }
    }
}
