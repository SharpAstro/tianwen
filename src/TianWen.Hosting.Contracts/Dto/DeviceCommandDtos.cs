using TianWen.Lib.Astrometry.Catalogs;
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

/// <summary>Cools a camera to a setpoint through the session's own ramp: <c>POST /api/v1/devices/camera/cool</c>.</summary>
public sealed class CoolRequestDto
{
    public required string DeviceUri { get; init; }

    /// <summary>The target, in °C; rounded to a whole degree, as a session's is.</summary>
    public required double SetpointC { get; init; }

    /// <summary>How long the ramp may take, in minutes; null for a session's default.</summary>
    public double? RampMinutes { get; init; }
}

/// <summary>A readout frame in binned sensor pixels: top-left, then size.</summary>
public sealed class FrameDto
{
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>
/// The settings to change on a camera: <c>POST /api/v1/devices/camera/settings</c>. Each is left as it is when null. Gain
/// and offset are values for a camera that takes one, and an index into its named list for one that takes a mode.
/// </summary>
public sealed class CameraSettingsRequestDto
{
    public required string DeviceUri { get; init; }
    public short? Gain { get; init; }
    public int? Offset { get; init; }

    /// <summary>Both axes. Setting it without a frame reads out the whole binned sensor.</summary>
    public int? Bin { get; init; }

    /// <summary>In binned pixels, snapped to what the camera can read out and kept on its sensor.</summary>
    public FrameDto? Frame { get; init; }
}

/// <summary>
/// Moves a focuser: <c>POST /api/v1/devices/focuser/move</c>, to <see cref="Position"/> or by <see cref="Steps"/> from where
/// it is; exactly one of the two.
/// </summary>
public sealed class FocuserMoveRequestDto
{
    public required string DeviceUri { get; init; }
    public int? Position { get; init; }
    public int? Steps { get; init; }
}

/// <summary>Turns a filter wheel to a position, counted from 0: <c>POST /api/v1/devices/filterwheel/change</c>.</summary>
public sealed class FilterChangeRequestDto
{
    public required string DeviceUri { get; init; }
    public required int Position { get; init; }
}

/// <summary>Slews a mount to a J2000 position: <c>POST /api/v1/devices/mount/goto</c>.</summary>
public sealed class MountGotoRequestDto
{
    public required string DeviceUri { get; init; }

    /// <summary>J2000 right ascension, in hours.</summary>
    public required double RaJ2000 { get; init; }

    /// <summary>J2000 declination, in degrees.</summary>
    public required double DecJ2000 { get; init; }

    /// <summary>What the target is called in the job's steps; its coordinates when null.</summary>
    public string? Name { get; init; }

    /// <summary>The catalogue object, when the target is one: the Sun and the Moon are tracked at their own rates.</summary>
    public CatalogIndex? Index { get; init; }

    /// <summary>The lowest altitude a goto may end at, in degrees; 10 when null.</summary>
    public int? MinAltitudeDegrees { get; init; }
}

/// <summary>
/// Moves one of a mount's axes at a rate, or stops it at 0: <c>POST /api/v1/devices/mount/move-axis</c>. The motion is
/// LEASED: it stops by itself <see cref="Api.NodeWire.MoveAxisLease"/> after the last request that asked for it, so a
/// client holding a move repeats this request while it holds.
/// </summary>
public sealed class MoveAxisRequestDto
{
    public required string DeviceUri { get; init; }
    public required TelescopeAxis Axis { get; init; }

    /// <summary>Degrees a second, signed for the direction, inside one of the axis's rate ranges; 0 stops the axis.</summary>
    public required double Rate { get; init; }
}

/// <summary>Switches a mount's tracking: <c>POST /api/v1/devices/mount/tracking</c>.</summary>
public sealed class MountTrackingRequestDto
{
    public required string DeviceUri { get; init; }
    public required bool On { get; init; }
}

/// <summary>A camera's settings as it reads them back after a change, which a camera may have snapped.</summary>
public sealed class CameraSettingsDto
{
    /// <summary>Null for a camera that has no gain to set.</summary>
    public short? Gain { get; init; }

    /// <summary>Null for a camera that has no offset to set.</summary>
    public int? Offset { get; init; }

    public required int BinX { get; init; }
    public required int BinY { get; init; }
    public required FrameDto Frame { get; init; }
}
