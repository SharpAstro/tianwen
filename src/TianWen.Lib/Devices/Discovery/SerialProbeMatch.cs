using System;
using System.Collections.Generic;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// Successful result from an <see cref="ISerialProbe"/>. Carries the discovered device
/// URI (ready to be consumed by the owning <see cref="IDeviceSource{TDevice}"/>) plus
/// optional probe-specific metadata (firmware version, site name, board revision, …)
/// that the source may want to stash or log.
/// </summary>
/// <param name="Port">Port that matched, with the <c>serial:</c> protocol prefix.</param>
/// <param name="DeviceUri">Device URI constructed by the probe.</param>
/// <param name="Metadata">Arbitrary key/value pairs captured during the handshake.</param>
public sealed record SerialProbeMatch(
    string Port,
    Uri DeviceUri,
    IReadOnlyDictionary<string, string>? Metadata = null);
