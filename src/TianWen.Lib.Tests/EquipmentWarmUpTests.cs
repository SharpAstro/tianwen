using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The out-of-session warm-up ramp (<see cref="EquipmentActions.WarmAndDisconnectAsync"/>) waits on the
/// injected <see cref="ITimeProvider"/>, never on the wall clock. It used to call <c>Task.Delay</c>, so
/// under a fake clock every 2 degree step cost a real 30 s and the ramp could not be tested at all, and
/// on the real clock it ignored the <c>TIANWEN_NOW</c> anchor every other wait honours.
/// </summary>
public class EquipmentWarmUpTests(ITestOutputHelper output)
{
    private static readonly Uri CameraUri = new("Camera://FakeDevice/cam1");

    [Fact(Timeout = 30_000)]
    public async Task TheRampWaitsOnTheInjectedClockAndThenDisconnects()
    {
        // A sensor that settles on whatever setpoint it is given, so each step is one read and one sleep.
        var ccd = -10.0;
        var camera = Substitute.For<ICameraDriver>();
        camera.CanGetCoolerOn.Returns(true);
        camera.CanGetHeatsinkTemperature.Returns(true);
        camera.GetCoolerOnAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        camera.GetHeatSinkTemperatureAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(0.0));
        camera.GetCCDTemperatureAsync(Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ccd));
        camera.SetSetCCDTemperatureAsync(Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(call => { ccd = call.ArgAt<double>(0); return ValueTask.CompletedTask; });

        var hub = Substitute.For<IDeviceHub>();
        hub.TryGetConnectedDriver<ICameraDriver>(CameraUri, out Arg.Any<ICameraDriver?>())
            .Returns(call => { call[1] = camera; return true; });

        var sleeps = new List<TimeSpan>();
        var clock = Substitute.For<ITimeProvider>();
        clock.SleepAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => { sleeps.Add(call.ArgAt<TimeSpan>(0)); return ValueTask.CompletedTask; });

        await EquipmentActions.WarmAndDisconnectAsync(hub, CameraUri, clock, FakeExternal.CreateLogger(output),
            force: false, TestContext.Current.CancellationToken);

        // -10 to within 1 degree of a 0 degree heat sink in 2 degree steps: -8, -6, -4, -2, 0 is five steps,
        // then the 2 s settle after the cooler goes off.
        sleeps.ShouldBe([.. Enumerable.Repeat(TimeSpan.FromSeconds(30), 5), TimeSpan.FromSeconds(2)]);
        await camera.Received(1).SetCoolerOnAsync(false, Arg.Any<CancellationToken>());
        await hub.Received(1).DisconnectAsync(CameraUri, false, Arg.Any<CancellationToken>());
    }
}
