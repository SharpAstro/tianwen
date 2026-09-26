using System;

namespace TianWen.Hosting.Dto;

/// <summary>
/// Body of <c>PUT /api/v1/devices/setting</c>: one of a device's settings, as the Equipment tab's text field commits it
/// (P3 part 4 of docs/plans/hardware-in-the-server.md, #930). Only over the node's socket.
/// </summary>
public sealed class DeviceSettingRequestDto
{
    /// <summary>The device, by the URI the profile holds for it.</summary>
    public required string DeviceUri { get; init; }

    /// <summary>The setting's key, one of the device's settings (<c>DeviceBase.Settings</c>).</summary>
    public required string Key { get; init; }

    public required string Value { get; init; }

    /// <summary>
    /// The profile whose slot for the device takes a non-secret setting, written through the node's one profile writer;
    /// null answers the new URI and writes nothing. A masked setting ignores it: it is kept by device, for every profile.
    /// </summary>
    public Guid? ProfileId { get; init; }
}

/// <summary>What <c>PUT /api/v1/devices/setting</c> did. Never carries a secret's value.</summary>
public sealed class DeviceSettingDto
{
    /// <summary>True when the setting was masked and went into the node's credential store, leaving the URI as it was.</summary>
    public required bool Secret { get; init; }

    /// <summary>The device's URI now: carrying the setting as a query parameter, or unchanged for a secret.</summary>
    public required string DeviceUri { get; init; }

    /// <summary>The profile's revision after the write, when a profile took the new URI; null otherwise.</summary>
    public string? Revision { get; init; }
}

/// <summary>Whether a masked setting has a value in the node's credential store (<c>GET /api/v1/devices/setting/secret</c>).</summary>
public sealed class DeviceSecretDto
{
    public required bool IsSet { get; init; }
}
