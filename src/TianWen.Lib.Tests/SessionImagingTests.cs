using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

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
        var ctx = await SessionTestHelper.CreateSessionAsync(output, config, obs, cancellationToken);

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

    [Fact]
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
                SubExposure: subExposure,
                Gain: 0,
                Offset: 0
            )
        };

        var ctx = await CreateImagingSessionAsync(observations: observations, cancellationToken: ct);

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

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var maxTicks = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxTicks && !imagingTask.IsCompleted && !timeout.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);
            await Task.Delay(10, ct);
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

        // With 30s subs over 30 min, we expect ~60 frames max. Allow for overhead.
        utilization.ShouldBeGreaterThan(0.50, "imaging utilization should exceed 50%");
    }

    [Fact]
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
                SubExposure: subExposure,
                Gain: 0,
                Offset: 0
            )
        };

        var ctx = await CreateImagingSessionAsync(configuration: config, observations: observations, cancellationToken: ct);

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

        using var timeout2 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var maxTicks = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxTicks && !imagingTask.IsCompleted && !timeout2.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);
            await Task.Delay(10, ct);
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

    [Fact]
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
                SubExposure: subExposure,
                Gain: 0,
                Offset: 0
            )
        };

        var ctx = await CreateImagingSessionAsync(observations: observations, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // when — run observation loop on thread pool, advance fake time from test thread
        var loopTask = Task.Run(async () => await ctx.Session.ObservationLoopAsync(ct));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var maxTicks = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxTicks && !loopTask.IsCompleted && !timeout.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);
            await Task.Delay(10, ct);
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
}
