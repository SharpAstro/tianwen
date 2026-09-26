using TianWen.Lib.Devices;

namespace TianWen.Hosting.Dto;

/// <summary>
/// A request naming one device (the device plane, P2 of docs/plans/hardware-in-the-server.md, #929). It carries the
/// WHOLE URI, query and all, because a device's settings (a serial port, a baud rate, a fake's options) ride on it and
/// the node builds the device from it. A URI's left part cannot be a path segment, which is why it is a body.
/// </summary>
public sealed class DeviceRequestDto
{
    public required string DeviceUri { get; init; }
}

/// <summary>A disconnect of one device: <c>POST /api/v1/devices/disconnect</c>.</summary>
public sealed class DisconnectRequestDto
{
    public required string DeviceUri { get; init; }

    /// <summary>
    /// Disconnect a camera whose cooler is on, or which is busy, as it is, without the warm-up: what the Equipment tab's
    /// Force Off confirms. It skips the HARDWARE check only. A device a run holds is refused whatever this says, since
    /// consenting to a cold disconnect is not consenting to end the night; stopping the run is the way past that.
    /// </summary>
    public bool SkipWarmUp { get; init; }
}

/// <summary>
/// Whether a connected device can be disconnected now, and why not: <c>GET /api/v1/devices/disconnect-safety</c>. The
/// read a client makes BEFORE it offers a disconnect, as the Equipment tab does today, to choose between disconnecting,
/// offering a warm-up or a cold disconnect, and naming the run that holds the device.
/// </summary>
public sealed class DisconnectCheckDto
{
    /// <summary>The hardware answer: a camera's cooler and whether it is at work; <see cref="DisconnectSafety.Safe"/> for anything else.</summary>
    public required DisconnectSafety Safety { get; init; }

    /// <summary>The run holding the device, which refuses every disconnect until it ends; null when nothing does.</summary>
    public string? LeaseOwner { get; init; }
}
