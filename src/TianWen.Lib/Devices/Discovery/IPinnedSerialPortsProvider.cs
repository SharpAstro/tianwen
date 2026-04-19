using System.Collections.Generic;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// Reports serial ports that are currently "pinned" by an active configuration
/// (typically an equipment profile that references <c>?port=COM5</c>). Discovery
/// skips probing these ports so it doesn't fight a driver that may be connected or
/// about to connect. The provider is consulted once per discovery pass; the
/// returned set is a snapshot.
/// <para>
/// Hosts (GUI / TUI / Server) register their own implementation. The default
/// <see cref="NullPinnedSerialPortsProvider"/> returns an empty set, which is the
/// safe behaviour for hosts without a notion of an "active profile."
/// </para>
/// </summary>
public interface IPinnedSerialPortsProvider
{
    /// <summary>
    /// Returns the set of pinned ports in the canonical <c>serial:COM5</c> form,
    /// using <see cref="System.StringComparer.OrdinalIgnoreCase"/>. Profile URIs
    /// that reference a transport sentinel (<c>wifi</c>, <c>wpd</c>, fake-mount
    /// names) are excluded so they don't pollute the filter.
    /// </summary>
    IReadOnlySet<string> GetPinnedPorts();
}

/// <summary>Safe default for hosts without profile awareness.</summary>
internal sealed class NullPinnedSerialPortsProvider : IPinnedSerialPortsProvider
{
    public IReadOnlySet<string> GetPinnedPorts() => System.Collections.Frozen.FrozenSet<string>.Empty;
}
