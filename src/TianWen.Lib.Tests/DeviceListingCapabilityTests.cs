using Shouldly;
using System;
using System.Text.Json;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Canon;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The node's device listing says what a camera can do (P6 part 1 of docs/plans/hardware-in-the-server.md, #936): its
/// named gains and whether it has a cooler, which a session's camera settings are offered by. Both are what the device IS,
/// so a client knows them with nothing connected, as the GUI knew them from its own device registry before the cut.
/// </summary>
public class DeviceListingCapabilityTests
{
    private static DeviceDto AcrossTheWire(DeviceDto device)
    {
        var json = JsonSerializer.Serialize(new[] { device }, HostingJsonContext.Default.DeviceDtoArray);
        return JsonSerializer.Deserialize(json, HostingJsonContext.Default.DeviceDtoArray).ShouldNotBeNull().ShouldHaveSingleItem();
    }

    [Fact]
    public void ADslrNamesItsGainsAndHasNoCooler()
    {
        var canon = AcrossTheWire(DeviceDto.FromDevice(new CanonDevice(new Uri("Camera://CanonDevice/r5#EOS R5")), connected: false));

        canon.CanCool.ShouldBe(false);
        canon.GainModes.ShouldNotBeNull().ShouldContain("ISO 100");
    }

    [Fact]
    public void AnAstroCameraTakesAGainValueAndCools()
    {
        var camera = AcrossTheWire(DeviceDto.FromDevice(new FakeDevice(DeviceType.Camera, 1), connected: false));

        camera.CanCool.ShouldBe(true);
        camera.GainModes.ShouldBeNull("it takes a gain value, not a named one");
    }

    [Fact]
    public void AnythingButACameraSaysNothingOfEither()
    {
        var mount = AcrossTheWire(DeviceDto.FromDevice(new FakeDevice(DeviceType.Mount, 1), connected: false));

        mount.CanCool.ShouldBeNull();
        mount.GainModes.ShouldBeNull();
    }
}
