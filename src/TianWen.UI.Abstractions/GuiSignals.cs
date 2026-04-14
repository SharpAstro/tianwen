using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>Request device discovery.</summary>
public readonly record struct DiscoverDevicesSignal(bool IncludeFake = false);

/// <summary>Add a new OTA to the active profile.</summary>
public readonly record struct AddOtaSignal;

/// <summary>Start editing site coordinates.</summary>
public readonly record struct EditSiteSignal;

/// <summary>Start creating a new profile.</summary>
public readonly record struct CreateProfileSignal;

/// <summary>Assign a discovered device to the active slot.</summary>
public readonly record struct AssignDeviceSignal(int DeviceIndex);

/// <summary>Connect a device via the device hub (out-of-session).</summary>
public readonly record struct ConnectDeviceSignal(System.Uri DeviceUri);

/// <summary>Disconnect a device via the device hub (out-of-session).
/// The handler safety-checks first; for an unsafe device it sets a confirmation
/// state instead of disconnecting immediately.</summary>
public readonly record struct DisconnectDeviceSignal(System.Uri DeviceUri);

/// <summary>Warm a cooled camera to ambient (gradual ramp), then disconnect.
/// Posted from the per-row "Warm &amp; Disconnect" confirmation button.</summary>
public readonly record struct WarmAndDisconnectDeviceSignal(System.Uri DeviceUri);

/// <summary>Force-disconnect bypassing safety checks (no warm-up, no idle wait).
/// Posted only after the secondary force-confirmation button is clicked.</summary>
public readonly record struct ForceDisconnectDeviceSignal(System.Uri DeviceUri);

/// <summary>Set the cooler setpoint (°C) on a hub-connected camera and turn the cooler on.</summary>
public readonly record struct SetCoolerSetpointSignal(System.Uri DeviceUri, double SetpointC);

/// <summary>Direct cooler-off (bypass safety). Posted only after force confirmation,
/// or when the camera is already safe (idle + near ambient).</summary>
public readonly record struct SetCoolerOffSignal(System.Uri DeviceUri);

/// <summary>Warm a cooled camera to ambient (gradual ramp), then turn the cooler off
/// without disconnecting. Posted from the cooler-panel "Warm up &amp; Off" confirmation.</summary>
public readonly record struct WarmAndCoolerOffSignal(System.Uri DeviceUri);

/// <summary>Update profile data (filter config, OTA props, etc.).</summary>
public readonly record struct UpdateProfileSignal(ProfileData Data);

/// <summary>Build the observation schedule from pinned targets.</summary>
public readonly record struct BuildScheduleSignal;

/// <summary>Toggle fullscreen mode.</summary>
public readonly record struct ToggleFullscreenSignal;

/// <summary>Request plate solving the current image.</summary>
public readonly record struct PlateSolveSignal;

/// <summary>Planner session state changed (proposals, sliders, settings). Triggers auto-save.</summary>
public readonly record struct SavePlannerSessionSignal;

/// <summary>Session configuration changed. Triggers auto-save.</summary>
public readonly record struct SaveSessionConfigSignal;

/// <summary>Start a new session from the current profile, config, and schedule.</summary>
public readonly record struct StartSessionSignal;

/// <summary>Request abort — shows confirmation strip in the live session tab.</summary>
public readonly record struct RequestAbortSessionSignal;

/// <summary>Confirmed abort — cancels the running session.</summary>
public readonly record struct ConfirmAbortSessionSignal;
