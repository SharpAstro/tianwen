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

        public static DeviceDto FromDevice(DeviceBase device, bool connected) => new()
        {
            Uri = device.DeviceUri.ToString(),
            DisplayName = device.DisplayName,
            DeviceId = device.DeviceId,
            DeviceType = device.DeviceType.ToString(),
            Connected = connected,
        };
    }
}
