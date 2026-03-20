using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Identifies a slot in a profile that a device can be assigned to.
    /// </summary>
    public abstract record AssignTarget
    {
        /// <summary>A profile-level slot (Mount, Guider, GuiderCamera, GuiderFocuser).</summary>
        public sealed record ProfileLevel(string Field) : AssignTarget;

        /// <summary>A per-OTA slot (Camera, Focuser, FilterWheel, Cover).</summary>
        public sealed record OTALevel(int OtaIndex, string Field) : AssignTarget;

        /// <summary>Returns the DeviceType expected for this slot.</summary>
        public DeviceType ExpectedDeviceType => this switch
        {
            ProfileLevel { Field: "Mount" } => DeviceType.Mount,
            ProfileLevel { Field: "Guider" } => DeviceType.Guider,
            ProfileLevel { Field: "GuiderCamera" } => DeviceType.Camera,
            ProfileLevel { Field: "GuiderFocuser" } => DeviceType.Focuser,
            OTALevel { Field: "Camera" } => DeviceType.Camera,
            OTALevel { Field: "Focuser" } => DeviceType.Focuser,
            OTALevel { Field: "FilterWheel" } => DeviceType.FilterWheel,
            OTALevel { Field: "Cover" } => DeviceType.CoverCalibrator,
            _ => DeviceType.Unknown
        };
    }
}
