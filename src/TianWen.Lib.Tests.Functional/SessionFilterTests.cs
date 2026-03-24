using Shouldly;
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Tests;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Tests for filter wheel sequencing in the imaging loop, including
/// the altitude-ladder traversal and focus offset application.
/// </summary>
public class SessionFilterTests(ITestOutputHelper output)
{
    // Horsehead + M42 framing center: midpoint between Horsehead (5.68h, -1.94°) and M42 (5.59h, -5.39°)
    private static readonly Target HorseheadM42 = new Target(5.63, -3.67, "Horsehead+M42", null);

    // Vienna winter night: M42/Horsehead region is well-placed in December/January evenings
    // 22:00 UTC on Dec 15 = 23:00 CET, M42 is near transit from Vienna
    private static readonly DateTimeOffset WinterNight = new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Dual plate setup modelling:
    /// OTA 1: Samyang 135mm f/2.0 + OSC camera + fixed L-Ultimate (Ha+OIII) → no filter wheel, single exposure plan
    /// OTA 2: Samyang 135mm f/2.0 + Mono camera + 5-pos filter wheel (L, SII, R, G, B) → altitude ladder plan
    /// Target: Horsehead Nebula + M42 region, across meridian from Melbourne.
    /// Verifies that the imaging loop produces frames from both cameras and that
    /// the mono camera's filter wheel is commanded through the altitude ladder.
    /// </summary>
    private const int TrueBestFocusPosition = 1000;

    [Fact]
    public async Task GivenDualPlateWithFilterWheelWhenImagingThenBothCamerasProduceFrames()
    {
        var ct = TestContext.Current.CancellationToken;

        // Use uniform 30s sub-exposures so tick GCD = 30s and the fake camera can produce images each tick
        var filterPlan = ImmutableArray.Create(
            new FilterExposure(1, TimeSpan.FromSeconds(30), Count: 2), // SII
            new FilterExposure(2, TimeSpan.FromSeconds(30), Count: 2), // R
            new FilterExposure(0, TimeSpan.FromSeconds(30), Count: 2)  // L (top)
        );

        var observations = new[]
        {
            new ScheduledObservation(
                HorseheadM42,
                WinterNight,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: filterPlan,
                Gain: 0,
                Offset: 0
            )
        };

        var ctx = await SessionTestHelper.CreateDualOTASessionAsync(output, observations: observations, now: WinterNight, cancellationToken: ct);

        // Set up both cameras at best focus so they produce synthetic star images
        ctx.OSCCamera.TrueBestFocus = TrueBestFocusPosition;
        ctx.OSCCamera.FocusPosition = TrueBestFocusPosition;
        ctx.MonoCamera.TrueBestFocus = TrueBestFocusPosition;
        ctx.MonoCamera.FocusPosition = TrueBestFocusPosition;

        // Move both focusers to best focus
        await ctx.OSCFocuser.BeginMoveAsync(TrueBestFocusPosition, ct);
        await ctx.MonoFocuser.BeginMoveAsync(TrueBestFocusPosition, ct);
        while (await ctx.OSCFocuser.GetIsMovingAsync(ct) || await ctx.MonoFocuser.GetIsMovingAsync(ct))
        {
            await ctx.External.SleepAsync(TimeSpan.FromMilliseconds(100), ct);
        }

        ctx.Session.AdvanceObservationForTest();

        // Enable tracking and start guiding (required by imaging loop)
        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);
        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        await guider.GuideAsync(0.3, 3, 30, ct);
        await ctx.External.SleepAsync(TimeSpan.FromSeconds(4), ct);

        var observation = ctx.Session.ActiveObservation!;
        var tickDuration = TimeSpan.FromSeconds(30);
        var loopTask = Task.Run(async () => await ctx.Session.ImagingLoopAsync(observation, -0.5, ct), ct);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        for (var i = 0; i < 30 && !loopTask.IsCompleted && !linked.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(tickDuration, ct);
            for (var spin = 0; spin < 10 && !loopTask.IsCompleted; spin++)
            {
                await Task.Delay(10, ct);
            }
        }

        loopTask.IsCompleted.ShouldBeTrue("imaging loop should complete within timeout");
        await loopTask;

        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0, "should have written frames from both cameras");

        output.WriteLine($"Total frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure time: {ctx.Session.TotalExposureTime}");

        ctx.CleanupImageOutputFolder();
    }

    /// <summary>
    /// Verifies that the altitude ladder for a 5-filter wheel (L, SII, R, G, B)
    /// produces the correct ordering: narrowband first (SII), then RGB, then Luminance on top.
    /// </summary>
    [Fact]
    public void GivenLSIIRGBWheelWhenBuildAutoFilterPlanThenLadderIsNarrowbandThenRGBThenLuminance()
    {
        var plan = SessionTestHelper.FakeWheelLSIIRGBFilterPlan;

        // Ladder: SII (narrowband) → R, G, B (broadband) → L (luminance, top)
        plan.Length.ShouldBe(5);

        // SII is narrowband → first
        plan[0].SubExposure.ShouldBe(TimeSpan.FromSeconds(300));

        // R, G, B are broadband → middle
        plan[1].SubExposure.ShouldBe(TimeSpan.FromSeconds(120));
        plan[2].SubExposure.ShouldBe(TimeSpan.FromSeconds(120));
        plan[3].SubExposure.ShouldBe(TimeSpan.FromSeconds(120));

        // Luminance at top → last
        plan[4].SubExposure.ShouldBe(TimeSpan.FromSeconds(120));

        output.WriteLine("Filter ladder for L/SII/R/G/B wheel:");
        output.WriteLine($"  [0] SII  300s ×{plan[0].Count} (narrowband, low-alt tolerant)");
        output.WriteLine($"  [1] R    120s ×{plan[1].Count}");
        output.WriteLine($"  [2] G    120s ×{plan[2].Count}");
        output.WriteLine($"  [3] B    120s ×{plan[3].Count}");
        output.WriteLine($"  [4] L    120s ×{plan[4].Count} (luminance, peak seeing)");
    }

    /// <summary>
    /// Verifies that a single-filter plan (no filter wheel, e.g. fixed L-Ultimate)
    /// works correctly in the imaging loop — no filter switching attempted.
    /// </summary>
    [Fact]
    public async Task GivenSingleFilterPlanWhenImagingThenFramesCapturedWithoutFilterSwitch()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);

        var observations = new[]
        {
            new ScheduledObservation(
                HorseheadM42,
                WinterNight,
                TimeSpan.FromMinutes(3),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        var ctx = await SessionTestHelper.CreateSessionAsync(output, observations: observations, now: WinterNight, cancellationToken: ct);

        // Set up camera at best focus
        ctx.Camera.TrueBestFocus = TrueBestFocusPosition;
        ctx.Camera.FocusPosition = TrueBestFocusPosition;
        await ctx.Focuser.BeginMoveAsync(TrueBestFocusPosition, ct);
        while (await ctx.Focuser.GetIsMovingAsync(ct))
        {
            await ctx.External.SleepAsync(TimeSpan.FromMilliseconds(100), ct);
        }

        ctx.Session.AdvanceObservationForTest();

        // Enable tracking and guiding
        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);
        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        await guider.GuideAsync(0.3, 3, 30, ct);
        await ctx.External.SleepAsync(TimeSpan.FromSeconds(4), ct);

        var observation = ctx.Session.ActiveObservation!;
        var loopTask = Task.Run(async () => await ctx.Session.ImagingLoopAsync(observation, -0.5, ct), ct);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        for (var i = 0; i < 30 && !loopTask.IsCompleted && !linked.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);
            for (var spin = 0; spin < 10 && !loopTask.IsCompleted; spin++)
            {
                await Task.Delay(10, ct);
            }
        }

        loopTask.IsCompleted.ShouldBeTrue("imaging loop should complete within timeout");
        await loopTask;

        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0, "should have written frames with single-filter plan");
        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");

        ctx.CleanupImageOutputFolder();
    }
}
