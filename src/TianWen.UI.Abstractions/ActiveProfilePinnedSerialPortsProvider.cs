using System;
using System.Collections.Generic;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Reads pinned <c>(port, expected device URI)</c> pairs from the host's active profile
/// (<see cref="GuiAppState.ActiveProfile"/>). Every URI slot in <see cref="ProfileData"/> —
/// mount, guider, optional guider camera/focuser, weather, and every OTA's
/// camera/cover/focuser/filter wheel — is walked on each call; any <c>?port=…</c> query
/// value that normalises to a real OS port (see <see cref="SerialPortNames.TryNormalize"/>)
/// becomes a <see cref="PinnedSerialPort"/>. Sentinel values (<c>wifi</c>, <c>wpd</c>,
/// fake-mount placeholders) are silently ignored so they can't block discovery.
/// <para>
/// Called on the discovery code path (background), reads a single volatile property on
/// <see cref="GuiAppState"/>. No locking: <see cref="GuiAppState.ActiveProfile"/> is swapped
/// atomically on the UI thread; a stale read just means we consult the previous profile for
/// one more discovery pass, which is harmless.
/// </para>
/// </summary>
public sealed class ActiveProfilePinnedSerialPortsProvider(GuiAppState appState) : IPinnedSerialPortsProvider
{
    public IReadOnlyList<PinnedSerialPort> GetPinnedPorts()
    {
        var profile = appState.ActiveProfile;
        if (profile?.Data is not { } data)
        {
            return [];
        }

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
        if (uri is null) return;
        var raw = uri.QueryValue(DeviceQueryKey.Port);
        if (SerialPortNames.TryNormalize(raw, out var normalized))
        {
            list.Add(new PinnedSerialPort(normalized, uri));
        }
    }
}
