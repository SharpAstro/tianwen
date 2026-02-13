using Shouldly;
using System;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

public class MeadeLX200BasedMountTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(-37.8743502, 145.1668205)]
    [InlineData(25.28022, 110.29639)]
    public async Task GivenMountWhenConnectingItOpensSerialPort(double siteLat, double siteLong)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection { ["latitude"] = Convert.ToString(siteLat), ["longitude"] = Convert.ToString(siteLong) });
        var fakeExternal = new FakeExternal(outputHelper);
        await using var mount = new FakeMeadeLX200ProtocolMountDriver(device, fakeExternal);

        // when
        await mount.ConnectAsync(cancellationToken);

        // then
        mount.Connected.ShouldBe(true);
        (await mount.GetAlignmentAsync(cancellationToken)).ShouldBe(AlignmentMode.GermanPolar);
        (await mount.IsTrackingAsync(cancellationToken)).ShouldBe(false);
    }

    [Theory]
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

        var mount = new FakeMeadeLX200ProtocolMountDriver(device, fakeExternal);
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

    [Theory]
    [InlineData(-37.8743502, 145.1668205, 11.11d, -45.125d, null)]
    [InlineData(25.28022, 110.29639, 15.58d, 0.15d, null)]
    [InlineData(51.38333333d, 8.08333333d, 8.85d, 11.8d, "2024-10-29T06:58:00Z")]
    public async Task GivenTargetWhenSlewingItSlewsToTarget(double siteLat, double siteLong, double targetRa, double targetDec, string? utc)
    {
        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection { ["latitude"] = Convert.ToString(siteLat), ["longitude"] = Convert.ToString(siteLong) });
        var fakeExternal = new FakeExternal(outputHelper, null, utc is not null ? DateTimeOffset.Parse(utc) : null, null);

        await using var mount = new FakeMeadeLX200ProtocolMountDriver(device, fakeExternal);

        var timeStamp = fakeExternal.TimeProvider.GetTimestamp();

        // when
        await mount.ConnectAsync(cancellationToken);
        await mount.SetTrackingAsync(true, cancellationToken);
        await mount.BeginSlewRaDecAsync(targetRa, targetDec, cancellationToken);
        (await mount.IsSlewingAsync(cancellationToken)).ShouldBe(true);
        while (await mount.IsSlewingAsync(cancellationToken))
        {
            // this will advance the fake timer and not actually sleep
            fakeExternal.Sleep(TimeSpan.FromSeconds(1));
        }

        // then
        var timePassed = fakeExternal.TimeProvider.GetElapsedTime(timeStamp);
        timePassed.ShouldBeGreaterThan(TimeSpan.FromSeconds(2));
        (await mount.IsSlewingAsync(cancellationToken)).ShouldBe(false);
        (await mount.IsTrackingAsync(cancellationToken)).ShouldBe(true);
        mount.Connected.ShouldBe(true);
        (await mount.GetAlignmentAsync(cancellationToken)).ShouldBe(AlignmentMode.GermanPolar);
    }
}