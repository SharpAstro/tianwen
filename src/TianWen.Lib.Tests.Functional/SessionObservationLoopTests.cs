using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Tests;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Multi-target observation loop integration tests using a winter night scenario from Vienna.
/// Site: 48.2°N, 16.3°E. Date: 2025-12-15, session starts 22:00 UTC (midnight local).
/// Equipment: 80mm f/6 APO refractor (480mm FL), 1024×768 sensor.
/// At 22:00 UTC Dec 15 from Vienna, LST ≈ 4.6h:
///   M45 (RA=3.79) HA≈0.8h, alt≈64° — high, visible
///   M42 (RA=5.59) HA≈−1.0h, alt≈36° — near transit, visible
///   Seagull (RA=7.06) HA≈−2.5h, alt≈23° — rising, visible above 15°
///   Sagittarius (RA=18.0, Dec=−30°) alt≈−66° — well below horizon
/// </summary>
public class SessionObservationLoopTests(ITestOutputHelper output)
{
    private const int TrueBestFocusPosition = 1000;
    private static readonly DateTimeOffset WinterNightStart = new(2025, 12, 15, 22, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Creates a session configured for winter observation loop tests:
    /// synthetic star generation enabled, focuser at best focus, observation index advanced.
    /// </summary>
    private async Task<SessionTestContext> CreateWinterSessionAsync(
        ScheduledObservation[] observations,
        SessionConfiguration? configuration = null,
        string? mountPort = "LX200",
        CancellationToken cancellationToken = default)
    {
        var config = configuration ?? SessionTestHelper.DefaultConfiguration;

        var ctx = await SessionTestHelper.CreateSessionAsync(
            output, config, observations, now: WinterNightStart, focalLength: 480, mountPort: mountPort, cancellationToken: cancellationToken);

        ctx.Camera.TrueBestFocus = TrueBestFocusPosition;
        ctx.Camera.FocusPosition = TrueBestFocusPosition;

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

    /// <summary>
    /// Runs the observation loop on a background task and pumps fake time from the test thread.
    /// Uses small time increments to avoid racing ahead of the observation loop.
    /// Returns when the loop completes or the wall-clock timeout expires.
    /// </summary>
    private static async Task RunObservationLoopWithTimePumpAsync(
        SessionTestContext ctx,
        TimeSpan subExposure,
        CancellationToken cancellationToken)
    {
        // Enable external time pump mode: the obs loop's SleepAsync will wait for
        // time to advance rather than advancing it, preventing concurrent Advance races.
        ctx.External.ExternalTimePump = true;

        var loopTask = Task.Run(async () => await ctx.Session.ObservationLoopAsync(cancellationToken), cancellationToken);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        // Pump time in small increments — the obs loop yields on SleepAsync until
        // we advance past its target time, ensuring deterministic sequencing.
        var pumpIncrement = TimeSpan.FromSeconds(5);
        var maxFakeTime = TimeSpan.FromHours(24);
        var pumped = TimeSpan.Zero;
        while (pumped < maxFakeTime && !loopTask.IsCompleted && !linked.IsCancellationRequested)
        {
            ctx.External.Advance(pumpIncrement);
            pumped += pumpIncrement;

            // Yield to let the observation loop process events triggered by the time advance
            await Task.Delay(1, cancellationToken);
        }

        loopTask.IsCompleted.ShouldBeTrue("observation loop should have completed within timeout");
        await loopTask;
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenTargetBelowHorizonWhenObservationLoopThenSkippedAndNextTargetImaged()
    {
        // given — Sagittarius region (RA=18h, Dec=-30°) is well below horizon in December nights
        // from Vienna, while M42 is near transit and visible
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);

        var config = SessionTestHelper.DefaultConfiguration with
        {
            MinHeightAboveHorizon = 10
        };

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(18.0, -30.0, "Sgr_Region", null),
                WinterNightStart,
                TimeSpan.FromMinutes(15),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            ),
            new ScheduledObservation(
                new Target(5.588, -5.391, "M42", null),
                WinterNightStart,
                TimeSpan.FromMinutes(15),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // when
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // then — Sgr_Region (index 0) should have been skipped
        ctx.Session.CurrentObservationIndex.ShouldBeGreaterThanOrEqualTo(1,
            "Sgr_Region should have been skipped due to being below horizon");
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0,
            "M42 should have produced frames");

        output.WriteLine($"Final observation index: {ctx.Session.CurrentObservationIndex}");
        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure time: {ctx.Session.TotalExposureTime}");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenThreeWinterTargetsWhenAllVisibleThenAllObservationsAdvanced()
    {
        // given — all three targets visible at 22:00 UTC Dec 15 from Vienna (min alt 10°)
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);

        var config = SessionTestHelper.DefaultConfiguration with
        {
            MinHeightAboveHorizon = 10
        };

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(5.588, -5.391, "M42", null),
                WinterNightStart,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            ),
            new ScheduledObservation(
                new Target(3.791, 24.105, "M45", null),
                WinterNightStart,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            ),
            new ScheduledObservation(
                new Target(7.063, -10.45, "Seagull", null),
                WinterNightStart,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // when
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // then — all three observations should have been attempted
        ctx.Session.CurrentObservationIndex.ShouldBeGreaterThanOrEqualTo(3,
            "all three observations should have been advanced through");
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThanOrEqualTo(3,
            "should have written at least one frame per target");

        output.WriteLine($"Final observation index: {ctx.Session.CurrentObservationIndex}");
        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure time: {ctx.Session.TotalExposureTime}");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenM42WhenAltitudeDropsBelowMinThenImagingStopsEarly()
    {
        // given — M42 transit altitude from Vienna ≈ 36.4°. With min alt 30°, M42 is above 30°
        // for about ±2h around transit (22:43 UTC). Starting at 22:00, it drops below ~00:45 UTC.
        // A 4-hour observation should stop early.
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var scheduledDuration = TimeSpan.FromHours(4);

        var config = SessionTestHelper.DefaultConfiguration with
        {
            MinHeightAboveHorizon = 30
        };

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(5.588, -5.391, "M42", null),
                WinterNightStart,
                scheduledDuration,
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // when
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // then — some frames captured, but imaging stopped early
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0,
            "should have captured frames while M42 was still above minimum altitude");
        ctx.Session.TotalExposureTime.ShouldBeLessThan(scheduledDuration * 0.9,
            "imaging should stop early when M42 drops below minimum altitude");

        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure time: {ctx.Session.TotalExposureTime}");
        output.WriteLine($"Scheduled duration: {scheduledDuration}");
    }

    [Fact(Timeout = 300_000)]
    public async Task GivenRefocusOnNewTargetWhenSwitchingTargetsThenBaselineStoredPerTarget()
    {
        // given — two targets with AlwaysRefocusOnNewTarget enabled
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);

        var config = SessionTestHelper.DefaultConfiguration with
        {
            MinHeightAboveHorizon = 10,
            AlwaysRefocusOnNewTarget = true
        };

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(5.588, -5.391, "M42", null),
                WinterNightStart,
                TimeSpan.FromMinutes(10),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            ),
            new ScheduledObservation(
                new Target(3.791, 24.105, "M45", null),
                WinterNightStart,
                TimeSpan.FromMinutes(10),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // when
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // then — baseline HFD stored per observation index
        ctx.Session.BaselineByObservation.ShouldContainKey(0,
            "baseline should be stored for first observation (M42)");
        ctx.Session.BaselineByObservation.ShouldContainKey(1,
            "baseline should be stored for second observation (M45)");
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0);

        output.WriteLine($"Final observation index: {ctx.Session.CurrentObservationIndex}");
        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Baseline observations: {string.Join(", ", ctx.Session.BaselineByObservation.Keys)}");
    }

    /// <summary>
    /// Test meridian flip: a target starting slightly east of meridian (HA ≈ -0.15h) with
    /// AcrossMeridian=true. After ~15 min of fake time, HA crosses the deadband (+0.1h),
    /// triggering PerformMeridianFlipAsync. The mount re-slews, guider restarts, and
    /// imaging continues on the new pier side.
    /// At Dec 15 22:00 UTC from Vienna, LST ≈ 4.74h.
    /// Target RA = 4.89h → initial HA = LST - RA = -0.15h (east of meridian).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenAcrossMeridianTargetWhenHACrossesDeadbandThenFlipAndContinueImaging()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);

        // Target starts at HA ≈ -0.15h, crosses to +0.1h after ~15 min → flip triggers
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(4.89, 20.0, "FlipTarget", null),
                WinterNightStart,
                TimeSpan.FromMinutes(30), // long enough to image before and after flip
                AcrossMeridian: true,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        // Use plain FakeMountDriver (not LX200 serial protocol) to avoid timer interleaving
        // between the slew simulation timer and the imaging loop's faster PeriodicTimer tick.
        using var ctx = await CreateWinterSessionAsync(observations, mountPort: null, cancellationToken: ct);

        // Run the observation loop with time pump
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // Should have produced frames (some before the flip, some after)
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0, "should have written frames across meridian flip");

        // Observation should have advanced (completed its scheduled duration)
        ctx.Session.CurrentObservationIndex.ShouldBeGreaterThanOrEqualTo(1,
            "observation should have advanced after completing duration");

        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure: {ctx.Session.TotalExposureTime}");
    }
}
