using System.Collections.Generic;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// Reports serial ports that the active configuration (typically an equipment profile)
/// already assigns to specific device URIs. Discovery uses this to run a targeted
/// verification probe on each pinned port before falling back to general probing —
/// see <see cref="ISerialProbeService"/> for the two-tier algorithm.
/// <para>
/// Hosts (GUI / TUI / Server) register their own implementation. The default
/// <see cref="NullPinnedSerialPortsProvider"/> returns an empty list — the safe
/// behaviour for hosts without a notion of "active profile".
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

/// <summary>Safe default for hosts without profile awareness.</summary>
internal sealed class NullPinnedSerialPortsProvider : IPinnedSerialPortsProvider
{
    public IReadOnlyList<PinnedSerialPort> GetPinnedPorts() => [];
}
