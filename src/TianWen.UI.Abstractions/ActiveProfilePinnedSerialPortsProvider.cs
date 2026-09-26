using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Reads pinned <c>(port, expected device URI)</c> pairs from the host's active profile
/// (<see cref="GuiAppState.ActiveProfile"/>), through the one walk every provider shares
/// (<see cref="PinnedSerialPort.In"/>).
/// <para>
/// Called on the discovery code path (background), reads a single volatile property on
/// <see cref="GuiAppState"/>. No locking: <see cref="GuiAppState.ActiveProfile"/> is swapped
/// atomically on the UI thread; a stale read just means we consult the previous profile for
/// one more discovery pass, which is harmless.
/// </para>
/// </summary>
public sealed class ActiveProfilePinnedSerialPortsProvider(GuiAppState appState) : IPinnedSerialPortsProvider
{
    public ValueTask<IReadOnlyList<PinnedSerialPort>> GetPinnedPortsAsync(CancellationToken cancellationToken)
        => new ValueTask<IReadOnlyList<PinnedSerialPort>>(appState.ActiveProfile?.Data is { } data ? PinnedSerialPort.In(data) : []);
}
