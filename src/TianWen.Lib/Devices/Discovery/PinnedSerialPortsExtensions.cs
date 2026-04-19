using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// Shared helper for filtering enumerated serial ports against the currently
/// pinned set. Every discovery caller (probe service + per-source loops) calls
/// this so the filter logic lives in exactly one place.
/// </summary>
public static class PinnedSerialPortsExtensions
{
    extension(IPinnedSerialPortsProvider provider)
    {
        /// <summary>
        /// Returns a filtered copy of <paramref name="ports"/> with any pinned
        /// port removed. Allocates only when at least one port is actually
        /// filtered out. Logs a Debug line per skipped port (with port name in
        /// the scope / payload) so the log trail shows why an expected probe
        /// didn't run.
        /// </summary>
        public IReadOnlyList<string> FilterUnpinned(IReadOnlyList<string> ports, ILogger? logger = null)
        {
            var pinned = provider.GetPinnedPorts();
            if (pinned.Count == 0 || ports.Count == 0) return ports;

            var anyFiltered = false;
            for (var i = 0; i < ports.Count; i++)
            {
                if (pinned.Contains(ports[i])) { anyFiltered = true; break; }
            }
            if (!anyFiltered) return ports;

            var result = new List<string>(ports.Count);
            foreach (var port in ports)
            {
                if (pinned.Contains(port))
                {
                    logger?.LogDebug("Skipping pinned port {Port} — already referenced by active profile.", port);
                }
                else
                {
                    result.Add(port);
                }
            }
            return result;
        }
    }
}
