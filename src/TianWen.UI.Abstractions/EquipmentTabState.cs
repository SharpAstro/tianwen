using DIR.Lib;
using System.Collections.Generic;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

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

    // Filter editing: which OTA's filter table is expanded (-1 = none)
    public int ExpandedFilterOtaIndex { get; set; } = -1;

    // Mutable filter list for in-memory editing (saved on explicit Save)
    public List<InstalledFilter>? EditingFilters { get; set; }
    public bool FiltersDirty { get; set; }

    /// <summary>
    /// Loads filters into the mutable editing list from the profile.
    /// </summary>
    public void BeginEditingFilters(IReadOnlyList<InstalledFilter> filters)
    {
        EditingFilters = new List<InstalledFilter>(filters);
        FiltersDirty = false;
    }

    /// <summary>
    /// Discards the mutable filter list.
    /// </summary>
    public void StopEditingFilters()
    {
        EditingFilters = null;
        FiltersDirty = false;
    }

    // Filter name dropdown
    public DropdownMenuState FilterNameDropdown { get; } = new();

    // Custom filter name input (shared across all slots, activated by "Custom..." entry)
    public int CustomFilterSlotIndex { get; set; } = -1;
    public TextInputState CustomFilterNameInput { get; } = new() { Placeholder = "Filter name..." };


    // OTA property editing: which OTA is being edited (-1 = none)
    public int EditingOtaIndex { get; set; } = -1;
    public TextInputState OtaNameInput { get; } = new() { Placeholder = "OTA name" };
    public TextInputState FocalLengthInput { get; } = new() { Placeholder = "Focal length (mm)" };
    public TextInputState ApertureInput { get; } = new() { Placeholder = "Aperture (mm)" };

    /// <summary>
    /// Initializes the OTA editing state from the given OTA data.
    /// </summary>
    public void BeginEditingOta(int otaIndex, OTAData ota)
    {
        EditingOtaIndex = otaIndex;
        OtaNameInput.Text = ota.Name;
        FocalLengthInput.Text = ota.FocalLength.ToString();
        ApertureInput.Text = ota.Aperture?.ToString() ?? "";
    }

    /// <summary>
    /// Stops OTA property editing.
    /// </summary>
    public void StopEditingOta()
    {
        EditingOtaIndex = -1;
    }
}
