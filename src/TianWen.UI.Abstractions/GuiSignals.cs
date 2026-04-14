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

/// <summary>Disconnect a device via the device hub (out-of-session).</summary>
public readonly record struct DisconnectDeviceSignal(System.Uri DeviceUri);

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
