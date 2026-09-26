using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Web;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The rules a profile's writer applies on its own, one copy for the GUI and the node since P3 part 2 of
/// docs/plans/hardware-in-the-server.md (#930): the legacy site moved off the mount's URI, a camera's sensor recorded
/// into its OTA, and a run's backlash mirrored onto its focuser's URI.
/// </summary>
public class ProfileDataRuleTests
{
    private static readonly Uri Camera = new FakeDevice(DeviceType.Camera, 1).DeviceUri;

    private static ProfileData Rig(Uri mount, double? latitude = null, double? longitude = null) => new ProfileData(
        mount, NoneDevice.Instance.DeviceUri, [new OTAData("Main", 800, Camera, null, null, null, null, null)],
        SiteLatitude: latitude, SiteLongitude: longitude);

    [Fact]
    public void ASiteOnTheMountsUriMovesIntoTheProfile()
    {
        var (migrated, changed) = Rig(new Uri("Mount://FakeDevice/FakeMount1?latitude=-37.8&longitude=144.9&elevation=30")).MigrateSiteFromMountUri();

        changed.ShouldBeTrue();
        migrated.Site.ShouldBe(new SiteCoordinates(-37.8, 144.9, 30));
    }

    [Fact]
    public void AProfileWithASiteKeepsItsOwn()
    {
        var rig = Rig(new Uri("Mount://FakeDevice/FakeMount1?latitude=-37.8&longitude=144.9"), latitude: 51.48, longitude: 0);

        var (migrated, changed) = rig.MigrateSiteFromMountUri();

        changed.ShouldBeFalse();
        migrated.Site.ShouldBe(new SiteCoordinates(51.48, 0));
    }

    [Fact]
    public void AMountUriWithNoSiteMovesNothing()
    {
        Rig(new Uri("Mount://FakeDevice/FakeMount1")).MigrateSiteFromMountUri().Changed.ShouldBeFalse();
        Rig(NoneDevice.Instance.DeviceUri).MigrateSiteFromMountUri().Changed.ShouldBeFalse();
    }

    private static ICameraDriver Sensor(double pixelSize, int width, int height)
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.PixelSizeX.Returns(pixelSize);
        camera.CameraXSize.Returns(width);
        camera.CameraYSize.Returns(height);
        return camera;
    }

    [Fact]
    public void ACamerasSensorIsRecordedIntoItsOtaOnce()
    {
        var rig = Rig(NoneDevice.Instance.DeviceUri);

        var captured = rig.CaptureSensorSpecs(Camera, Sensor(3.76, 6248, 4176)).ShouldNotBeNull();

        captured.OTAs[0].CameraPixelSizeUm.ShouldBe(3.76);
        (captured.OTAs[0].CameraSensorWidthPx, captured.OTAs[0].CameraSensorHeightPx).ShouldBe((6248, 4176));
        captured.CaptureSensorSpecs(Camera, Sensor(3.76, 6248, 4176)).ShouldBeNull("the same sensor again is nothing to write");
        captured.CaptureSensorSpecs(Camera, Sensor(4.63, 6248, 4176)).ShouldNotBeNull("a different sensor is");
    }

    [Fact]
    public void ACameraInNoOtaOrWithNoGeometryRecordsNothing()
    {
        var rig = Rig(NoneDevice.Instance.DeviceUri);

        rig.CaptureSensorSpecs(new FakeDevice(DeviceType.Camera, 2).DeviceUri, Sensor(3.76, 6248, 4176)).ShouldBeNull();
        rig.CaptureSensorSpecs(Camera, Sensor(double.NaN, 6248, 4176)).ShouldBeNull();
        rig.CaptureSensorSpecs(Camera, Sensor(3.76, 0, 4176)).ShouldBeNull();
    }

    [Fact]
    public void ARunsBacklashGoesOntoItsFocusersUriAndKeepsTheRest()
    {
        var focuser = new Uri("Focuser://FakeDevice/FakeFocuser1?port=COM7#Fake Focuser 1");
        var noFocuser = Rig(NoneDevice.Instance.DeviceUri);
        var rig = noFocuser with { OTAs = [noFocuser.OTAs[0] with { Focuser = focuser }] };
        var estimates = new Dictionary<Uri, BacklashEstimateRecord> { [focuser] = new BacklashEstimateRecord(30, 45, 5, DateTimeOffset.UnixEpoch) };

        var (mirrored, changed) = rig.WithBacklashEstimates(estimates);

        changed.ShouldBeTrue();
        var query = HttpUtility.ParseQueryString(mirrored.OTAs[0].Focuser.ShouldNotBeNull().Query);
        (query[DeviceQueryKey.FocuserBacklashIn.Key], query[DeviceQueryKey.FocuserBacklashOut.Key], query["port"]).ShouldBe(("30", "45", "COM7"));
        mirrored.WithBacklashEstimates(estimates).Changed.ShouldBeFalse("the same backlash again is nothing to write");
        rig.WithBacklashEstimates(new Dictionary<Uri, BacklashEstimateRecord>()).Changed.ShouldBeFalse();
    }
}
