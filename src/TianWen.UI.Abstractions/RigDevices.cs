using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// What a rig's view shows of its devices while its node runs nothing (P5b part 9 of docs/plans/hardware-in-the-server.md,
/// #935): the OTAs of the rig's own profile and its mount, from the device states its node reads (<c>GET
/// /api/v1/devices/state</c>). This computer's idle view reads its devices through its hub; the node reads a rig's through
/// the same readers (<see cref="DeviceHubReadingExtensions"/>), and both become a view's telemetry through the same builder
/// (<see cref="PreviewOTATelemetry.From"/>), so an idle rig's Live Session lays out its OTAs, its mount and its limit as
/// this computer's does. Before this a rig with no run showed "No OTAs configured in profile".
/// </summary>
public static class RigDevices
{
    /// <summary>
    /// Puts the devices a rig's node reads onto the rig's view: each OTA of <paramref name="profile"/> from its camera,
    /// focuser and filter wheel, and the mount with the node's limit verdict for it. A device the node does not hold
    /// connected reads as not connected, and a mount it does not points nowhere known (<see cref="MountState.Unknown"/>),
    /// exactly as this computer's view reads its own.
    /// </summary>
    public static void Apply(LiveSessionState view, ProfileData profile, IReadOnlyList<DeviceStateDto> devices)
    {
        var otas = ImmutableArray.CreateBuilder<PreviewOTATelemetry>(profile.OTAs.Length);
        foreach (var ota in profile.OTAs)
        {
            otas.Add(PreviewOTATelemetry.From(
                ota,
                Connected(devices, ota.Camera)?.Camera?.ToReading(),
                ota.Focuser is { } focuser ? Connected(devices, focuser)?.Focuser?.ToReading() : null,
                ota.FilterWheel is { } filterWheel ? Connected(devices, filterWheel)?.FilterWheel?.ToReading() : null));
        }
        view.ResizePreviewArrays(otas.Count);
        view.PreviewOTATelemetry = otas.MoveToImmutable();

        if (profile.Mount is not { Scheme: not "none" } mountUri)
        {
            return;
        }
        if (Connected(devices, mountUri) is { Mount: { } mount } mountDevice)
        {
            view.MountState = mount.ToState();
            if (mountDevice.DisplayName is { } name)
            {
                view.MountDisplayName = name;
            }
            view.MountLimitVerdict = mount.Limit?.ToVerdict() ?? MountLimitVerdict.Clear;
        }
        else
        {
            // Not connected, as this computer's idle view reads a mount its hub does not hold: the name stays.
            view.MountState = MountState.Unknown;
            view.MountLimitVerdict = MountLimitVerdict.Clear;
        }
    }

    /// <summary>The device the node holds connected by the hub's identity rule (scheme, host and path), or null.</summary>
    private static DeviceStateDto? Connected(IReadOnlyList<DeviceStateDto> devices, Uri uri)
    {
        foreach (var device in devices)
        {
            if (device.Connected && Uri.TryCreate(device.DeviceUri, UriKind.Absolute, out var deviceUri) && DeviceBase.SameDevice(deviceUri, uri))
            {
                return device;
            }
        }
        return null;
    }
}
