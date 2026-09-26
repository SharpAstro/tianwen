using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Hosting;

/// <summary>
/// The node's pinned serial ports: those of its ACTIVE profile (P1 of docs/plans/hardware-in-the-server.md, #917,
/// "Pinned serial ports"). A discovery on the node then verifies a pinned port with the device the profile expects
/// there before any general probing, as the GUI's does, instead of probing every port blind. Only the GUI registered
/// a provider, from its own active profile, so every scan the node ran probed the pinned ports too.
/// </summary>
/// <remarks>
/// The profile is read from its file at each pass, since the GUI or the CLI may have edited it since; the active
/// profile's id comes from the node's settings, not from the hosted session, which the discovery this serves is
/// itself a dependency of.
/// </remarks>
internal sealed class NodePinnedSerialPorts(NodeSettingsStore settings, IExternal external) : IPinnedSerialPortsProvider
{
    public async ValueTask<IReadOnlyList<PinnedSerialPort>> GetPinnedPortsAsync(CancellationToken cancellationToken)
        => settings.Current.ActiveProfileId is { } profileId && await Profile.TryReadDataAsync(external, profileId, cancellationToken) is { } data
            ? PinnedSerialPort.In(data)
            : [];
}
