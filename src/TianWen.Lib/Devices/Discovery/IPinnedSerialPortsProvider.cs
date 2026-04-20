using System.Collections.Generic;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// Reports serial ports that the active configuration (typically an equipment profile)
/// already assigns to specific device URIs. Discovery uses this to run a targeted
/// verification probe on each pinned port before falling back to general probing —
/// see <see cref="ISerialProbeService"/> for the two-tier algorithm.
/// <para>
/// Optional: hosts with a notion of "active profile" (GUI / TUI / Server) register
/// their own implementation. If no provider is registered, <see cref="SerialProbeService"/>
/// treats the pinned list as empty and goes straight to general probing — the safe
/// behaviour for hosts without profile awareness.
/// </para>
/// </summary>
public interface IPinnedSerialPortsProvider
{
    /// <summary>
    /// Returns the list of pinned <c>(port, expected device URI)</c> pairs. Ports are
    /// in the canonical <c>serial:…</c> form; URIs that reference a transport sentinel
    /// (<c>wifi</c>, <c>wpd</c>, fake-mount names) are excluded so they don't clutter
    /// the verification pass.
    /// </summary>
    IReadOnlyList<PinnedSerialPort> GetPinnedPorts();
}
