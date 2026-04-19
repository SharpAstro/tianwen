using System;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// One entry in the pinned-port set reported by <see cref="IPinnedSerialPortsProvider"/>.
/// Pairs a serial port (in the canonical <c>serial:…</c> form) with the full device URI
/// the active profile expects to find there, so discovery can run a targeted verification
/// probe before falling back to general probing.
/// </summary>
/// <param name="Port">Serial port in the canonical enumerated form (e.g. <c>serial:COM5</c>).</param>
/// <param name="ExpectedUri">Full device URI from the active profile — scheme (DeviceType),
/// host (device source name), and path (deviceId) are all used to pick a verification probe
/// and confirm identity after the handshake.</param>
public sealed record PinnedSerialPort(string Port, Uri ExpectedUri);
