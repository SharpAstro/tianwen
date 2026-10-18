using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A filter wheel's slots live on its URI (<see cref="FilterWheelSlots"/>), and the Equipment tab's filter table reads
/// them only there, so a wheel whose URI lists none showed "Filters (0)" with nothing to edit. Discovery writes them where
/// the source knows the wheel's slots (the fake wheels, as ZWO's and QHY's always have), and a wheel that knows them only
/// once connected (Alpaca, ASCOM) has them written onto its URI by its connect (<c>CaptureFilterSlots</c>).
/// </summary>
public class FilterWheelSlotsTests
{
    private static string[] Names(IReadOnlyList<InstalledFilter> filters) => [.. filters.Select(static f => f.DisplayName)];

    [Fact]
    public void EveryFakeWheelIsDiscoveredWithItsSlotsOnItsUriAndItsPresetInItsName()
    {
        var wheels = new FakeDeviceSource().RegisteredDevices(DeviceType.FilterWheel).ToArray();

        wheels.Select(static w => w.DisplayName).ShouldBe([
            "Fake Filter Wheel 1 (LRGB)", "Fake Filter Wheel 2 (LRGBSHO)", "Fake Filter Wheel 3 (Planetary)",
            "Fake Filter Wheel 4 (LSHOD)", "Fake Filter Wheel 5 (LHRGB)"]);
        for (var i = 0; i < wheels.Length; i++)
        {
            FilterWheelSlots.Read(wheels[i].DeviceUri).ShouldBe(FakeFilterWheelDriver.GetDefaultFiltersForId(i + 1),
                $"{wheels[i].DisplayName} lists the slots its driver has");
        }
    }

    [Fact]
    public void ThePresetsAreThePlanetaryAndDeepSkyWheelsARigIsSetUpWith()
    {
        Names(FakeFilterWheelDriver.GetDefaultFiltersForId(3)).ShouldBe(["Luminance", "Red", "Green", "Blue", "IR 685", "CH4"]);
        Names(FakeFilterWheelDriver.GetDefaultFiltersForId(4)).ShouldBe(["Luminance", "SII", "H-Alpha", "OIII", "Dark"]);
        Names(FakeFilterWheelDriver.GetDefaultFiltersForId(5)).ShouldBe(["Luminance", "H-Alpha", "Red", "Green", "Blue"]);
        Names(FakeFilterWheelDriver.GetDefaultFiltersForId(2)).ShouldBe(["Luminance", "H-Alpha", "OIII", "SII", "Red", "Green", "Blue"]);
        Names(FakeFilterWheelDriver.GetDefaultFiltersForId(1)).ShouldBe(["Luminance", "Red", "Green", "Blue"],
            "FakeFilterWheel1 stays the LRGB wheel the session tests are written against");
    }

    [Fact]
    public void SlotsWrittenOntoAUriReplaceItsOldOnesAndKeepEveryOtherKey()
    {
        var uri = new Uri("FilterWheel://AlpacaDevice/wheel0?host=127.0.0.1&filter1=Red&offset1=20&filter2=Green&offset2=0&filter3=Blue&offset3=-15#Wheel");

        var written = FilterWheelSlots.WithSlots(uri, [new InstalledFilter("Luminance"), new InstalledFilter("H-Alpha", 21)]);

        FilterWheelSlots.Read(written).ShouldBe([new InstalledFilter("Luminance"), new InstalledFilter("H-Alpha", 21)]);
        var query = HttpUtility.ParseQueryString(written.Query);
        query["host"].ShouldBe("127.0.0.1");
        query["filter3"].ShouldBeNull("a slot the new list does not have is gone");
        written.Fragment.ShouldBe("#Wheel");
    }

    private static readonly Uri BareWheel = new Uri("FilterWheel://AlpacaDevice/wheel0?host=127.0.0.1#Alpaca Wheel");

    private static ProfileData Rig(Uri? wheel) => new ProfileData(
        NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri,
        [new OTAData("Main", 800, new FakeDevice(DeviceType.Camera, 1).DeviceUri, null, null, wheel, null, null)]);

    private static IFilterWheelDriver Reporting(params InstalledFilter[] filters)
    {
        var wheel = Substitute.For<IFilterWheelDriver>();
        wheel.Filters.Returns(filters);
        return wheel;
    }

    [Fact]
    public void AConnectedWheelsSlotsAreWrittenOntoAUriThatListsNone()
    {
        var captured = Rig(BareWheel).CaptureFilterSlots(BareWheel, Reporting(new InstalledFilter("Red", 20), new InstalledFilter("OIII", -3)))
            .ShouldNotBeNull();

        var assigned = captured.OTAs[0].FilterWheel.ShouldNotBeNull();
        FilterWheelSlots.Read(assigned).ShouldBe([new InstalledFilter("Red", 20), new InstalledFilter("OIII", -3)]);
        HttpUtility.ParseQueryString(assigned.Query)["host"].ShouldBe("127.0.0.1");
        captured.CaptureFilterSlots(BareWheel, Reporting(new InstalledFilter("Red", 20))).ShouldBeNull(
            "once listed, the slots are the user's: a connect never replaces them");
    }

    [Fact]
    public void AWheelThatReportsNoSlotsOrIsInNoOtaWritesNothing()
    {
        Rig(BareWheel).CaptureFilterSlots(BareWheel, Reporting()).ShouldBeNull();
        Rig(null).CaptureFilterSlots(BareWheel, Reporting(new InstalledFilter("Red"))).ShouldBeNull();
        Rig(BareWheel).CaptureFilterSlots(new Uri("FilterWheel://AlpacaDevice/wheel1?host=127.0.0.1"), Reporting(new InstalledFilter("Red")))
            .ShouldBeNull("another wheel");
    }
}
