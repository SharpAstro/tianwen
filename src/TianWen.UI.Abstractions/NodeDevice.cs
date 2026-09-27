using System;
using System.Collections.Generic;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// What a camera IS, for the session's camera settings: its named gains (a DSLR's ISO steps; empty for a camera that takes
/// a gain value) and whether it has a cooler to set.
/// </summary>
public readonly record struct CameraCapabilities(IReadOnlyList<string> GainModes, bool CanCool)
{
    /// <summary>What a camera nothing is known of is taken to be: a gain value, and a cooler.</summary>
    public static CameraCapabilities Unknown => new CameraCapabilities([], CanCool: true);
}

/// <summary>
/// A device the node listed (<c>GET /api/v1/devices/structured</c>), as the Equipment tab lists and assigns it (P6 of
/// docs/plans/hardware-in-the-server.md, #936). A client of the node has none of the vendor device types (they live with the
/// drivers, in the node), so a listed device is this one type, carrying what the node said the device is: its source
/// moniker and, for a camera, its <see cref="CameraCapabilities"/>. Its identity is its URI, as for every device.
/// </summary>
public sealed record class NodeDevice : DeviceBase
{
    private readonly string? _source;

    public NodeDevice(DeviceDto listed) : base(new Uri(listed.Uri))
    {
        _source = listed.Source;
        Capabilities = DeviceType is DeviceType.Camera
            ? new CameraCapabilities(listed.GainModes ?? [], listed.CanCool ?? true)
            : null;
    }

    /// <summary>What the camera is, as the node listed it; null for any other device.</summary>
    public CameraCapabilities? Capabilities { get; }

    /// <summary>The node's moniker for the device, else what its URI's host says.</summary>
    public override string Source => _source ?? DeviceClass;
}
