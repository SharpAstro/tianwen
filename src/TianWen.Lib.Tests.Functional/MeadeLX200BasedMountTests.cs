using Shouldly;
using System;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class MeadeLX200BasedMountTests(ITestOutputHelper outputHelper)
{
    [Theory(Timeout = 60_000)]
    [InlineData(-37.8743502, 145.1668205)]
    [InlineData(25.28022, 110.29639)]
    public async Task GivenMountWhenConnectingItOpensSerialPort(double siteLat, double siteLong)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection { ["latitude"] = Convert.ToString(siteLat), ["longitude"] = Convert.ToString(siteLong) });
        var fakeExternal = new FakeExternal(outputHelper);
        await using var mount = new FakeMeadeLX200ProtocolMountDriver(device, fakeExternal.BuildServiceProvider());

        // when
        await mount.ConnectAsync(cancellationToken);

        // then
        mount.Connected.ShouldBe(true);
        (await mount.GetAlignmentAsync(cancellationToken)).ShouldBe(AlignmentMode.GermanPolar);
        (await mount.IsTrackingAsync(cancellationToken)).ShouldBe(false);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(-37.8743502, 145.1668205)]
    [InlineData(25.28022, 110.29639)]
    public async Task GivenMountWhenConnectingAndDisconnectingThenSerialPortIsClosed(double siteLat, double siteLong)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection { ["latitude"] = Convert.ToString(siteLat), ["longitude"] = Convert.ToString(siteLong) });
        var fakeExternal = new FakeExternal(outputHelper);

        int receivedConnect = 0;
        int receivedDisconnect = 0;

        var mount = new FakeMeadeLX200ProtocolMountDriver(device, fakeExternal.BuildServiceProvider());
        mount.DeviceConnectedEvent += (_, e) =>
        {
            if (e.Connected)
            {
                Interlocked.Increment(ref receivedConnect);
            }
            else
            {
                Interlocked.Increment(ref receivedDisconnect);
            }
        };

        // when
        await mount.ConnectAsync(cancellationToken);

        // then
        mount.Connected.ShouldBe(true);
        receivedConnect.ShouldBe(1);
        receivedDisconnect.ShouldBe(0);
        await Should.NotThrowAsync(async () => await mount.GetSiderealTimeAsync(cancellationToken));

        // after
        await mount.DisconnectAsync(cancellationToken);

        // then
        mount.Connected.ShouldBe(false);
        receivedConnect.ShouldBe(1);
        receivedDisconnect.ShouldBe(1);

        await Should.ThrowAsync(async () => await mount.GetSiderealTimeAsync(cancellationToken), typeof(InvalidOperationException));
    }

    [Theory(Timeout = 60_000)]
    [InlineData(-37.8743502, 145.1668205, 11.11d, -45.125d, null, ":Sd-45*07:30#")]
    [InlineData(25.28022, 110.29639, 15.58d, 0.15d, null, ":Sd00*09:00#")]
    [InlineData(51.38333333d, 8.08333333d, 8.85d, 11.8d, "2024-10-29T06:58:00Z", ":Sd11*48:00#")]
    [InlineData(-37.8743502, 145.1668205, 10.75d, -59.7d, null, ":Sd-59*42:00#")]
    public async Task GivenTargetWhenSlewingItSlewsToTarget(double siteLat, double siteLong, double targetRa, double targetDec, string? utc, string expectedSd)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection { ["latitude"] = Convert.ToString(siteLat), ["longitude"] = Convert.ToString(siteLong) });
        var timeProvider = new FakeTimeProviderWrapper(utc is not null ? DateTimeOffset.Parse(utc) : null);
        var fakeExternal = new FakeExternal(outputHelper, timeProvider);

        await using var mount = new FakeMeadeLX200ProtocolMountDriver(device, fakeExternal.BuildServiceProvider());

        var timeStamp = timeProvider.GetTimestamp();

        // when
        await mount.ConnectAsync(cancellationToken);
        await mount.SetTrackingAsync(true, cancellationToken);
        await mount.BeginSlewRaDecAsync(targetRa, targetDec, cancellationToken);
        (await mount.IsSlewingAsync(cancellationToken)).ShouldBe(true);
        while (await mount.IsSlewingAsync(cancellationToken))
        {
            // this will advance the fake timer and not actually sleep
            await timeProvider.SleepAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }

        // then
        var timePassed = ((ITimeProvider)timeProvider).GetElapsedTime(timeStamp);
        timePassed.ShouldBeGreaterThan(TimeSpan.FromSeconds(2));
        (await mount.IsSlewingAsync(cancellationToken)).ShouldBe(false);
        (await mount.IsTrackingAsync(cancellationToken)).ShouldBe(true);
        mount.Connected.ShouldBe(true);
        (await mount.GetAlignmentAsync(cancellationToken)).ShouldBe(AlignmentMode.GermanPolar);

        // #837: the declination that went out is the one asked for, as the FAKE parsed it (:Gd# answers the
        // fake's target) and where the mount ended up, not only a slew that finished somewhere.
        mount.SerialDevice.ShouldNotBeNull().Commands.ShouldContain(expectedSd);
        (await mount.GetTargetDeclinationAsync(cancellationToken)).ShouldBe(targetDec, OneArcSecond);
        (await mount.GetDeclinationAsync(cancellationToken)).ShouldBe(targetDec, OneArcSecond);
    }

    private const double OneArcSecond = 1 / 3600d;
    private const double HalfAnArcMinute = 1 / 120d;

    private async Task<FakeMeadeLX200ProtocolMountDriver> ConnectedMountAsync(double siteLat, double siteLong, CancellationToken cancellationToken)
    {
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection { ["latitude"] = Convert.ToString(siteLat), ["longitude"] = Convert.ToString(siteLong) });
        var mount = new FakeMeadeLX200ProtocolMountDriver(device, new FakeExternal(outputHelper).BuildServiceProvider());
        await mount.ConnectAsync(cancellationToken);
        return mount;
    }

    [Theory(Timeout = 60_000)]
    [InlineData(48.2d, ":St48*12#")]
    [InlineData(-37.9d, ":St-37*54#")]
    [InlineData(-0.0001d, ":St00*00#")]
    [InlineData(89.9999d, ":St90*00#")]
    public async Task GivenSiteLatitudeWhenSettingItIsSentOnTheWire(double latitude, string expected)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var mount = await ConnectedMountAsync(10, 10, cancellationToken);

        // when
        await mount.SetSiteLatitudeAsync(latitude, cancellationToken);

        // then
        var fake = mount.SerialDevice.ShouldNotBeNull();
        fake.Commands[^1].ShouldBe(expected);
        fake.SiteLatitude.ShouldBe(latitude, HalfAnArcMinute);
        (await mount.GetSiteLatitudeAsync(cancellationToken)).ShouldBe(latitude, HalfAnArcMinute);
    }

    // :Sg is degrees WEST, 0 to 360: 16 deg 18' east is 343 deg 42' west.
    [Theory(Timeout = 60_000)]
    [InlineData(16.3d, ":Sg343*42#")]
    [InlineData(-100.5d, ":Sg100*30#")]
    [InlineData(145.1668205d, ":Sg214*50#")]
    [InlineData(-0.0001d, ":Sg000*00#")]
    [InlineData(0.0001d, ":Sg000*00#")]
    [InlineData(180d, ":Sg180*00#")]
    public async Task GivenSiteLongitudeWhenSettingItIsSentOnTheWireAndReadsBack(double longitude, string expected)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var mount = await ConnectedMountAsync(10, 10, cancellationToken);

        // when
        await mount.SetSiteLongitudeAsync(longitude, cancellationToken);

        // then
        var fake = mount.SerialDevice.ShouldNotBeNull();
        fake.Commands[^1].ShouldBe(expected);
        var readBack = await mount.GetSiteLongitudeAsync(cancellationToken);
        // 180 east and 180 west are one meridian
        Math.Abs(Math.IEEERemainder(readBack - longitude, 360)).ShouldBeLessThanOrEqualTo(HalfAnArcMinute);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenLunarTrackingWhenSetItReadsBackAsLunar()
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var mount = await ConnectedMountAsync(48.2, 16.3, cancellationToken);

        // when
        await mount.SetTrackingSpeedAsync(TrackingSpeed.Lunar, cancellationToken);

        // then
        (await mount.GetTrackingSpeedAsync(cancellationToken)).ShouldBe(TrackingSpeed.Lunar);

        await mount.SetTrackingSpeedAsync(TrackingSpeed.Sidereal, cancellationToken);
        (await mount.GetTrackingSpeedAsync(cancellationToken)).ShouldBe(TrackingSpeed.Sidereal);
    }
}