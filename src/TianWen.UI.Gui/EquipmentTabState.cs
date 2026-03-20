using System.Collections.Generic;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// State for the Equipment/Profile tab.
/// </summary>
public class EquipmentTabState
{
    // Profile creation
    public bool IsCreatingProfile { get; set; }
    public TextInputState ProfileNameInput { get; } = new() { Placeholder = "Enter profile name..." };

    // Site configuration
    public bool IsEditingSite { get; set; }
    public TextInputState LatitudeInput { get; } = new() { Placeholder = "Latitude (e.g. 48.2)" };
    public TextInputState LongitudeInput { get; } = new() { Placeholder = "Longitude (e.g. 16.3)" };
    public TextInputState ElevationInput { get; } = new() { Placeholder = "Elevation m (e.g. 200)" };
    // ActiveTextInput moved to GuiAppState — single source of truth across all tabs

    // Device discovery
    public IReadOnlyList<DeviceBase> DiscoveredDevices { get; set; } = [];
    public bool IsDiscovering { get; set; }

    // Assignment mode: when non-null, clicking a device in the list assigns it to this slot
    public AssignTarget? ActiveAssignment { get; set; }

    // Scrolling
    public int DeviceScrollOffset { get; set; }
    public int ProfileScrollOffset { get; set; }

    // Profile list (for multi-profile picker)
    public IReadOnlyList<Profile> AllProfiles { get; set; } = [];
}

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
