using DIR.Lib;
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
