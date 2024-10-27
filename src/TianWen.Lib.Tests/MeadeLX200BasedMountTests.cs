using NSubstitute;
using Shouldly;
using System;
using System.Text;
using System.Threading;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Meade;
using Xunit;
using Xunit.Abstractions;

namespace TianWen.Lib.Tests;

public class MeadeLX200BasedMountTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("ttyUSB1", true)]
    [InlineData("COM4", true)]
    [InlineData("COM3", false)]
    public void GivenMountWhenConnectingItOpensSerialPort(string deviceId, bool southPole)
    {
        // given
        var device = new MeadeDevice(DeviceType.Mount, deviceId, $"Meade Mount on {deviceId}");

        var fakeExternal = Substitute.For<FakeExternal>(outputHelper, null, null, null);
        fakeExternal
            .OpenSerialDevice(Arg.Is(deviceId), Arg.Any<int>(), Arg.Any<Encoding>(), Arg.Any<TimeSpan>())
            .Returns(x => new FakeMeadeLX200SerialDevice(true, southPole, x.ArgAt<Encoding>(2), fakeExternal.TimeProvider));

        // when
        var mount = new MeadeLX200BasedMount(device, fakeExternal)
        {
            Connected = true
        };

        // then
        mount.Connected.ShouldBe(true);
        mount.Alignment.ShouldBe(AlignmentMode.GermanPolar);
        mount.Tracking.ShouldBe(false);
    }

    [Theory]
    [InlineData("ttyUSB1", true)]
    [InlineData("COM4", true)]
    [InlineData("COM3", false)]
    public void GivenMountWhenConnectingAndDisconnectingThenSerialPortIsClosed(string deviceId, bool southPole)
    {
        // given
        var device = new MeadeDevice(DeviceType.Mount, deviceId, $"Meade Mount on {deviceId}");

        FakeMeadeLX200SerialDevice? serialDevice = null;
        var fakeExternal = Substitute.For<FakeExternal>(outputHelper, null, null, null);
        fakeExternal
            .OpenSerialDevice(Arg.Is(deviceId), Arg.Any<int>(), Arg.Any<Encoding>(), Arg.Any<TimeSpan>())
            .Returns(x => serialDevice ??= new FakeMeadeLX200SerialDevice(true, southPole, x.ArgAt<Encoding>(2), fakeExternal.TimeProvider));

        int receivedConnect = 0;
        int receivedDisconnect = 0;
        var mount = new MeadeLX200BasedMount(device, fakeExternal);
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
        mount.Connected = true;

        // then
        mount.Connected.ShouldBe(true);
        receivedConnect.ShouldBe(1);
        receivedDisconnect.ShouldBe(0);
        serialDevice.ShouldNotBeNull();
        serialDevice.IsOpen.ShouldBeTrue();
        Should.NotThrow(() => mount.SiderealTime);

        // after
        mount.Connected = false;

        // then
        mount.Connected.ShouldBe(false);
        receivedConnect.ShouldBe(1);
        receivedDisconnect.ShouldBe(1);
        serialDevice.IsOpen.ShouldBeFalse();

        Should.Throw(() => mount.SiderealTime, typeof(InvalidOperationException));
    }
}