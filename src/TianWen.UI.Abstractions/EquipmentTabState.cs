using System;
using DIR.Lib;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
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

    // Mount safety limits (docs/plans/mount-safety-limits.md, P1). The four numbers are text inputs saved
    // together like the site; the on/off switch and the two responses are buttons that save at once.
    public bool IsEditingMountLimits { get; set; }
    public TextInputState LimitMeridianWarnInput { get; } = new() { Placeholder = "min past meridian (e.g. 20)" };
    public TextInputState LimitMeridianExtraInput { get; } = new() { Placeholder = "further min (e.g. 20)" };
    public TextInputState LimitHorizonActionInput { get; } = new() { Placeholder = "altitude deg (e.g. 10)" };
    public TextInputState LimitHorizonExtraInput { get; } = new() { Placeholder = "deg above (e.g. 5)" };
    // The switch and the two responses are pending edit state like the four inputs above: applied on Save,
    // dropped on Cancel. They used to write the profile on every click, which Cancel could not undo.
    public bool LimitEnabledPending { get; set; }
    public MountLimitResponse LimitMeridianResponsePending { get; set; }
    public MountLimitResponse LimitHorizonResponsePending { get; set; }

    // Device discovery
    public IReadOnlyList<DeviceBase> DiscoveredDevices { get; set; } = [];
    public bool IsDiscovering { get; set; }

    /// <summary>
    /// Device URIs whose connect/disconnect transition is currently in flight.
    /// Both segments of the On|Off button are disabled while a URI is in this set
    /// to prevent rapid double-clicks issuing competing transitions.
    /// <para>
    /// <b>Thread safety:</b> mutated from async signal handlers whose continuations
    /// run on thread pool threads after <c>await</c>, and read from the render thread
    /// during hit testing. Must be a concurrent collection; a <c>HashSet</c> would
    /// crash with <c>IndexOutOfRangeException</c> on a bucket-array race.
    /// </para>
    /// </summary>
    public ConcurrentDictionary<Uri, byte> PendingTransitions { get; } = new();

    /// <summary>
    /// First-stage disconnect confirmation: when set, the row for this URI shows
    /// [Warm &amp; Disconnect] [Force Off] [Cancel] instead of the On|Off button.
    /// Cleared on Cancel, on Warm &amp; Disconnect dispatch, or when Force escalates.
    /// </summary>
    public Uri? PendingDisconnectConfirm { get; set; }

    /// <summary>Safety classification cached from the pre-check that put the URI into
    /// <see cref="PendingDisconnectConfirm"/>: drives the warning text.</summary>
    public EquipmentActions.DisconnectSafety PendingDisconnectSafety { get; set; }

    /// <summary>
    /// Second-stage force-disconnect confirmation: when set, the row shows
    /// [Cancel] ⚠ Really force? [REALLY FORCE] with the destructive button at the
    /// opposite side of the row to prevent muscle-memory double-click escalation.
    /// </summary>
    public Uri? PendingForceConfirm { get; set; }

    /// <summary>
    /// Camera telemetry sample buffers, keyed by URI path (host+path, no query/fragment).
    /// Populated by <c>AppSignalHandler.PollCameraTelemetry</c> on the equipment tab's
    /// per-frame tick while the camera is hub-connected. Drives the per-camera cooler
    /// graph + readout in the expanded camera control panel.
    /// <para>
    /// <b>Thread safety:</b> the dictionary is mutated by per-camera tracker continuations
    /// (one per URI) and read by the render thread, so must be a concurrent collection.
    /// The <see cref="CameraTelemetryBuffer"/> values are themselves internally locked.
    /// </para>
    /// </summary>
    public ConcurrentDictionary<string, CameraTelemetryBuffer> CameraTelemetry { get; } = new();

    /// <summary>Per-camera setpoint input shared across cameras (camera URI path → text input state).</summary>
    public Dictionary<string, TextInputState> CameraSetpointInputs { get; } = new();

    /// <summary>First-stage cooler-off confirmation per URI: triggers the inline strip
    /// [Warm up &amp; Off] [Force] [Cancel] under the cooler control panel.</summary>
    public Uri? PendingCoolerOffConfirm { get; set; }

    /// <summary>Second-stage force-cooler-off confirmation, position-swapped just like
    /// the disconnect force flow to defeat muscle-memory escalation.</summary>
    public Uri? PendingCoolerOffForceConfirm { get; set; }

    // Assignment mode: when non-null, clicking a device in the list assigns it to this slot
    public AssignTarget? ActiveAssignment { get; set; }

    // Scrolling (device-list scroll offset now lives in EquipmentTab's ListScrollController)
    public int ProfileScrollOffset { get; set; }

    // Profile list (for multi-profile picker)
    public IReadOnlyList<Profile> AllProfiles { get; set; } = [];

    /// <summary>
    /// Rigs bound locally (docs/plans/remote-profile.md). Listed in the profile picker alongside the
    /// discovered ones so a rig that is switched off still appears -- marked offline -- rather than
    /// looking like the binding was lost. ImmutableArray because the load completes on a background
    /// continuation while the render thread enumerates it.
    /// </summary>
    public ImmutableArray<RemoteRigBinding> BoundRigs { get; set; } = [];

    /// <summary>Whether a rig is currently on screen, so the picker can offer the way back to local.</summary>
    public bool RemoteContextActive { get; set; }

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
    public DropdownMenuState<string> FilterNameDropdown { get; } = new();

    // Profile-switcher dropdown, opened from the profile panel header. Lists AllProfiles plus
    // discovered "tianwen-server" rigs (docs/plans/remote-profile.md).
    public DropdownMenuState<string> ProfileDropdown { get; } = new();

    /// <summary>
    /// When non-null, the profile-switch-refused dialog is up showing this explanation (from
    /// <see cref="TianWen.Lib.Devices.ProfileSwitchVerdict.Describe"/>): the user picked another
    /// profile while equipment was connected or a session was running. Cleared by the dialog's
    /// [OK] button or Esc (<c>DismissActiveState</c>).
    /// </summary>
    public string? ProfileSwitchBlocked { get; set; }

    // Custom filter name input (shared across all slots, activated by "Custom..." entry)
    public int CustomFilterSlotIndex { get; set; } = -1;
    public TextInputState CustomFilterNameInput { get; } = new() { Placeholder = "Filter name..." };


    // OTA property editing: which OTA is being edited (-1 = none)
    public int EditingOtaIndex { get; set; } = -1;

    /// <summary>OTA index armed for removal (-1 = none). First click on a removable OTA's Remove
    /// button arms it (the button turns into a "Confirm?" affordance); a second click on the same
    /// OTA actually removes it. Cleared on Esc (DismissActiveState) or after the removal dispatches.</summary>
    public int PendingRemoveOtaIndex { get; set; } = -1;
    public TextInputState OtaNameInput { get; } = new() { Placeholder = "OTA name" };
    public TextInputState FocalLengthInput { get; } = new() { Placeholder = "Focal length (mm)" };
    public TextInputState ApertureInput { get; } = new() { Placeholder = "Aperture (mm)" };

    // Guider focal length editing
    public TextInputState GuiderFocalLengthInput { get; } = new() { Placeholder = "Guide scope FL (mm)" };

    // Generic device settings editing
    /// <summary>URI path of the device whose settings pane is currently expanded, or null.</summary>
    public string? ExpandedDeviceSettingsUri { get; set; }

    /// <summary>Mutable copy of the device URI being edited (query params are mutated).</summary>
    public Uri? EditingDeviceUri { get; set; }

    /// <summary>The original device URI before edits began (for save/cancel comparison).</summary>
    public Uri? SavedDeviceSettingsUri { get; set; }

    /// <summary>True when the editing URI differs from the saved URI.</summary>
    public bool DeviceSettingsDirty { get; set; }

    /// <summary>Key of the string setting currently being edited, or null.</summary>
    public string? EditingStringSettingKey { get; set; }

    /// <summary>
    /// Whether the "Advanced" sub-section of the expanded device settings pane is open.
    /// Collapsed by default; survives Save (re-opening the same device) but resets when a
    /// different device's pane is expanded.
    /// </summary>
    public bool AdvancedDeviceSettingsExpanded { get; set; }

    /// <summary>Text input state for the active <see cref="DeviceSettingKind.StringEditor"/> field.</summary>
    public TextInputState StringSettingInput { get; } = new();

    /// <summary>
    /// Begins editing device settings for the given device URI.
    /// </summary>
    public void BeginEditingDeviceSettings(Uri deviceUri)
    {
        var deviceKey = deviceUri.GetLeftPart(UriPartial.Path);
        if (ExpandedDeviceSettingsUri != deviceKey)
        {
            // Different device: start with the advanced sub-section collapsed. Re-opening the
            // same device (e.g. after Save) keeps it as the user left it.
            AdvancedDeviceSettingsExpanded = false;
        }
        ExpandedDeviceSettingsUri = deviceKey;
        SavedDeviceSettingsUri = deviceUri;
        EditingDeviceUri = deviceUri;
        DeviceSettingsDirty = false;
    }

    /// <summary>
    /// Discards the device settings editing state.
    /// </summary>
    public void StopEditingDeviceSettings()
    {
        ExpandedDeviceSettingsUri = null;
        EditingDeviceUri = null;
        DeviceSettingsDirty = false;
        AdvancedDeviceSettingsExpanded = false;
    }

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
