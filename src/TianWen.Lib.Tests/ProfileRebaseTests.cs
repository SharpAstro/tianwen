using Shouldly;
using System;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// An edit the node refused as made against a profile that moved on is made again onto the profile as it is now
/// (<see cref="ProfileDataExtensions.RebasedOnto"/>, P6 part 2 of docs/plans/hardware-in-the-server.md, #936): the fields the
/// edit changed are the edit's, every other field the latest's, so neither change is lost.
/// </summary>
public class ProfileRebaseTests
{
    private static OTAData Ota(string name, int focalLength) =>
        new OTAData(name, focalLength, new FakeDevice(DeviceType.Camera, 1).DeviceUri, null, null, null, null, null);

    private static readonly ProfileData Read = new ProfileData(
        new FakeDevice(DeviceType.Mount, 1).DeviceUri, NoneDevice.Instance.DeviceUri,
        [Ota("Main", 800), Ota("Guide", 240)],
        SiteLatitude: 48.2, SiteLongitude: 16.3);

    [Fact]
    public void AFieldTheEditChangedIsTheEditsAndOneItDidNotIsTheLatests()
    {
        var edited = Read with { GuiderFocalLength = 240 };
        var latest = Read with { SiteElevation = 1234 };

        var rebased = edited.RebasedOnto(Read, latest);

        rebased.GuiderFocalLength.ShouldBe(240);
        rebased.SiteElevation.ShouldBe(1234);
    }

    [Fact]
    public void AFieldBothChangedIsTheEdits()
    {
        var rebased = (Read with { SiteLatitude = 51.5 }).RebasedOnto(Read, Read with { SiteLatitude = -33.9 });

        rebased.SiteLatitude.ShouldBe(51.5, "the edit being made is the later one");
    }

    [Fact]
    public void AnEditOfOneTelescopeLeavesAnotherTelescopesChange()
    {
        var edited = Read with { OTAs = Read.OTAs.SetItem(0, Ota("Refractor", 480)) };
        var latest = Read with { OTAs = Read.OTAs.SetItem(1, Ota("Guide", 400)) };

        var rebased = edited.RebasedOnto(Read, latest);

        rebased.OTAs[0].Name.ShouldBe("Refractor");
        rebased.OTAs[1].FocalLength.ShouldBe(400);
    }

    [Fact]
    public void AnEditThatAddsATelescopeTakesItsListWhole()
    {
        var edited = Read with { OTAs = Read.OTAs.Add(Ota("Third", 1000)) };
        var latest = Read with { OTAs = Read.OTAs.SetItem(1, Ota("Guide", 400)) };

        var rebased = edited.RebasedOnto(Read, latest);

        rebased.OTAs.Length.ShouldBe(3, "an index no longer names the same telescope in both");
        rebased.OTAs[2].Name.ShouldBe("Third");
    }

    [Fact]
    public void AnEditThatLeftTheTelescopesAloneTakesTheLatests()
    {
        var latest = Read with { OTAs = [Ota("Only", 600)] };

        var rebased = (Read with { GuiderFocalLength = 240 }).RebasedOnto(Read, latest);

        rebased.OTAs.ShouldHaveSingleItem().Name.ShouldBe("Only");
    }

    [Fact]
    public void AUriChangedOnlyInItsNameIsAChange()
    {
        // Uri.Equals leaves the fragment out, and the fragment is a device's display name.
        var renamed = new Uri(Read.Mount.GetLeftPart(UriPartial.Query) + "#Renamed mount");
        var rebased = (Read with { Mount = renamed }).RebasedOnto(Read, Read with { SiteElevation = 1 });

        rebased.Mount.OriginalString.ShouldBe(renamed.OriginalString);
    }

    [Fact]
    public void TheDeviceListLeavesOutProfilesAndTheEmptySlotAndFakesUnlessAsked()
    {
        NodeDevice Listed(DeviceBase device) => new NodeDevice(DeviceDto.FromDevice(device, connected: false));
        var fakeCamera = Listed(new FakeDevice(DeviceType.Camera, 1));
        var manualCover = Listed(new ManualCoverDevice());
        var profile = Listed(new Profile(Guid.NewGuid(), "A rig", ProfileData.Empty));
        var none = Listed(NoneDevice.Instance);

        EquipmentActions.ForTheDeviceList([fakeCamera, manualCover, profile, none], includeFake: false)
            .ShouldBe([manualCover]);
        EquipmentActions.ForTheDeviceList([fakeCamera, manualCover, profile, none], includeFake: true)
            .ShouldBe([fakeCamera, manualCover], "by type: camera before cover");
    }

    [Fact]
    public void AListedCameraCarriesWhatItIsAndAnyOtherDeviceNothing()
    {
        var canon = new NodeDevice(DeviceDto.FromDevice(new TianWen.Lib.Devices.Canon.CanonDevice(new Uri("Camera://CanonDevice/r5#EOS R5")), connected: false));
        var mount = new NodeDevice(DeviceDto.FromDevice(new FakeDevice(DeviceType.Mount, 1), connected: false));

        var capabilities = canon.Capabilities.ShouldNotBeNull();
        capabilities.CanCool.ShouldBeFalse();
        capabilities.GainModes.ShouldContain("ISO 100");
        canon.Source.ShouldBe("Canon", "the node's moniker, which a URI alone cannot give");
        mount.Capabilities.ShouldBeNull();
    }
}
