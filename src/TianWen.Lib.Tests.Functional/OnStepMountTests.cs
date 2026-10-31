using Shouldly;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.OnStep;
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

    // OnStep refuses a goto with the :MS# digit ALONE, named by OnStep's own codes ("below the horizon limit", where Meade's
    // table says "below horizon limit"), and the port takes the next command. The hang this fixed (waiting for Meade's
    // #-terminated message held the port's lock until the node restarted, #1916) needs a port that BLOCKS on an empty read,
    // which the fake does not: it answers nothing at once, so this pins the wording and the free port, not the hang.
    [Theory(Timeout = 60_000)]
    [InlineData(-37.8743502, 145.1668205, 80.0)]
    [InlineData(48.2, 16.3, -80.0)]
    public async Task GivenOnStepWhenAGotoIsRefusedTheRefusalIsInOnStepsWordsAndThePortIsFree(double siteLat, double siteLong, double belowHorizonDec)
    {
        var ct = TestContext.Current.CancellationToken;
        var device = MakeDevice(siteLat, siteLong);
        var fakeExternal = new FakeExternal(outputHelper);
        await using var mount = new FakeOnStepMountDriver(device, fakeExternal.BuildServiceProvider());
        await mount.ConnectAsync(ct);

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => mount.BeginSlewRaDecAsync(6.0, belowHorizonDec, ct).AsTask());

        refused.Message.ShouldContain("below the horizon limit");
        (await mount.IsSlewingAsync(ct)).ShouldBeFalse("the port is free for the next command");
    }

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
    [InlineData(-37.8743502, 145.1668205, GuideDirection.West, 'w')]
    [InlineData(-37.8743502, 145.1668205, GuideDirection.East, 'e')]
    [InlineData(-37.8743502, 145.1668205, GuideDirection.North, 'n')]
    [InlineData(-37.8743502, 145.1668205, GuideDirection.South, 's')]
    [InlineData(48.2, 16.3, GuideDirection.West, 'w')]
    [InlineData(48.2, 16.3, GuideDirection.East, 'e')]
    public async Task GivenAHeldButtonTheMountMovesThatWayAndStopsOnRelease(double siteLat, double siteLong, GuideDirection direction, char expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var device = MakeDevice(siteLat, siteLong);
        var fakeExternal = new FakeExternal(outputHelper);
        await using var mount = new FakeOnStepMountDriver(device, fakeExternal.BuildServiceProvider());
        await mount.ConnectAsync(ct);
        var fake = mount.SerialDevice.ShouldBeOfType<FakeOnStepSerialDevice>();

        var (axis, rate) = MountJog.AxisRateFor(direction, 8, siteLat);
        mount.CanMoveAxis(axis).ShouldBeTrue();
        var before = fake.Commands.Length;
        await mount.MoveAxisAsync(axis, rate, ct);

        fake.Moving.ShouldBe(new[] { expected });
        (axis is TelescopeAxis.Primary ? fake.MoveRateRa : fake.MoveRateDec).ShouldBe(Math.Abs(rate), 1e-8, "the rate as asked, in degrees a second");
        fake.Commands[before..].ShouldNotContain(c => c.StartsWith(":Q", StringComparison.Ordinal),
            "no stop before a start: on the TeeSeek's OnStep a :Qw# just before :Me# left the axis standing");

        await mount.MoveAxisAsync(axis, 0, ct);

        fake.Moving.ShouldBeEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenOnStepWhenConnectingItsClockIsSetAndReadsBackOurs()
    {
        // The TeeSeek's OnStep sat 11 hours off and refused a goto to Venus, high in the west, as below the horizon:
        // nothing set its clock on connect, only a session's start did.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper();
        await using var mount = new FakeOnStepMountDriver(MakeDevice(-37.8743502, 145.1668205), new FakeExternal(outputHelper, timeProvider).BuildServiceProvider());

        await mount.ConnectAsync(ct);

        var commands = mount.SerialDevice.ShouldNotBeNull().Commands.ToList();
        var sc = commands.FindIndex(c => c.StartsWith(":SC", StringComparison.Ordinal));
        var sl = commands.FindIndex(c => c.StartsWith(":SL", StringComparison.Ordinal));
        sc.ShouldBeGreaterThanOrEqualTo(0);
        sl.ShouldBeGreaterThan(sc, "date, then time, as the ASCOM OnStep driver sets them");
        commands.ShouldNotContain(c => c.StartsWith(":SG", StringComparison.Ordinal), "the mount's own UTC offset is left as it is");
        mount.TimeIsSetByUs.ShouldBeTrue();
        var mountUtc = (await mount.TryGetUTCDateFromMountAsync(ct)).ShouldNotBeNull();
        (mountUtc - timeProvider.GetUtcNow().UtcDateTime).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(2));

        // What was SENT, not what the fake reads back: the fake's own clock is the time provider's, so the read-back is true
        // whatever :SC and :SL carried. Melbourne's mount offset (:GG# answers -11, the hours added to local to give UTC) is
        // non-zero, so a clock sent as UTC, an 11 hour error, cannot pass.
        var fake = mount.SerialDevice.ShouldBeOfType<FakeOnStepSerialDevice>();
        fake.UtcOffset = TimeSpan.FromHours(-11);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        await mount.SetUTCDateAsync(utcNow, ct);

        var sentLocal = fake.ClockDateSent.ShouldNotBeNull().Date + fake.ClockTimeSent.ShouldNotBeNull();
        (sentLocal - (utcNow - fake.UtcOffset)).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1), "the local time the offset implies, not UTC");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenACommandLeftHalfSentWhenConnectingTheFirstCommandIsNotSwallowed()
    {
        // A discovery probe of another family left ":e1\r" in OnStep's buffer; the driver's :GVP# was read as its
        // tail and the connect waited for ever on a reply that never came.
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            ["latitude"] = Convert.ToString(-37.8743502),
            ["longitude"] = Convert.ToString(145.1668205),
            ["port"] = "OnStep",
            ["staleCommandBuffer"] = "true"
        });
        var fakeExternal = new FakeExternal(outputHelper);
        await using var mount = new FakeOnStepMountDriver(device, fakeExternal.BuildServiceProvider());

        await mount.ConnectAsync(ct);

        mount.Connected.ShouldBe(true);
        mount.SerialDevice.ShouldNotBeNull().Commands[0].ShouldBe("#");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(48.2, 16.3, 6.75, 16.7)]
    [InlineData(-37.8743502, 145.1668205, 11.11, -45.125)]
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

        // #837: OnStep inherits the LX200 formatter; the declination the fake parsed is the one asked for
        (await mount.GetTargetDeclinationAsync(ct)).ShouldBe(targetDec, 1 / 3600d);
        (await mount.GetDeclinationAsync(ct)).ShouldBe(targetDec, 1 / 3600d);
    }

    // OnStepX refuses an unsigned :Sd with 0 (signPresent = true), so a northern target is sent with its '+'.
    [Theory(Timeout = 60_000)]
    [InlineData(48.2, 16.3, 6.75, 45.125, ":Sd+45*07:30#")]
    [InlineData(-37.8743502, 145.1668205, 11.11, 0.15, ":Sd+00*09:00#")]
    [InlineData(-37.8743502, 145.1668205, 11.11, -45.125, ":Sd-45*07:30#")]
    public async Task GivenOnStepMountWhenSlewingTheDeclinationIsSentSigned(double siteLat, double siteLong, double targetRa, double targetDec, string expectedSd)
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper();
        await using var mount = new FakeOnStepMountDriver(MakeDevice(siteLat, siteLong), new FakeExternal(outputHelper, timeProvider).BuildServiceProvider());
        await mount.ConnectAsync(ct);

        // when
        await mount.BeginSlewRaDecAsync(targetRa, targetDec, ct);

        // then
        mount.SerialDevice.ShouldNotBeNull().Commands.ShouldContain(expectedSd);
        (await mount.GetTargetDeclinationAsync(ct)).ShouldBe(targetDec, 1 / 3600d);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(48.2d, 16.3d, ":St+48*12#", ":Sg343*42#")]
    [InlineData(-37.9d, 145.1668205d, ":St-37*54#", ":Sg214*50#")]
    public async Task GivenOnStepMountWhenSettingTheSiteItIsSentOnTheWire(double latitude, double longitude, string expectedSt, string expectedSg)
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        await using var mount = new FakeOnStepMountDriver(MakeDevice(10, 10), new FakeExternal(outputHelper).BuildServiceProvider());
        await mount.ConnectAsync(ct);

        // when
        await mount.SetSiteLatitudeAsync(latitude, ct);
        await mount.SetSiteLongitudeAsync(longitude, ct);

        // then
        var commands = mount.SerialDevice.ShouldNotBeNull().Commands;
        commands[^2].ShouldBe(expectedSt);
        commands[^1].ShouldBe(expectedSg);
        (await mount.GetSiteLatitudeAsync(ct)).ShouldBe(latitude, 1 / 120d);
        (await mount.GetSiteLongitudeAsync(ct)).ShouldBe(longitude, 1 / 120d);
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

        // when: fake transitions p → I → P over ~300ms fake time;
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

        // then: :hR# is immediate in the fake (no I→p transition needed)
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

    [Fact]
    public void OnStepDeviceExposesEditableTransportSettings()
    {
        // Serial-discovered device: WiFi host is empty, so Port field is visible and tcp is hidden.
        var serialDevice = new TianWen.Lib.Devices.OnStep.OnStepDevice(DeviceType.Mount, "id", "OnStep (Serial)", port: "COM3");

        var serialSettings = serialDevice.Settings;
        serialSettings.Length.ShouldBe(3); // port, host, tcp

        var portSetting = serialSettings[0];
        portSetting.Key.ShouldBe("port");
        portSetting.IsVisible.ShouldNotBeNull();
        portSetting.IsVisible!(serialDevice.DeviceUri).ShouldBe(true);

        var hostSetting = serialSettings[1];
        hostSetting.Key.ShouldBe("host");
        // Host is always visible.
        (hostSetting.IsVisible is null || hostSetting.IsVisible(serialDevice.DeviceUri)).ShouldBe(true);

        var tcpSetting = serialSettings[2];
        tcpSetting.Key.ShouldBe("tcp");
        tcpSetting.IsVisible.ShouldNotBeNull();
        tcpSetting.IsVisible!(serialDevice.DeviceUri).ShouldBe(false); // hidden on serial device

        // WiFi-discovered device: host is set, so tcp is visible and port field is hidden.
        var wifiDevice = new TianWen.Lib.Devices.OnStep.OnStepDevice(DeviceType.Mount, "id", "OnStep (WiFi)", host: "192.168.1.42", tcpPort: 9999);

        wifiDevice.Settings[0].IsVisible!(wifiDevice.DeviceUri).ShouldBe(false); // port hidden
        (wifiDevice.Settings[1].IsVisible is null || wifiDevice.Settings[1].IsVisible!(wifiDevice.DeviceUri)).ShouldBe(true); // host visible
        wifiDevice.Settings[2].IsVisible!(wifiDevice.DeviceUri).ShouldBe(true); // tcp visible
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
