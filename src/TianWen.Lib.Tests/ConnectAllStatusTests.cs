using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Whether Connect All can act is asked of what the NODE can reach, never of the Equipment tab's list, which leaves the
/// fake devices out unless asked (found in the ZWO live check, 2026-09-28: a profile of fake devices read "Discover
/// first" after every plain Discover, while the node listed each of them).
/// </summary>
public class ConnectAllStatusTests
{
    private static readonly Uri Mount = new Uri("Mount://FakeDevice/FakeMount1?latitude=48.2&longitude=16.3#Fake Mount");
    private static readonly Uri Guider = new Uri("Guider://FakeDevice/FakeGuider1#Fake Guider 1");

    private static readonly ProfileData Profile = ProfileData.Empty with { Mount = Mount, Guider = Guider };

    private static readonly IReadOnlyDictionary<Uri, byte> NothingPending = new Dictionary<Uri, byte>();

    private static NodeDevice Listed(string uri, string type) => new NodeDevice(new DeviceDto
    {
        Uri = uri,
        DisplayName = uri,
        DeviceId = uri,
        DeviceType = type,
        Connected = false,
    });

    // As the node lists them: the mount without the site its profile URI carries, which is still the same device.
    private static readonly ImmutableArray<NodeDevice> TheNodesListing =
    [
        Listed("Mount://FakeDevice/FakeMount1#Fake Mount", "Mount"),
        Listed("Guider://FakeDevice/FakeGuider1#Fake Guider 1", "Guider"),
    ];

    [Fact]
    public void AProfileOfFakeDevicesTheNodeListedIsReadyToConnect()
    {
        EquipmentActions.ComputeConnectAllStatus(Profile, node: null, TheNodesListing, NothingPending, isDiscovering: false)
            .ShouldBe(new EquipmentActions.ConnectAllStatus(Visible: true, Enabled: true, Label: "Connect All"));
    }

    [Fact]
    public void ADeviceTheNodeNeverListedAsksForADiscovery()
    {
        EquipmentActions.ComputeConnectAllStatus(Profile, node: null, [TheNodesListing[0]], NothingPending, isDiscovering: false)
            .ShouldBe(new EquipmentActions.ConnectAllStatus(Visible: true, Enabled: false, Label: "Discover first"));
    }

    [Fact]
    public void NothingIsConnectedWhileTheNodeIsStillDiscovering()
    {
        EquipmentActions.ComputeConnectAllStatus(Profile, node: null, TheNodesListing, NothingPending, isDiscovering: true)
            .ShouldBe(new EquipmentActions.ConnectAllStatus(Visible: true, Enabled: false, Label: "Discovering…"));
    }
}
