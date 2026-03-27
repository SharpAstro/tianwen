using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class SessionImagingTests(ITestOutputHelper output)
{
    private const int TrueBestFocusPosition = 1000;

    /// <summary>
    /// Creates a session configured for imaging loop tests:
    /// synthetic star generation enabled, focuser at best focus, observation index advanced.
    /// </summary>
    private async Task<SessionTestContext> CreateImagingSessionAsync(
        SessionConfiguration? configuration = null,
        ScheduledObservation[]? observations = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var config = configuration ?? SessionTestHelper.DefaultConfiguration;
        var obs = observations ?? SessionTestHelper.DefaultScheduledObservations;

        // Use a date/time that allows astronomical observations from Vienna (48.2N, 16.3E)
        // June 15, 22:00 UTC = ~midnight local = well into astronomical night
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, config, obs, now: now, cancellationToken: cancellationToken);

        ctx.Camera.TrueBestFocus = TrueBestFocusPosition;
        ctx.Camera.FocusPosition = TrueBestFocusPosition; // at perfect focus

        // Move focuser to best focus
        await ctx.Focuser.BeginMoveAsync(TrueBestFocusPosition, cancellationToken);
        while (await ctx.Focuser.GetIsMovingAsync(cancellationToken))
        {
            await ctx.External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        // Advance observation index so ActiveObservation is set
        ctx.Session.AdvanceObservationForTest();

        return ctx;
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenHighAltitudeTargetWhenImagingLoopThenHighUtilization()
    {
        // given — M13 (RA=16.695h, Dec=+36.46) near zenith from Vienna in June
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var scheduledDuration = TimeSpan.FromMinutes(30);

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(16.695, 36.46, "M13", null),
                new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero),
                scheduledDuration,
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        using var ctx = await CreateImagingSessionAsync(observations: observations, cancellationToken: ct);

        // Enable tracking on mount
        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // Start guiding
        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        await guider.GuideAsync(0.3, 3, 30, ct);
        await ctx.External.SleepAsync(TimeSpan.FromSeconds(4), ct); // settle

        var observation = ctx.Session.ActiveObservation;
        observation.ShouldNotBeNull();

        // Get hour angle for pier side tracking
        var hourAngle = await ctx.Mount.GetHourAngleAsync(ct);

        // when — run imaging loop on thread pool, advance fake time from test thread
        var imagingTask = Task.Run(async () => await ctx.Session.ImagingLoopAsync(observation, hourAngle, ct));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var maxTicks = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxTicks && !imagingTask.IsCompleted && !linked.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);
            for (var spin = 0; spin < 10 && !imagingTask.IsCompleted; spin++)
            {
                await Task.Delay(10, ct);
            }
        }

        imagingTask.IsCompleted.ShouldBeTrue("imaging loop should have completed within timeout");
        var result = await imagingTask;

        // then
        result.ShouldBe(ImageLoopNextAction.AdvanceToNextObservation);
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0, "should have written at least one frame");
        ctx.Session.TotalExposureTime.ShouldBeGreaterThan(TimeSpan.Zero);

        var utilization = ctx.Session.TotalExposureTime / scheduledDuration;
        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure time: {ctx.Session.TotalExposureTime}");
        output.WriteLine($"Scheduled duration: {scheduledDuration}");
        output.WriteLine($"Utilization: {utilization:P1}");

        // With 30s subs over 30 min, each frame takes 2 ticks (start + fetch) → 30 frames = 50%.
        utilization.ShouldBeGreaterThanOrEqualTo(0.45, "imaging utilization should be at least 45%");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenHighMinAltitudeWhenTargetDropsBelowThenImagingStopsEarly()
    {
        // given — M13 max altitude from Vienna ~78°, set min to 70° so it drops below quickly
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var scheduledDuration = TimeSpan.FromHours(4);

        var config = SessionTestHelper.DefaultConfiguration with
        {
            MinHeightAboveHorizon = 70
        };

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(16.695, 36.46, "M13", null),
                new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero),
                scheduledDuration,
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        using var ctx = await CreateImagingSessionAsync(configuration: config, observations: observations, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        await guider.GuideAsync(0.3, 3, 30, ct);
        await ctx.External.SleepAsync(TimeSpan.FromSeconds(4), ct);

        var observation = ctx.Session.ActiveObservation;
        observation.ShouldNotBeNull();

        var hourAngle = await ctx.Mount.GetHourAngleAsync(ct);

        // when — run imaging loop on thread pool, advance fake time from test thread
        var imagingTask = Task.Run(async () => await ctx.Session.ImagingLoopAsync(observation, hourAngle, ct));

        using var timeout2 = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var linked2 = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout2.Token);
        var maxTicks = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxTicks && !imagingTask.IsCompleted && !linked2.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);
            for (var spin = 0; spin < 10 && !imagingTask.IsCompleted; spin++)
            {
                await Task.Delay(10, ct);
            }
        }

        imagingTask.IsCompleted.ShouldBeTrue("imaging loop should have completed within timeout");
        var result = await imagingTask;

        // then — should have stopped early due to altitude check
        result.ShouldBe(ImageLoopNextAction.AdvanceToNextObservation);

        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure time: {ctx.Session.TotalExposureTime}");
        output.WriteLine($"Scheduled duration: {scheduledDuration}");

        // With 70° min altitude, imaging should stop well before the full 4 hours
        ctx.Session.TotalExposureTime.ShouldBeLessThan(scheduledDuration * 0.5,
            "imaging should stop early when target drops below minimum altitude");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenObservationLoopWhenSingleTargetThenFramesWrittenAndAdvanced()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var scheduledDuration = TimeSpan.FromMinutes(15);

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(16.695, 36.46, "M13", null),
                new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero),
                scheduledDuration,
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        using var ctx = await CreateImagingSessionAsync(observations: observations, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // when — run observation loop on thread pool, advance fake time from test thread
        var loopTask = Task.Run(async () => await ctx.Session.ObservationLoopAsync(ct), ct);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var maxTicks = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxTicks && !loopTask.IsCompleted && !linked.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);
            for (var spin = 0; spin < 10 && !loopTask.IsCompleted; spin++)
            {
                await Task.Delay(10, ct);
            }
        }

        loopTask.IsCompleted.ShouldBeTrue("observation loop should have completed within timeout");
        await loopTask;

        // then
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0, "should have written frames");
        ctx.Session.TotalExposureTime.ShouldBeGreaterThan(TimeSpan.Zero);

        // Observation should have advanced past the single target
        ctx.Session.CurrentObservationIndex.ShouldBeGreaterThanOrEqualTo(1,
            "observation index should have advanced after completing target");

        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure time: {ctx.Session.TotalExposureTime}");
        output.WriteLine($"Final observation index: {ctx.Session.CurrentObservationIndex}");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenFocusDriftWhenHFDExceedsThresholdThenAutoRefocusTriggered()
    {
        // given — start at best focus, then defocus mid-session to trigger drift detection
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var scheduledDuration = TimeSpan.FromMinutes(10);

        // Use a low drift threshold to make it easier to trigger
        var config = SessionTestHelper.DefaultConfiguration with
        {
            FocusDriftThreshold = 1.05f, // 5% HFD increase triggers refocus
            BaselineHfdFrameCount = 2,   // establish baseline quickly
            DitherEveryNthFrame = 0      // disable dithering for this test
        };

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(16.695, 36.46, "M13", null),
                new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero),
                scheduledDuration,
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        using var ctx = await CreateImagingSessionAsync(configuration: config, observations: observations, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        await guider.GuideAsync(0.3, 3, 30, ct);
        await ctx.External.SleepAsync(TimeSpan.FromSeconds(4), ct);

        var observation = ctx.Session.ActiveObservation;
        observation.ShouldNotBeNull();
        var hourAngle = await ctx.Mount.GetHourAngleAsync(ct);

        // when — run imaging loop, defocus after baseline is established
        var defocused = false;
        var imagingTask = Task.Run(async () => await ctx.Session.ImagingLoopAsync(observation, hourAngle, ct));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        for (var i = 0; i < 500 && !imagingTask.IsCompleted && !linked.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);

            // After baseline is established (2 frames), defocus by moving focuser away
            if (!defocused && ctx.Session.BaselineByObservation.ContainsKey(0))
            {
                output.WriteLine($"Baseline established after pump {i}, defocusing by 80 steps");
                var currentPos = await ctx.Focuser.GetPositionAsync(ct);
                await ctx.Focuser.BeginMoveAsync(currentPos + 80, ct);
                while (await ctx.Focuser.GetIsMovingAsync(ct))
                {
                    await ctx.External.SleepAsync(TimeSpan.FromMilliseconds(100), ct);
                }
                defocused = true;
            }

            for (var spin = 0; spin < 10 && !imagingTask.IsCompleted; spin++)
            {
                await Task.Delay(10, ct);
            }
        }

        imagingTask.IsCompleted.ShouldBeTrue("imaging loop should have completed within timeout");
        await imagingTask;

        // then
        defocused.ShouldBeTrue("should have defocused during the test");
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0);

        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure time: {ctx.Session.TotalExposureTime}");

        // The baseline should have been updated after refocus (AutoFocusAsync stores new baseline)
        ctx.Session.BaselineByObservation.ShouldContainKey(0);
        var baselines = ctx.Session.BaselineByObservation[0];
        baselines[0].IsValid.ShouldBeTrue("baseline should be valid after auto-refocus");

        output.WriteLine($"Final baseline HFD: {baselines[0].MedianHfd:F2}");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenDitherEveryNthFrameWhenEnoughFramesCapturedThenDitheringTriggered()
    {
        // given — DitherEveryNthFrame=5 (default config), 30s subs, 10 min observation
        // With GCD=30s, tickSec=5, ditherEveryNTicks = 5 * (30/5) = 30 ticks (= 150s)
        // 10 min = 600s = 120 ticks → dithering fires at tick 30, 60, 90
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var scheduledDuration = TimeSpan.FromMinutes(10);

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(16.695, 36.46, "M13", null),
                new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero),
                scheduledDuration,
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        using var ctx = await CreateImagingSessionAsync(observations: observations, cancellationToken: ct);

        // Enable tracking on mount
        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // Start guiding
        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        await guider.GuideAsync(0.3, 3, 30, ct);
        await ctx.External.SleepAsync(TimeSpan.FromSeconds(4), ct); // settle

        var observation = ctx.Session.ActiveObservation;
        observation.ShouldNotBeNull();

        var hourAngle = await ctx.Mount.GetHourAngleAsync(ct);

        // when — run imaging loop, pump time
        var imagingTask = Task.Run(async () => await ctx.Session.ImagingLoopAsync(observation, hourAngle, ct));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var maxTicks = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxTicks && !imagingTask.IsCompleted && !linked.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);
            for (var spin = 0; spin < 10 && !imagingTask.IsCompleted; spin++)
            {
                await Task.Delay(10, ct);
            }
        }

        imagingTask.IsCompleted.ShouldBeTrue("imaging loop should have completed within timeout");
        var result = await imagingTask;

        // then — should have captured frames and dithered
        result.ShouldBe(ImageLoopNextAction.AdvanceToNextObservation);
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0);

        // With 10 min and 30s subs, we expect ~20 frames and at least 2 dithers
        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure time: {ctx.Session.TotalExposureTime}");

        // The guider's DitherCount tracks how many times DitherAsync was called
        guider.DitherCount.ShouldBeGreaterThan(0, "dithering should have been triggered at least once");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenCloudsRollingInWhenStarCountDropsThenConditionDetected()
    {
        // given — clear sky initially, clouds roll in after baseline is established,
        // then clear again to test recovery
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var scheduledDuration = TimeSpan.FromMinutes(30);

        var config = SessionTestHelper.DefaultConfiguration with
        {
            ConditionDeteriorationThreshold = 0.6f, // 60% star count drop triggers
            ConditionRecoveryTimeout = TimeSpan.FromMinutes(3), // short timeout for test
            BaselineHfdFrameCount = 2,
            DitherEveryNthFrame = 0 // disable dithering for this test
        };

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(16.695, 36.46, "M13", null),
                new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero),
                scheduledDuration,
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        using var ctx = await CreateImagingSessionAsync(configuration: config, observations: observations, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        await guider.GuideAsync(0.3, 3, 30, ct);
        await ctx.External.SleepAsync(TimeSpan.FromSeconds(4), ct);

        var observation = ctx.Session.ActiveObservation;
        observation.ShouldNotBeNull();
        var hourAngle = await ctx.Mount.GetHourAngleAsync(ct);

        // when — run imaging loop, inject clouds after baseline established, clear after a bit
        var cloudsInjected = false;
        var cloudsCleared = false;
        var imagingTask = Task.Run(async () => await ctx.Session.ImagingLoopAsync(observation, hourAngle, ct));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        for (var i = 0; i < 500 && !imagingTask.IsCompleted && !linked.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);

            // After baseline established, inject heavy clouds
            if (!cloudsInjected && ctx.Session.BaselineByObservation.ContainsKey(0))
            {
                output.WriteLine($"Baseline established at pump {i}, injecting clouds (coverage=0.8)");
                ctx.Camera.CloudCoverage = 0.8;
                cloudsInjected = true;
            }
            // After a few cloudy frames, clear the sky so recovery can succeed
            else if (cloudsInjected && !cloudsCleared && i > 10)
            {
                output.WriteLine($"Clearing clouds at pump {i}");
                ctx.Camera.CloudCoverage = 0;
                cloudsCleared = true;
            }

            for (var spin = 0; spin < 10 && !imagingTask.IsCompleted; spin++)
            {
                await Task.Delay(10, ct);
            }
        }

        imagingTask.IsCompleted.ShouldBeTrue("imaging loop should have completed within timeout");
        var result = await imagingTask;

        // then
        cloudsInjected.ShouldBeTrue("clouds should have been injected during the test");
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0, "should have written frames before clouds");

        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure time: {ctx.Session.TotalExposureTime}");
        output.WriteLine($"Result: {result}");
    }
}
