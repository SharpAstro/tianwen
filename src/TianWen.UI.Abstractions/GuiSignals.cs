using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>Request device discovery.</summary>
public readonly record struct DiscoverDevicesSignal;

/// <summary>Add a new OTA to the active profile.</summary>
public readonly record struct AddOtaSignal;

/// <summary>Start editing site coordinates.</summary>
public readonly record struct EditSiteSignal;

/// <summary>Start creating a new profile.</summary>
public readonly record struct CreateProfileSignal;

/// <summary>Assign a discovered device to the active slot.</summary>
public readonly record struct AssignDeviceSignal(int DeviceIndex);

/// <summary>Update profile data (filter config, OTA props, etc.).</summary>
public readonly record struct UpdateProfileSignal(ProfileData Data);
