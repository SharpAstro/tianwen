using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Tests;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Integration tests for the FOV obstruction scout flow. Pure-function classifier tests
/// live in <c>SessionScoutClassifierTests</c> in the unit-test project.
/// </summary>
[Collection("Session")]
public class SessionScoutAndProbeTests(ITestOutputHelper output)
{
    private const int TrueBestFocusPosition = 1000;

    /// <summary>
    /// Vienna winter night, 2025-12-15 22:00 UTC. M13 is below horizon (alt ~−40°),
    /// so we use M45 (high) and Seagull (rising) for tests where altitude matters.
    /// </summary>
    private static readonly DateTimeOffset WinterNightStart = new(2025, 12, 15, 22, 0, 0, TimeSpan.Zero);

    private async Task<SessionTestContext> CreateScoutSessionAsync(
        ScheduledObservation[] observations,
        SessionConfiguration? configuration = null,
        CancellationToken cancellationToken = default)
    {
        var config = configuration ?? SessionTestHelper.DefaultConfiguration with
        {
            // Short scout exposure so test loops finish quickly under fake time
            ScoutExposure = TimeSpan.FromSeconds(2)
        };

        var ctx = await SessionTestHelper.CreateSessionAsync(
            output, config, observations, now: WinterNightStart, focalLength: 480, cancellationToken: cancellationToken);

        ctx.Camera.TrueBestFocus = TrueBestFocusPosition;
        ctx.Camera.FocusPosition = TrueBestFocusPosition;

        // Move focuser to best focus
        await ctx.Focuser.BeginMoveAsync(TrueBestFocusPosition, cancellationToken);
        while (await ctx.Focuser.GetIsMovingAsync(cancellationToken))
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return ctx;
    }

    private static ScheduledObservation Obs(double ra, double dec, string name)
        => new(
            new Target(ra, dec, name, null),
            WinterNightStart,
            TimeSpan.FromMinutes(60),
            AcrossMeridian: false,
            FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(60)),
            Gain: 0,
            Offset: 0);

    [Fact(Timeout = 60_000)]
    public async Task GivenFirstObservationNoBaselineWhenScoutThenHealthy()
    {
        // No previous observation → no baseline → conservative Healthy classification.
        var ct = TestContext.Current.CancellationToken;
        var observations = new[] { Obs(3.79, 24.1, "M45") };
        using var ctx = await CreateScoutSessionAsync(observations, cancellationToken: ct);
        ctx.Session.AdvanceObservationForTest(); // index 0; prev = -1 → no baseline

        // Slew to target so scout has a sky position
        var target = ctx.Session.ActiveObservation!.Target;
        await ctx.Mount.BeginSlewRaDecAsync(target.RA, target.Dec, ct);
        while (await ctx.Mount.IsSlewingAsync(ct))
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(50), ct);
        }

        await RunScoutWithTimePumpAsync(ctx, async ct2 =>
        {
            var result = await ctx.Session.ScoutAndProbeAsync(ctx.Session.ActiveObservation!, ct2);
            result.Classification.ShouldBe(ScoutClassification.Healthy);
            result.EstimatedClearIn.ShouldBeNull();
        }, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenHealthyScoutMatchingBaselineWhenScoutAndProbeThenHealthy()
    {
        // Clear sky on both pre- and post-scout, baseline matches observed star count.
        var ct = TestContext.Current.CancellationToken;
        var observations = new[] { Obs(3.79, 24.1, "M45 prev"), Obs(3.79, 24.1, "M45 next") };
        using var ctx = await CreateScoutSessionAsync(observations, cancellationToken: ct);

        ctx.Camera.CloudCoverage = 0; // clear

        // Set baseline for index 0 with a believable star count for a 2s scout in this field
        ctx.Session.SetBaselineForObservationForTest(0,
        [
            new FrameMetrics(StarCount: 30, MedianHfd: 2.5f, MedianFwhm: 3.0f,
                Exposure: TimeSpan.FromSeconds(2), Gain: 0)
        ]);

        // Move to index 1 — scout will compare against baseline at index 0
        ctx.Session.AdvanceObservationForTest();
        ctx.Session.AdvanceObservationForTest();

        var target = ctx.Session.ActiveObservation!.Target;
        await ctx.Mount.BeginSlewRaDecAsync(target.RA, target.Dec, ct);
        while (await ctx.Mount.IsSlewingAsync(ct))
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(50), ct);
        }

        await RunScoutWithTimePumpAsync(ctx, async ct2 =>
        {
            var result = await ctx.Session.ScoutAndProbeAsync(ctx.Session.ActiveObservation!, ct2);
            output.WriteLine($"Result: {result.Classification}, scout stars: {result.Metrics[0].StarCount}");
            result.Classification.ShouldBe(ScoutClassification.Healthy);
        }, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenAllScoutsLowStarsWhenScoutAndProbeThenTransparency()
    {
        // Heavy cloud cover throughout — pre-scout fails, nudge does not recover.
        // Classifier flags as Obstruction tentatively, nudge confirms Transparency.
        var ct = TestContext.Current.CancellationToken;
        var observations = new[] { Obs(3.79, 24.1, "M45 prev"), Obs(3.79, 24.1, "M45 next") };
        using var ctx = await CreateScoutSessionAsync(observations, cancellationToken: ct);

        ctx.Camera.CloudCoverage = 0.98; // near-total cloud → almost no detectable stars

        ctx.Session.SetBaselineForObservationForTest(0,
        [
            new FrameMetrics(StarCount: 50, MedianHfd: 2.5f, MedianFwhm: 3.0f,
                Exposure: TimeSpan.FromSeconds(2), Gain: 0)
        ]);

        ctx.Session.AdvanceObservationForTest();
        ctx.Session.AdvanceObservationForTest();

        var target = ctx.Session.ActiveObservation!.Target;
        await ctx.Mount.BeginSlewRaDecAsync(target.RA, target.Dec, ct);
        while (await ctx.Mount.IsSlewingAsync(ct))
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(50), ct);
        }

        await RunScoutWithTimePumpAsync(ctx, async ct2 =>
        {
            var result = await ctx.Session.ScoutAndProbeAsync(ctx.Session.ActiveObservation!, ct2);
            output.WriteLine($"Result: {result.Classification}, stars: {result.Metrics[0].StarCount}");
            result.Classification.ShouldBe(ScoutClassification.Transparency);
            result.EstimatedClearIn.ShouldBeNull();
        }, ct);
    }

    [Fact(Timeout = 30_000)]
    public async Task GivenRisingTargetWhenEstimateObstructionClearTimeThenReturnsPositive()
    {
        // Seagull from Vienna at 22:00 UTC Dec 15: alt ~23° rising. With a small nudge of
        // ~0.07° (matches default fake camera FOV), the target should reach clear-alt within
        // a few minutes.
        var ct = TestContext.Current.CancellationToken;
        var observations = new[] { Obs(7.06, -10.7, "Seagull") };
        using var ctx = await CreateScoutSessionAsync(observations, cancellationToken: ct);
        ctx.Session.AdvanceObservationForTest();

        var clearIn = await ctx.Session.EstimateObstructionClearTimeAsync(
            ctx.Session.ActiveObservation!, ct);

        clearIn.ShouldNotBeNull();
        clearIn!.Value.ShouldBeGreaterThan(TimeSpan.Zero);
        clearIn.Value.ShouldBeLessThan(TimeSpan.FromMinutes(30));
        output.WriteLine($"Seagull rising — estimated clear in {clearIn}.");
    }

    [Fact(Timeout = 30_000)]
    public async Task GivenSettingTargetWhenEstimateObstructionClearTimeThenNull()
    {
        // M45 from Vienna at 22:00 UTC Dec 15: alt ~58°, near transit/descending. After a tiny
        // forward step the altitude should not exceed +nudge → null clear time.
        // (Use a noon-time observation 4h after to ensure it's setting.)
        var ct = TestContext.Current.CancellationToken;
        var lateNight = new DateTimeOffset(2025, 12, 16, 4, 0, 0, TimeSpan.Zero);
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(3.79, 24.1, "M45 setting", null),
                lateNight,
                TimeSpan.FromMinutes(60),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(60)),
                Gain: 0,
                Offset: 0)
        };
        var config = SessionTestHelper.DefaultConfiguration with { ScoutExposure = TimeSpan.FromSeconds(2) };
        using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, config, observations, now: lateNight, focalLength: 480, cancellationToken: ct);
        ctx.Session.AdvanceObservationForTest();

        var clearIn = await ctx.Session.EstimateObstructionClearTimeAsync(
            ctx.Session.ActiveObservation!, ct);

        clearIn.ShouldBeNull();
    }

    /// <summary>
    /// Pumps fake time on the test thread while <paramref name="action"/> runs on the thread pool.
    /// Required because the scout uses <c>SleepAsync</c> for the exposure dwell, and
    /// <c>FakeTimeProvider</c> needs external advancement to fire those timers.
    /// </summary>
    private static async Task RunScoutWithTimePumpAsync(
        SessionTestContext ctx, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        ctx.TimeProvider.ExternalTimePump = true;
        var task = Task.Run(async () => await action(ct), ct);

        var pumpIncrement = TimeSpan.FromSeconds(1);
        var maxFakeTime = TimeSpan.FromMinutes(20);
        var pumped = TimeSpan.Zero;
        while (pumped < maxFakeTime && !task.IsCompleted && !ct.IsCancellationRequested)
        {
            ctx.TimeProvider.Advance(pumpIncrement);
            pumped += pumpIncrement;
            await Task.Delay(1, ct);
        }

        task.IsCompleted.ShouldBeTrue("scout action should have completed within fake time");
        await task;
    }
}
