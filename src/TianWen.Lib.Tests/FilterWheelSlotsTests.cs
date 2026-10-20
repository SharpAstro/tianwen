using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A filter wheel's slots on its URI (<see cref="FilterWheelSlots"/>) are only ever the USER's: the Equipment tab's table writes
/// them, and nothing writes a driver's there. Where a URI lists none the tab SHOWS the connected wheel's own slots, names and focus
/// offsets as the node reads them (<see cref="EquipmentActions.FiltersOnShow"/>), so an Alpaca, ASCOM or fake wheel no longer shows
/// "Filters (0)". Review round 1 (2026-10-20): a driver's names written onto the URI read back as overrides and froze them, and a
/// fake's preset on its URI came back on every rediscovery.
/// </summary>
public class FilterWheelSlotsTests
{
    private static string[] Names(IReadOnlyList<InstalledFilter> filters) => [.. filters.Select(static f => f.DisplayName)];

    [Fact]
    public void EveryFakeWheelIsNamedForItsPresetAndItsUriListsNoSlots()
    {
        var wheels = new FakeDeviceSource().RegisteredDevices(DeviceType.FilterWheel).ToArray();

        wheels.Select(static w => w.DisplayName).ShouldBe([
            "Fake Filter Wheel 1 (LRGB)", "Fake Filter Wheel 2 (LRGBSHO)", "Fake Filter Wheel 3 (Planetary)",
            "Fake Filter Wheel 4 (LSHO)", "Fake Filter Wheel 5 (LHRGB)"]);
        wheels.ShouldAllBe(static w => FilterWheelSlots.Read(w.DeviceUri).Count == 0,
            "a URI's slots are the user's; the driver reports the preset's");
    }

    [Fact]
    public void ThePresetsAreThePlanetaryAndDeepSkyWheelsARigIsSetUpWith()
    {
        Names(FakeFilterWheelDriver.GetDefaultFiltersForId(3)).ShouldBe(["Luminance", "Red", "Green", "Blue", "IR 685", "CH4"]);
        Names(FakeFilterWheelDriver.GetDefaultFiltersForId(4)).ShouldBe(["Luminance", "SII", "H-Alpha", "OIII"],
            "no blank for darks, which a flat run would expose through");
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

    private static ProfileData Rig(Uri wheel) => new ProfileData(
        NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri,
        [new OTAData("Main", 800, new FakeDevice(DeviceType.Camera, 1).DeviceUri, null, null, wheel, null, null)]);

    private static PreviewOTATelemetry Reporting(string[] names, int[] offsets) => PreviewOTATelemetry.Unknown with
    {
        FilterWheelConnected = true, FilterSlots = [.. names], FilterOffsets = [.. offsets],
    };

    [Fact]
    public void AUriThatListsNoSlotsShowsTheConnectedWheelsOwnWithTheirOffsets()
    {
        var (filters, fromWheel) = EquipmentActions.FiltersOnShow(Rig(BareWheel), 0, Reporting(["Ha", "OIII"], [21, -3]));

        fromWheel.ShouldBeTrue();
        filters.ShouldBe([new InstalledFilter("Ha", 21), new InstalledFilter("OIII", -3)]);
    }

    [Fact]
    public void TheUsersSlotsOnTheUriWinOverWhatTheWheelReports()
    {
        var mine = FilterWheelSlots.WithSlots(BareWheel, [new InstalledFilter("Red", 8)]);

        var (filters, fromWheel) = EquipmentActions.FiltersOnShow(Rig(mine), 0, Reporting(["Ha", "OIII"], [21, -3]));

        fromWheel.ShouldBeFalse();
        filters.ShouldBe([new InstalledFilter("Red", 8)]);
    }

    [Fact]
    public void AWheelNotConnectedOrNamingNoSlotsShowsNone()
    {
        EquipmentActions.FiltersOnShow(Rig(BareWheel), 0, null).Filters.ShouldBeEmpty();
        EquipmentActions.FiltersOnShow(Rig(BareWheel), 0, PreviewOTATelemetry.Unknown).Filters.ShouldBeEmpty();
        EquipmentActions.FiltersOnShow(Rig(BareWheel), 0, Reporting([], [])).Filters.ShouldBeEmpty();
    }

    [Fact]
    public void AWheelsOffsetsCrossTheWireBesideItsSlots()
    {
        var reading = new FilterWheelReading(1, "OIII", ["Ha", "OIII"], [21, -3]);

        var back = FilterWheelDeviceStateDto.FromReading(reading).ToReading();

        back.Slots.ShouldBe(["Ha", "OIII"]);
        back.Offsets.ShouldBe([21, -3]);
    }
}
