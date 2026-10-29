using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The one warm-up (#1840): an ambient target with no heat sink aims for the +25 C fallback, and the ramp ends on the
/// cooler's power, so an unreachable target costs a few steps and not the safety cap.
/// </summary>
[Collection("Session")]
public class CameraCoolingRampTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    [Fact(Timeout = 60_000)]
    public async Task AHeatsinklessCooledCameraIsWarmedTowardTheFallbackTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        ctx.Camera.MakeHeatsinkless(CameraReading.WarmUpFallbackTargetC, sensorCannotPassAmbient: false);

        await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(-10, SetpointTempKind.Normal), Budget, 80, SetupointDirection.Down, ct);
        var cooled = await ctx.Camera.GetCCDTemperatureAsync(ct);
        cooled.ShouldBeLessThan(0);

        var reached = await ctx.Session.CoolCamerasToAmbientAsync(Budget);

        reached.ShouldBeTrue();
        var warmed = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Cooled {cooled:F1} C, warmed {warmed:F1} C");
        warmed.ShouldBe(CameraReading.WarmUpFallbackTargetC, 1.0);
    }

    [Fact(Timeout = 60_000)]
    public async Task AWarmUpEndsOnCoolerPowerWhenTheTargetCannotBeReached()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        // A cold night: the room is 12 C, so the sensor stalls there at 0 % power, well short of +25 C.
        ctx.Camera.MakeHeatsinkless(12, sensorCannotPassAmbient: true);

        await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(-10, SetpointTempKind.Normal), Budget, 80, SetupointDirection.Down, ct);

        var start = ctx.TimeProvider.GetUtcNow();
        var reached = await ctx.Session.CoolCamerasToAmbientAsync(Budget);
        var slept = ctx.TimeProvider.GetUtcNow() - start;

        var sensor = await ctx.Camera.GetCCDTemperatureAsync(ct);
        var power = await ctx.Camera.GetCoolerPowerAsync(ct);
        output.WriteLine($"Sensor {sensor:F1} C, power {power:F1} %, {slept.TotalSeconds:F0} s slept");
        sensor.ShouldBe(12.0, 0.5);
        power.ShouldBeLessThanOrEqualTo(CameraCoolingRamp.WarmUpPowerThreshold);
        reached.ShouldBeFalse();
        // The cap is twice the planned 30 steps (900 s); the walk up is about 22 steps, then a few at no power.
        slept.ShouldBeLessThan(TimeSpan.FromSeconds(8 * 15) + TimeSpan.FromSeconds(22 * 15));
    }
}
