using Shouldly;
using System;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class OnStepMountTests(ITestOutputHelper outputHelper)
{
    // port=OnStep selects FakeOnStepSerialDevice in FakeDevice.ConnectSerialDevice.
    // Without it, the base falls through to the LX200 fake which can't handle :GU#.
    private static FakeDevice MakeDevice(double siteLat, double siteLong)
        => new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            ["latitude"] = Convert.ToString(siteLat),
            ["longitude"] = Convert.ToString(siteLong),
            ["port"] = "OnStep"
        });

    [Theory(Timeout = 60_000)]
    [InlineData(-37.8743502, 145.1668205)]
    [InlineData(25.28022, 110.29639)]
    public async Task GivenOnStepMountWhenConnectingItOpensSerialPortAndReportsAlignment(double siteLat, double siteLong)
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var device = MakeDevice(siteLat, siteLong);
        var fakeExternal = new FakeExternal(outputHelper);
        await using var mount = new FakeOnStepMountDriver(device, fakeExternal.BuildServiceProvider());

        // when
        await mount.ConnectAsync(ct);

        // then
        mount.Connected.ShouldBe(true);
        (await mount.GetAlignmentAsync(ct)).ShouldBe(AlignmentMode.GermanPolar);
        (await mount.IsTrackingAsync(ct)).ShouldBe(false);
        (await mount.AtParkAsync(ct)).ShouldBe(false);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(48.2, 16.3, 6.75, 16.7)]
    public async Task GivenOnStepMountWhenSlewingItReportsNotSlewingAfterCompletion(double siteLat, double siteLong, double targetRa, double targetDec)
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var device = MakeDevice(siteLat, siteLong);
        var timeProvider = new FakeTimeProviderWrapper();
        var fakeExternal = new FakeExternal(outputHelper, timeProvider);

        await using var mount = new FakeOnStepMountDriver(device, fakeExternal.BuildServiceProvider());

        // when
        await mount.ConnectAsync(ct);
        await mount.SetTrackingAsync(true, ct);
        (await mount.IsTrackingAsync(ct)).ShouldBe(true); // OnStep :Te# returned 1 → :GU# omits 'n'

        await mount.BeginSlewRaDecAsync(targetRa, targetDec, ct);
        (await mount.IsSlewingAsync(ct)).ShouldBe(true); // :GU# omits 'N' while slewing

        while (await mount.IsSlewingAsync(ct))
        {
            await timeProvider.SleepAsync(TimeSpan.FromSeconds(1), ct);
        }

        // then
        (await mount.IsSlewingAsync(ct)).ShouldBe(false);
        (await mount.IsTrackingAsync(ct)).ShouldBe(true);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(48.2, 16.3)]
    public async Task GivenOnStepMountWhenParkingItPollsGuStatusUntilParked(double siteLat, double siteLong)
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var device = MakeDevice(siteLat, siteLong);
        var timeProvider = new FakeTimeProviderWrapper();
        var fakeExternal = new FakeExternal(outputHelper, timeProvider);
        await using var mount = new FakeOnStepMountDriver(device, fakeExternal.BuildServiceProvider());

        await mount.ConnectAsync(ct);
        await mount.SetTrackingAsync(true, ct);
        (await mount.IsTrackingAsync(ct)).ShouldBe(true);
        (await mount.AtParkAsync(ct)).ShouldBe(false);

        // when — fake transitions p → I → P over ~300ms fake time;
        // ParkAsync's 250ms poll loop must wait through the 'I' tick before seeing 'P'.
        await mount.ParkAsync(ct);

        // then
        (await mount.AtParkAsync(ct)).ShouldBe(true);
        (await mount.IsTrackingAsync(ct)).ShouldBe(false); // parking stops tracking
    }

    [Theory(Timeout = 60_000)]
    [InlineData(48.2, 16.3)]
    public async Task GivenParkedOnStepMountWhenUnparkingItPollsGuStatusUntilNotParked(double siteLat, double siteLong)
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var device = MakeDevice(siteLat, siteLong);
        var timeProvider = new FakeTimeProviderWrapper();
        var fakeExternal = new FakeExternal(outputHelper, timeProvider);
        await using var mount = new FakeOnStepMountDriver(device, fakeExternal.BuildServiceProvider());

        await mount.ConnectAsync(ct);
        await mount.ParkAsync(ct);
        (await mount.AtParkAsync(ct)).ShouldBe(true);

        // when
        await mount.UnparkAsync(ct);

        // then — :hR# is immediate in the fake (no I→p transition needed)
        (await mount.AtParkAsync(ct)).ShouldBe(false);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(48.2, 16.3)]
    public async Task GivenOnStepMountWhenQueryingAxisPositionItReturnsEncoderCounts(double siteLat, double siteLong)
    {
        var ct = TestContext.Current.CancellationToken;
        var device = MakeDevice(siteLat, siteLong);
        var fakeExternal = new FakeExternal(outputHelper);
        await using var mount = new FakeOnStepMountDriver(device, fakeExternal.BuildServiceProvider());

        await mount.ConnectAsync(ct);

        var ra = await mount.GetAxisPositionAsync(TelescopeAxis.Primary, ct);
        var dec = await mount.GetAxisPositionAsync(TelescopeAxis.Seconary, ct);
        var tertiary = await mount.GetAxisPositionAsync(TelescopeAxis.Tertiary, ct);

        ra.ShouldNotBeNull();
        dec.ShouldNotBeNull();
        tertiary.ShouldBeNull("OnStep has only two mechanical axes");

        // Fake home position: HA=6h, Dec=+90° → RA counts should be positive, Dec positive.
        ra.Value.ShouldBeGreaterThan(0);
        dec.Value.ShouldBeGreaterThan(0);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(48.2, 16.3)]
    public async Task GivenOnStepMountCapabilitiesAreReportedCorrectly(double siteLat, double siteLong)
    {
        var ct = TestContext.Current.CancellationToken;
        var device = MakeDevice(siteLat, siteLong);
        var fakeExternal = new FakeExternal(outputHelper);
        await using var mount = new FakeOnStepMountDriver(device, fakeExternal.BuildServiceProvider());

        await mount.ConnectAsync(ct);

        // OnStep advertises native unpark + set-park
        mount.CanPark.ShouldBe(true);
        mount.CanUnpark.ShouldBe(true);
        mount.CanSetPark.ShouldBe(true);
        mount.TrackingSpeeds.ShouldContain(TrackingSpeed.Sidereal);
        mount.TrackingSpeeds.ShouldContain(TrackingSpeed.Lunar);
        mount.TrackingSpeeds.ShouldContain(TrackingSpeed.Solar);
        mount.TrackingSpeeds.ShouldContain(TrackingSpeed.King);
    }
}
