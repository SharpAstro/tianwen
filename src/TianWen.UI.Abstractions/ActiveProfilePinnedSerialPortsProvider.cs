using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Reads pinned serial ports from the host's active profile (<see cref="GuiAppState.ActiveProfile"/>).
/// Every URI slot in <see cref="ProfileData"/> — mount, guider, optional guider camera/focuser,
/// weather, and every OTA's camera/cover/focuser/filter wheel — is walked each call; any
/// <c>?port=…</c> query value that normalises to a real OS port (see
/// <see cref="SerialPortNames.TryNormalize"/>) is included. Sentinel values (<c>wifi</c>,
/// <c>wpd</c>, fake-mount placeholders) are silently ignored so they can't block discovery.
/// <para>
/// Called on the discovery code path (background), reads a single volatile property on
/// <see cref="GuiAppState"/>. No locking: <see cref="GuiAppState.ActiveProfile"/> is swapped
/// atomically on the UI thread; a stale read just means we consult the previous profile for
/// one more discovery pass, which is harmless.
/// </para>
/// </summary>
public sealed class ActiveProfilePinnedSerialPortsProvider(GuiAppState appState) : IPinnedSerialPortsProvider
{
    public IReadOnlySet<string> GetPinnedPorts()
    {
        var profile = appState.ActiveProfile;
        if (profile?.Data is not { } data)
        {
            return ImmutableHashSet<string>.Empty;
        }

        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        AddIfPort(builder, data.Mount);
        AddIfPort(builder, data.Guider);
        AddIfPort(builder, data.GuiderCamera);
        AddIfPort(builder, data.GuiderFocuser);
        AddIfPort(builder, data.Weather);

        foreach (var ota in data.OTAs)
        {
            AddIfPort(builder, ota.Camera);
            AddIfPort(builder, ota.Cover);
            AddIfPort(builder, ota.Focuser);
            AddIfPort(builder, ota.FilterWheel);
        }

        return builder.ToImmutable();
    }

    private static void AddIfPort(ImmutableHashSet<string>.Builder builder, Uri? uri)
    {
        if (uri is null) return;
        var raw = uri.QueryValue(DeviceQueryKey.Port);
        if (SerialPortNames.TryNormalize(raw, out var normalized))
        {
            builder.Add(normalized);
        }
    }
}
