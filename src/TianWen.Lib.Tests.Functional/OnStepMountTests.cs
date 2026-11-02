using Shouldly;
using System;
using System.Collections.Specialized;
using System.Globalization;
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

    private static FakeDevice MakeDeviceWithHome(string homeOutcome)
        => new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            ["latitude"] = Convert.ToString(48.2),
            ["longitude"] = Convert.ToString(16.3),
            ["port"] = "OnStep",
            ["homeOutcome"] = homeOutcome
        });

    // The connect reads :GXE6# then :GX92# and works the mount's own top rate out of them, where every test before ran on the
    // 48x fallback because the fake answered neither.
    [Fact(Timeout = 60_000)]
    public async Task GivenOnStepWhenConnectingTheTopMoveRateIsTheMountsOwnNotTheFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var mount = new FakeOnStepMountDriver(MakeDevice(48.2, 16.3), new FakeExternal(outputHelper).BuildServiceProvider());

        await mount.ConnectAsync(ct);

        var commands = mount.SerialDevice.ShouldNotBeNull().Commands.ToList();
        var gxe6 = commands.IndexOf(":GXE6#");
        gxe6.ShouldBeGreaterThanOrEqualTo(0);
        commands.IndexOf(":GX92#").ShouldBeGreaterThan(gxe6);
        var top = mount.AxisRates(TelescopeAxis.Primary).ShouldHaveSingleItem().Maximum;
        top.ShouldBe(1.7578, 1e-3, "the fake's 11378 steps a degree and 50 us a step: 422x sidereal");
        top.ShouldNotBe(MountJog.RateDegPerSec(OnStepMountDriver<FakeDevice>.FallbackFastestMoveSidereal), "not the 48x fallback");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenOnStepWhenSentHomeItPollsUntilTheHomeFlagAndTheCommandAnswersNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper();
        await using var mount = new FakeOnStepMountDriver(MakeDevice(48.2, 16.3), new FakeExternal(outputHelper, timeProvider).BuildServiceProvider());
        await mount.ConnectAsync(ct);
        mount.CanFindHome.ShouldBeTrue();
        (await mount.AtHomeAsync(ct)).ShouldBeFalse("not sent home yet");

        await mount.FindHomeAsync(ct);

        var commands = mount.SerialDevice.ShouldNotBeNull().Commands.ToList();
        var home = commands.IndexOf(":hC#");
        home.ShouldBeGreaterThanOrEqualTo(0);
        commands.Skip(home + 1).Count(c => c is ":GU#").ShouldBeGreaterThanOrEqualTo(2, "it was still on its way at the first poll");
        (await mount.AtParkAsync(ct)).ShouldBeFalse();
        (await mount.AtHomeAsync(ct)).ShouldBeTrue("the H flag of :GU#, which the base's plain false never read");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenOnStepWhenHomeFailsTheFailureFlagEndsItWithAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper();
        await using var mount = new FakeOnStepMountDriver(MakeDeviceWithHome("fail"), new FakeExternal(outputHelper, timeProvider).BuildServiceProvider());
        await mount.ConnectAsync(ct);

        var failed = await Should.ThrowAsync<InvalidOperationException>(() => mount.FindHomeAsync(ct).AsTask());

        failed.Message.ShouldContain("failure going home");
    }

    // 240 s of FAKE time: the session's time pump rules (the fake clock's SleepAsync advances it), no wall clock in the test.
    [Fact(Timeout = 60_000)]
    public async Task GivenOnStepThatNeverArrivesHomeItGivesUpAfterTwoHundredAndFortySeconds()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper();
        await using var mount = new FakeOnStepMountDriver(MakeDeviceWithHome("never"), new FakeExternal(outputHelper, timeProvider).BuildServiceProvider());
        await mount.ConnectAsync(ct);
        var started = timeProvider.GetUtcNow();

        var timedOut = await Should.ThrowAsync<TimeoutException>(() => mount.FindHomeAsync(ct).AsTask());

        timedOut.Message.ShouldContain("240 s");
        (timeProvider.GetUtcNow() - started).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(240));
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenAParkedOnStepSendingItHomeIsRefusedAndNothingIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper();
        await using var mount = new FakeOnStepMountDriver(MakeDevice(48.2, 16.3), new FakeExternal(outputHelper, timeProvider).BuildServiceProvider());
        await mount.ConnectAsync(ct);
        await mount.ParkAsync(ct);

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => mount.FindHomeAsync(ct).AsTask());

        refused.Message.ShouldContain("is parked");
        mount.SerialDevice.ShouldNotBeNull().Commands.ShouldNotContain(":hC#");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenAJogBeforeAndAfterASouthernSitePushTheRaDirectionFollowsTheNewHemisphere()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var mount = new FakeOnStepMountDriver(MakeDevice(48.2, 16.3), new FakeExternal(outputHelper).BuildServiceProvider());
        await mount.ConnectAsync(ct);
        var fake = mount.SerialDevice.ShouldBeOfType<FakeOnStepSerialDevice>();

        await mount.MoveAxisAsync(TelescopeAxis.Primary, 0.5, ct);
        fake.Moving.ToArray().ShouldBe(new[] { 'w' }, "north: a positive RA rate is west");
        await mount.MoveAxisAsync(TelescopeAxis.Primary, 0, ct);

        await mount.SetSiteLatitudeAsync(-37.9, ct);
        await mount.MoveAxisAsync(TelescopeAxis.Primary, 0.5, ct);

        fake.Moving.ToArray().ShouldBe(new[] { 'e' }, "the push made the site southern, and the cached hemisphere must not outlive it");
        await mount.MoveAxisAsync(TelescopeAxis.Primary, 0, ct);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("2026-11-02T01:54:55Z", ":SG-11#", -11.0)] // Melbourne's summer, the day the TeeSeek read +10 h at 12:54
    [InlineData("2026-07-01T10:00:00Z", ":SG-10#", -10.0)] // its winter
    public async Task GivenOnStepWhenConnectingItsClockIsSetInTheSitesOwnSeason(string utc, string expectedSg, double expectedCorrectionHours)
    {
        // The TeeSeek's OnStep sat 11 hours off and refused a goto to Venus, high in the west, as below the horizon:
        // nothing set its clock on connect, only a session's start did. Then it kept the +10 h it was given through
        // Melbourne's summer, its local clock an hour behind the wall's, since its own offset was left as it was.
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture));
        await using var mount = new FakeOnStepMountDriver(MakeDevice(-37.8743502, 145.1668205), new FakeExternal(outputHelper, timeProvider).BuildServiceProvider());

        await mount.ConnectAsync(ct);

        var commands = mount.SerialDevice.ShouldNotBeNull().Commands.ToList();
        var sg = commands.IndexOf(expectedSg);
        var sc = commands.FindIndex(c => c.StartsWith(":SC", StringComparison.Ordinal));
        var sl = commands.FindIndex(c => c.StartsWith(":SL", StringComparison.Ordinal));
        sg.ShouldBeGreaterThanOrEqualTo(0, "the site's offset in its season (the fake starts at UTC)");
        sc.ShouldBeGreaterThan(sg, "the offset first, so the local time sent after it means the right UTC");
        sl.ShouldBeGreaterThan(sc, "date, then time, as the ASCOM OnStep driver sets them");
        mount.TimeIsSetByUs.ShouldBeTrue();
        var fake = mount.SerialDevice.ShouldBeOfType<FakeOnStepSerialDevice>();
        fake.UtcOffset.ShouldBe(TimeSpan.FromHours(expectedCorrectionHours));
        var mountUtc = (await mount.TryGetUTCDateFromMountAsync(ct)).ShouldNotBeNull();
        (mountUtc - timeProvider.GetUtcNow().UtcDateTime).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(2));

        // What was SENT, not what the fake reads back: the fake's own clock is the time provider's, so the read-back is true
        // whatever :SC and :SL carried. The offset is non-zero, so a clock sent as UTC, an error of hours, cannot pass.
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var sentLocal = fake.ClockDateSent.ShouldNotBeNull().Date + fake.ClockTimeSent.ShouldNotBeNull();
        (sentLocal - (utcNow - fake.UtcOffset)).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(2), "the local time the offset implies, not UTC");

        // A mount already on the site's offset is not sent it again.
        var sent = fake.Commands.Length;
        await mount.SetUTCDateAsync(timeProvider.GetUtcNow().UtcDateTime, ct);
        fake.Commands.Skip(sent).ShouldNotContain(c => c.StartsWith(":SG", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-11.0, "-11")]
    [InlineData(10.0, "+10")]
    [InlineData(0.0, "+00")]
    [InlineData(-5.75, "-05:45")]
    [InlineData(3.5, "+03:30")]
    public void OnStepsUtcOffsetArgumentIsSignedWithMinutesOnlyWhenNotWhole(double hours, string expected)
        => OnStepMountDriver<OnStepDevice>.FormatUtcCorrection(TimeSpan.FromHours(hours)).ShouldBe(expected);

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

    [Fact(Timeout = 60_000)]
    public async Task GivenOnStepItReportsNoRawAxisPositionAndNoThirdAxis()
    {
        // OnStep 4.x has no raw-count command (:GX44# / :GX45# do not exist; the bench TeeSeek times out on them), so
        // the guider's periodic-error path sees no position rather than a five-second timeout on every read. The axis
        // ANGLE is :GX42# / :GX43#, and OnStep has two axes.
        var ct = TestContext.Current.CancellationToken;
        await using var driver = new FakeOnStepMountDriver(MakeDevice(48.2, 16.3), new FakeExternal(outputHelper).BuildServiceProvider());
        await driver.ConnectAsync(ct);
        IMountDriver mount = driver;

        (await mount.GetAxisPositionAsync(TelescopeAxis.Primary, ct)).ShouldBeNull();
        (await mount.GetAxisAngleAsync(TelescopeAxis.Tertiary, ct)).ShouldBeNull("OnStep has only two mechanical axes");
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

    // The bench TeeSeek at home read RA/Dec at the pole and HA 11.975 h through 55 minutes of tracking, while its head
    // turned: the tube lies along the polar axis there. OnStep's :GX42# (axis 1) followed the head, 90 at home and up
    // 15 degrees an hour, and that is the axis angle the meridian limit reads.
    [Theory(Timeout = 60_000)]
    [InlineData(-37.8743502, 145.1668205)]
    [InlineData(48.2, 16.3)]
    public async Task GivenOnStepAtHomeWhenTrackingThePrimaryAxisAngleFollowsTheHead(double siteLat, double siteLong)
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper();
        await using var mount = new FakeOnStepMountDriver(MakeDevice(siteLat, siteLong), new FakeExternal(outputHelper, timeProvider).BuildServiceProvider());
        await mount.ConnectAsync(ct);

        (await mount.GetAxisAngleAsync(TelescopeAxis.Primary, ct)).ShouldNotBeNull().ShouldBe(0.0, 0.01, "home is counterweight down");
        (await mount.GetAxisAngleAsync(TelescopeAxis.Seconary, ct)).ShouldNotBeNull().ShouldBe(0.0, 0.01, "and the Dec axis on the pole");

        await mount.SetTrackingAsync(true, ct);
        for (var minute = 0; minute < 60; minute++)
        {
            await timeProvider.SleepAsync(TimeSpan.FromMinutes(1), ct);
        }

        (await mount.GetAxisAngleAsync(TelescopeAxis.Primary, ct)).ShouldNotBeNull().ShouldBe(15.0, 0.5, "an hour of tracking turns the RA axis 15 degrees, in the tracking direction");
    }

    // The SkyWatcher driver's numbers for the same poses (FakeSkywatcherMountDriverTests): one mechanical angle whichever
    // driver reads it. A sync keeps the fake's pier side, which at home is OnStep's East.
    [Theory(Timeout = 60_000)]
    [InlineData(48.2, 16.3, 3.0, 45.0, -45.0, 45.0)]
    [InlineData(-37.8743502, 145.1668205, 2.0, -45.0, -60.0, -45.0)]
    public async Task GivenOnStepSyncedOffHomeTheAxisAnglesAreTheSkywatcherDriversForThatPose(double siteLat, double siteLong, double hourAngle, double dec, double expectedPrimary, double expectedSecondary)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var mount = new FakeOnStepMountDriver(MakeDevice(siteLat, siteLong), new FakeExternal(outputHelper).BuildServiceProvider());
        await mount.ConnectAsync(ct);

        var lst = await mount.GetSiderealTimeAsync(ct);
        await mount.SyncRaDecAsync(((lst - hourAngle) % 24.0 + 24.0) % 24.0, dec, ct);

        (await mount.GetAxisAngleAsync(TelescopeAxis.Primary, ct)).ShouldNotBeNull().ShouldBe(expectedPrimary, 0.05);
        (await mount.GetAxisAngleAsync(TelescopeAxis.Seconary, ct)).ShouldNotBeNull().ShouldBe(expectedSecondary, 0.01);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenOnStepTheAxisAngleIsReadThroughTheInterface()
    {
        // A public method on a class that does not list IMountDriver is never called through it: OnStep's former
        // GetAxisPositionAsync was dead that way, so this goes through the interface, as the limit watcher does.
        var ct = TestContext.Current.CancellationToken;
        await using var driver = new FakeOnStepMountDriver(MakeDevice(-37.8743502, 145.1668205), new FakeExternal(outputHelper).BuildServiceProvider());
        await driver.ConnectAsync(ct);
        IMountDriver mount = driver;

        (await mount.GetAxisAngleAsync(TelescopeAxis.Primary, ct)).ShouldNotBeNull();
    }
}
