using NSubstitute;
using Shouldly;
using System;
using System.Collections.Immutable;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the one reading of a device URI (<see cref="DeviceBase.DeviceIdOf"/> and friends) and its
/// use by <see cref="Profile.Detailed"/> for a device the hub has not discovered (#444).
/// </summary>
public class ProfileDetailedDeviceUriTests
{
    [Theory]
    [InlineData("Camera://FakeDevice/FakeCamera1#Fake%20Camera%201")]
    [InlineData("Telescope://AscomDevice/ASCOM.Simulator/Telescope?port=COM3&baud=9600#Sim%20%26%20Co")]
    [InlineData("Focuser://ZWODevice/EAF/12345?focuserBacklashIn=20#ZWO+EAF")]
    [InlineData("Mount://FakeDevice/FakeMount1?port=SkyWatcher")]
    [InlineData("NotAType://Somewhere/Id")]
    public void StaticHelpersAgreeWithTheInstanceProperties(string uri)
    {
        var deviceUri = new Uri(uri);
        var device = new FakeDevice(deviceUri);

        DeviceBase.DeviceTypeOf(deviceUri).ShouldBe(device.DeviceType);
        DeviceBase.DeviceIdOf(deviceUri).ShouldBe(device.DeviceId);
        DeviceBase.DisplayNameOf(deviceUri).ShouldBe(device.DisplayName);
        DeviceBase.DeviceClassOf(deviceUri).ShouldBe(device.DeviceClass);
    }

    [Fact]
    public void HelpersReadAMultiSegmentPathAndAnEncodedFragment()
    {
        var deviceUri = new Uri("Telescope://AscomDevice/ASCOM.Simulator/Telescope?port=COM3#Sim%20%26%20Co");

        // Telescope is the ASCOM alias of Mount.
        DeviceBase.DeviceTypeOf(deviceUri).ShouldBe(DeviceType.Mount);
        DeviceBase.DeviceIdOf(deviceUri).ShouldBe("ASCOM.Simulator/Telescope");
        DeviceBase.DisplayNameOf(deviceUri).ShouldBe("Sim & Co");
        DeviceBase.DeviceClassOf(deviceUri).ShouldBe("AscomDevice", StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void AnUndiscoveredDevicePrintsItsNameIdAndTypeFromItsUri()
    {
        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetDeviceFromUri(Arg.Any<Uri>(), out Arg.Any<DeviceBase?>()).Returns(false);

        var detailed = ProfileWith(
            mount: new Uri("Mount://FakeDevice/FakeMount1?port=SkyWatcher#My%20Mount"),
            focuser: new Uri("Focuser://ZWODevice/EAF/12345")).Detailed(hub);

        detailed.ShouldContain("Mount: My Mount (FakeMount1) [not discovered: Mount via fakedevice]");
        detailed.ShouldContain("Focuser: EAF/12345 [not discovered: Focuser via zwodevice]");
        detailed.ShouldNotContain("[Unknown Device]");
    }

    [Fact]
    public void AUriWithNoIdFallsBackToTheRawUri()
    {
        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetDeviceFromUri(Arg.Any<Uri>(), out Arg.Any<DeviceBase?>()).Returns(false);
        var mount = new Uri("Mount://FakeDevice/#Nameless");

        var detailed = ProfileWith(mount: mount).Detailed(hub);

        detailed.ShouldContain($"Mount: {mount} [Unknown Device]");
    }

    [Fact]
    public void ADiscoveredDevicePrintsTheDiscoveredFormat()
    {
        var mountUri = new Uri("Mount://FakeDevice/FakeMount1?port=SkyWatcher#My%20Mount");
        var discovered = new FakeDevice(mountUri);
        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetDeviceFromUri(Arg.Any<Uri>(), out Arg.Any<DeviceBase?>()).Returns(false);
        hub.TryGetDeviceFromUri(mountUri, out Arg.Any<DeviceBase?>())
            .Returns(call =>
            {
                call[1] = discovered;
                return true;
            });

        var detailed = ProfileWith(mount: mountUri).Detailed(hub);

        detailed.ShouldContain("Mount: My Mount (FakeMount1)\n");
        detailed.ShouldNotContain("not discovered: Mount");
    }

    private static Profile ProfileWith(Uri mount, Uri? focuser = null)
    {
        var camera = new Uri("Camera://FakeDevice/FakeCamera1#Fake%20Camera%201");
        var data = new ProfileData(
            Mount: mount,
            Guider: NoneDevice.Instance.DeviceUri,
            OTAs: ImmutableArray.Create(new OTAData("Scope", 500, camera, Cover: null, Focuser: focuser, FilterWheel: null, PreferOutwardFocus: null, OutwardIsPositive: null)));
        return new Profile(Guid.NewGuid(), "Test", data);
    }
}
