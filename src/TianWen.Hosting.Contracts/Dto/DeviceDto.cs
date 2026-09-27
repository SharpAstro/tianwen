using System;
using TianWen.Lib.Devices;

namespace TianWen.Hosting.Dto
{
    /// <summary>
    /// A discovered device in machine-readable form.
    /// <para>
    /// The pre-existing <c>GET /devices</c> returns pre-formatted display strings
    /// (<c>"Camera: ZWO ASI533MC Pro (asi533)"</c>), which a human can read and a client cannot use: no
    /// URI to assign, no connection state, and the only way back to structure is to re-parse the label.
    /// Since a URI is what every assignment and driver lookup is keyed on, a remote equipment panel
    /// needs this shape. The string endpoint stays as-is for existing callers.
    /// </para>
    /// </summary>
    public sealed class DeviceDto
    {
        /// <summary>The device URI -- the identity everything else is keyed on.</summary>
        public required string Uri { get; init; }

        public required string DisplayName { get; init; }

        public required string DeviceId { get; init; }

        /// <summary>Camera / Telescope / Focuser / FilterWheel / CoverCalibrator / Guider / Weather / ...</summary>
        public required string DeviceType { get; init; }

        /// <summary>Whether the node currently holds a connected driver for this URI.</summary>
        public required bool Connected { get; init; }

        /// <summary>
        /// The vendor or transport moniker the Equipment tab's device list shows ("ZWO", "ASCOM", "Open-Meteo"): what the
        /// device's own type says (<see cref="DeviceBase.Source"/>), which a client of the node cannot work out from a URI.
        /// </summary>
        public string? Source { get; init; }

        /// <summary>
        /// A camera's named gains (<see cref="IDeviceWithGainModes"/>), which a session offers in place of a gain value; null
        /// for a camera that takes a value and for any other device. What the device IS, so known with nothing connected
        /// (P6 part 1 of docs/plans/hardware-in-the-server.md).
        /// </summary>
        public string[]? GainModes { get; init; }

        /// <summary>
        /// Whether a camera has a cooler to set (false for an <see cref="IUncooledCamera"/>, a DSLR); null for any other
        /// device. What a session's camera settings offer a setpoint by, known with nothing connected.
        /// </summary>
        public bool? CanCool { get; init; }

        public static DeviceDto FromDevice(DeviceBase device, bool connected) => new()
        {
            Uri = device.DeviceUri.ToString(),
            DisplayName = device.DisplayName,
            DeviceId = device.DeviceId,
            DeviceType = device.DeviceType.ToString(),
            Connected = connected,
            Source = device.Source,
            GainModes = device is IDeviceWithGainModes { GainModes.Count: > 0 } withGainModes ? [.. withGainModes.GainModes] : null,
            CanCool = device.DeviceType is TianWen.Lib.Devices.DeviceType.Camera ? device is not IUncooledCamera : null,
        };
    }
}
