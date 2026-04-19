using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// Central serial-port discovery coordinator. Opens each port once per baud rate,
/// runs all registered <see cref="ISerialProbe"/>s that share that baud against the
/// shared open handle, and exposes matches to device sources via
/// <see cref="ResultsFor(string)"/>.
/// <para>
/// Call order is: <see cref="ProbeAllAsync"/> (owned by <see cref="IDeviceDiscovery"/>),
/// then per-source <c>DiscoverAsync</c> reads results. Every log line emitted during
/// probing carries <c>port</c>, <c>baud</c>, and <c>probe</c> in logger scope so
/// timeouts can be attributed to the probe that owns them.
/// </para>
/// </summary>
public interface ISerialProbeService
{
    /// <summary>
    /// Enumerate serial ports (excluding any pinned-in-profile ports, once Phase 3 lands)
    /// and run all registered probes. Safe to call multiple times — each call clears
    /// prior results. Non-throwing: per-port and per-probe failures are logged and
    /// absorbed; a probe that throws is treated as a no-match on that port.
    /// </summary>
    ValueTask ProbeAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Matches published by the probe whose <see cref="ISerialProbe.Name"/> equals
    /// <paramref name="probeName"/>. Empty if no probe is registered under that name
    /// or if <see cref="ProbeAllAsync"/> has not been called.
    /// </summary>
    IReadOnlyList<SerialProbeMatch> ResultsFor(string probeName);
}
