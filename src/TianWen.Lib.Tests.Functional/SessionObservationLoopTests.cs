using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Linq;
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
///   M45 (RA=3.79) HA≈0.8h, alt≈64°: high, visible
///   M42 (RA=5.59) HA≈−1.0h, alt≈36°: near transit, visible
///   Seagull (RA=7.06) HA≈−2.5h, alt≈23°: rising, visible above 15°
///   Sagittarius (RA=18.0, Dec=−30°) alt≈−66°: well below horizon
/// </summary>
[Collection("Session")]
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
        string? mountPort = null,
        DateTimeOffset? now = null,
        MountLimitConfiguration? mountLimits = null,
        bool coupleCameraToMount = true,
        CancellationToken cancellationToken = default)
    {
        var config = configuration ?? SessionTestHelper.DefaultConfiguration;

        var ctx = await SessionTestHelper.CreateSessionAsync(
            output, config, observations, now: now ?? WinterNightStart, focalLength: 480, mountPort: mountPort,
            mountLimits: mountLimits, coupleCameraToMount: coupleCameraToMount, cancellationToken: cancellationToken);

        ctx.Camera.TrueBestFocus = TrueBestFocusPosition;
        ctx.Camera.FocusPosition = TrueBestFocusPosition;

        // Move focuser to best focus
        await ctx.Focuser.BeginMoveAsync(TrueBestFocusPosition, cancellationToken);
        while (await ctx.Focuser.GetIsMovingAsync(cancellationToken))
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
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
        ctx.TimeProvider.ExternalTimePump = true;

        var loopTask = ctx.Track(Task.Run(async () => await ctx.Session.ObservationLoopAsync(ctx.Token), ctx.Token));

        // Pump time in small increments: the obs loop yields on SleepAsync until
        // we advance past its target time, ensuring deterministic sequencing.
        await ctx.TimeProvider.PumpUntilCompletedAsync(loopTask, TimeSpan.FromSeconds(5), TimeSpan.FromHours(24),
            progress: () => ctx.Session.ImagingLoopTicks, cancellationToken: cancellationToken);

        loopTask.IsCompleted.ShouldBeTrue("observation loop should have completed within timeout");
        await loopTask;
    }

    /// <summary>
    /// Subscribes to <see cref="ISession.FrameWritten"/> and collects every written frame's
    /// exposure-log entry (target name + fake-clock timestamp) so tests can assert <em>when</em>
    /// a given target was actually imaged. The event fires from the loop's thread-pool task, so
    /// the collector is a <see cref="ConcurrentQueue{T}"/>.
    /// </summary>
    private static ConcurrentQueue<ExposureLogEntry> CaptureFrames(SessionTestContext ctx)
    {
        var frames = new ConcurrentQueue<ExposureLogEntry>();
        ctx.Session.FrameWritten += (_, e) => frames.Enqueue(e.Entry);
        return frames;
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenTargetBelowHorizonWhenObservationLoopThenSkippedAndNextTargetImaged()
    {
        // given: Sagittarius region (RA=18h, Dec=-30°) is well below horizon in December nights
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

        await using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // when
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // then: Sgr_Region (index 0) should have been skipped
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
        // given, all three targets visible at 22:00 UTC Dec 15 from Vienna (min alt 10°)
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

        await using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // when
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // then, all three observations should have been attempted
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
        // given: M42 transit altitude from Vienna ≈ 36.4°. With min alt 30°, M42 drops below
        // ~00:45 UTC. Start at 00:20 (25 min before drop) so we capture a few frames then stop.
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var scheduledDuration = TimeSpan.FromHours(1);
        var nearDropStart = new DateTimeOffset(2025, 12, 16, 0, 20, 0, TimeSpan.Zero);

        var config = SessionTestHelper.DefaultConfiguration with
        {
            MinHeightAboveHorizon = 30
        };

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(5.588, -5.391, "M42", null),
                nearDropStart,
                scheduledDuration,
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        await using var ctx = await CreateWinterSessionAsync(observations, config, now: nearDropStart, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // when
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // then: some frames captured, but imaging stopped early due to altitude
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
        // given: two targets with AlwaysRefocusOnNewTarget enabled
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

        await using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // when
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // then: baseline HFD stored per observation index
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
        await using var ctx = await CreateWinterSessionAsync(observations, mountPort: null, cancellationToken: ct);

        // Run the observation loop with time pump
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // Should have produced frames (some before the flip, some after)
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0, "should have written frames across meridian flip");

        // A German equatorial mount MUST flip here (contrast: the non-German theory below asserts 0).
        ctx.Session.MeridianFlipCount.ShouldBeGreaterThan(0,
            "a GEM crossing the meridian must perform a flip");

        // Observation should have advanced (completed its scheduled duration)
        ctx.Session.CurrentObservationIndex.ShouldBeGreaterThanOrEqualTo(1,
            "observation should have advanced after completing duration");

        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Total exposure: {ctx.Session.TotalExposureTime}");
    }

    /// <summary>
    /// A fork/equatorial (<see cref="AlignmentMode.Polar"/>) or Alt-Az mount never meridian-flips; 
    /// only a German equatorial mount's counterweight bar would collide with the pier past the meridian.
    /// Same geometry as <see cref="GivenAcrossMeridianTargetWhenHACrossesDeadbandThenFlipAndContinueImaging"/>
    /// (a target that crosses the meridian mid-observation), but the mount reports a non-German alignment,
    /// so the imaging loop must track straight across: frames keep being written and ZERO flips occur.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(AlignmentMode.Polar)] // fork on an equatorial wedge
    [InlineData(AlignmentMode.AltAz)] // alt-azimuth
    public async Task GivenNonGermanMountWhenTargetCrossesMeridianThenImagesWithoutFlipping(AlignmentMode alignment)
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);

        // Same crossing geometry as the GEM flip test: HA starts ~-0.15h, crosses to +0.1h after ~15 min.
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(4.89, 20.0, "MeridianCrosser", null),
                WinterNightStart,
                TimeSpan.FromMinutes(30),
                AcrossMeridian: true,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        await using var ctx = await CreateWinterSessionAsync(observations, mountPort: null, cancellationToken: ct);

        // Make the (otherwise German) fake report a non-German alignment; a fork or Alt-Az mount.
        ((FakeMountDriver)ctx.Mount).Alignment = alignment;

        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        ctx.Session.MeridianFlipCount.ShouldBe(0,
            "a fork / Alt-Az mount tracks across the meridian and must never flip");
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0,
            "imaging must continue straight across the meridian");
        ctx.Session.CurrentObservationIndex.ShouldBeGreaterThanOrEqualTo(1,
            "observation should advance after completing its duration");

        output.WriteLine($"alignment={alignment} frames={ctx.Session.TotalFramesWritten} flips={ctx.Session.MeridianFlipCount}");
    }

    /// <summary>
    /// Regression for the SkyWatcher meridian-flip infinite loop (the observation-loop "endless slew"):
    /// join an <c>AcrossMeridian=true</c> observation whose target has <em>already</em> crossed the
    /// meridian (HA ≈ +0.8h west at the start of imaging). The SkyWatcher fake reports its pier side
    /// from the Dec encoder, so it stays Normal throughout a west-of-meridian track and never signals a
    /// pier-side change. The old code re-commanded a (no-op) flip every tick; aborting every exposure,
    /// writing zero frames, and slewing forever. The fix (destination-side gate + hasFlipped backstop)
    /// recognises the mount is already on the correct side and just images. We assert frames are written
    /// and the loop completes (before the fix it would never complete and TotalFramesWritten stays 0).
    /// At Dec 15 22:00 UTC from Vienna LST ≈ 4.74h, so RA = 3.94h → HA = +0.8h (west of meridian).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenSkywatcherJoinsAcrossMeridianTargetAlreadyWestThenImagesWithoutFlipLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(3.94, 20.0, "JoinedWestTarget", null),
                WinterNightStart,
                TimeSpan.FromMinutes(10),
                AcrossMeridian: true,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        await using var ctx = await CreateWinterSessionAsync(observations, mountPort: "SkyWatcher", cancellationToken: ct);

        // The SkyWatcher driver defaults to NaN site (real mounts learn it via the protocol); the live
        // session pushes it in InitialisationAsync, which this direct-loop harness bypasses. Set it so the
        // transform can resolve the site time zone (Vienna), matching CreateSessionAsync's URI coords.
        await ctx.Mount.SetSiteLatitudeAsync(48.2, ct);
        await ctx.Mount.SetSiteLongitudeAsync(16.3, ct);

        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // Before the fix: 0 frames (every exposure aborted by a perpetual flip) and the loop never ends.
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0,
            "an already-past-meridian target must image, not flip forever on a SkyWatcher");
        ctx.Session.CurrentObservationIndex.ShouldBeGreaterThanOrEqualTo(1,
            "observation should advance after completing its duration instead of looping");

        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
    }

    /// <summary>
    /// Mount safety limits, end to end on the SkyWatcher fake (docs/plans/mount-safety-limits.md): a GEM with
    /// limits ENABLED images a target across the meridian. The flip is real on this driver now (the goto
    /// chooses the other axis solution), so after it the mount is in the Normal state and the meridian
    /// limit must stay clear however far west it tracks -- the bug the pointing-state fix closed was the
    /// limit stopping exactly such a rig ~30 min after a good flip. Limits warn 20 / act 40 min, so the
    /// clamp (act - 5) never touches the default flip window (5-10 min) and the two coexist as designed.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task GivenSkywatcherWithLimitsWhenTargetCrossesMeridianThenItFlipsAndTheLimitStaysClear()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        // Same crossing geometry as the GEM flip test (HA ~-0.15 h at start), imaged for 75 min so the
        // hour angle ends well past the 40-min action threshold that would have stopped an unflipped rig.
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(4.89, 20.0, "MeridianCrosser", null),
                WinterNightStart,
                TimeSpan.FromMinutes(75),
                AcrossMeridian: true,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };
        var limits = new MountLimitConfiguration(Enabled: true, MeridianWarnMinutes: 20.0, MeridianActionExtraMinutes: 20.0);
        // OPTS OUT of camera-mount coupling, which is otherwise the default -- on COST, not on any
        // failure. Coupled this test passes, flips once and writes ~147 frames exactly as it does
        // here; it just takes 3m06 of its 5-minute budget instead of 20 s, and buys nothing with
        // them: nothing below asserts on a guide frame.
        //
        // Worth recording what the cost is NOT, because two plausible readings are both wrong and
        // this comment previously stated one of them as fact. It is not the guide star: the tracker
        // acquires on the first frame and holds (measured; a probe drove 200 coupled guide frames
        // and stayed locked through 123 px of drift). And it is not the rendering, which is the
        // reading the fake's own cost invites -- across a full coupled run the synthetic renders
        // total 946 ms and the coupled mount-pointing reads 860 ms, together 1% of the 186 s.
        //
        // What remains is the harness waiting for the session loop to get back to a SleepAsync park,
        // and that wait is REAL rather than a polling artefact. Worth stating because the obvious
        // suspect was measured and cleared: PumpUntilCompletedAsync polls Task.Delay(1), which on
        // Windows returns in ~15.7 ms rather than 1 ms, and the pump's wait was 165 s across 10,445
        // such polls -- a perfect fit for "the poll granularity IS the cost". It is not.
        // WindowsTimerResolution now holds the quantum at ~1.5 ms for the pumped run, which made the
        // uncoupled test 4x faster (20 s -> 5 s) and this whole suite 2.3x faster, and left the
        // COUPLED time unchanged at ~3m20. So the finer clock only samples the same genuine wait more
        // often. A signal-based wait was tried too, and was worse (4m12).
        //
        // The residue is therefore how long the coupled loop takes to make progress between parks,
        // which is not yet explained by anything measured. Do not re-derive the two dead ends above.
        await using var ctx = await CreateWinterSessionAsync(observations, mountPort: "SkyWatcher", mountLimits: limits,
            coupleCameraToMount: false, cancellationToken: ct);
        await ctx.Mount.SetSiteLatitudeAsync(48.2, ct);
        await ctx.Mount.SetSiteLongitudeAsync(16.3, ct);

        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        ctx.Session.MeridianFlipCount.ShouldBeGreaterThan(0, "a GEM crossing the meridian must flip");
        (await ctx.Mount.GetSideOfPierAsync(ct)).ShouldBe(PointingState.Normal, "the SkyWatcher fake really flipped: the other axis solution");
        (await ctx.Mount.GetHourAngleAsync(ct)).ShouldBeGreaterThan(40.0 / 60.0, "premise: the run ended past the action threshold");
        ctx.Session.MountLimitVerdict.IsBreached.ShouldBeFalse(
            $"a flipped rig tracking west is moving away from the pier, yet: {ctx.Session.MountLimitVerdict.Describe()}");
        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeTrue("the limit never acted");
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0);
        ctx.Session.CurrentObservationIndex.ShouldBeGreaterThanOrEqualTo(1, "the observation ran its full duration");
        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}, flips: {ctx.Session.MeridianFlipCount}");
    }

    /// <summary>
    /// The limit acts on the pointing state the SESSION verified, not the one the driver reports.
    /// <para>
    /// The rig is a plain <see cref="FakeMountDriver"/>, whose <see cref="PointingStateSource"/> is
    /// <see cref="PointingStateSource.Computed"/> -- it derives pier side from the hour angle, so east
    /// of the meridian it always answers <see cref="PointingState.ThroughThePole"/> whatever the tube
    /// is doing. <c>MountLimits.TrustedPointingState</c> rightly refuses that report, and the limit
    /// then falls back to an hour-angle estimate that reads CLEAR here.
    /// </para>
    /// <para>
    /// But the session knows better: the poll latched <see cref="PointingState.Normal"/> while the
    /// mount was west, and no slew has moved it since. A rig on the far side of the pier that is then
    /// pointed EAST -- a wrong-way goto, a bad sync -- is swinging its tube back toward the pier, and
    /// this is the case only the verified state can see. It is deliberately the MIRROR case: west of
    /// the meridian an Unknown state and a ThroughThePole one read alike, so a west-side test would
    /// pass with the wiring removed.
    /// </para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheLimitActsOnTheVerifiedPointingStateNotTheDriversReport()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(4.89, 20.0, "MirrorHazard", null),
                WinterNightStart, TimeSpan.FromMinutes(30), AcrossMeridian: true,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure), Gain: 0, Offset: 0)
        };
        var limits = new MountLimitConfiguration(Enabled: true, MeridianWarnMinutes: 20.0, MeridianActionExtraMinutes: 20.0);
        await using var ctx = await CreateWinterSessionAsync(observations, mountLimits: limits, cancellationToken: ct);
        await ctx.Mount.SetSiteLatitudeAsync(48.2, ct);
        await ctx.Mount.SetSiteLongitudeAsync(16.3, ct);
        // Establish the verified state through an actual GOTO -- the only thing that sets it. There is
        // no first-poll seed: a seed with no goto behind it is just the driver's own answer wearing the
        // word "verified", and the limit asked for a verified state precisely to refuse that.
        // Land WEST but inside the threshold (HA ~ +0.3h against an action point of 40 min), so the
        // slew itself cannot trip the limit and the latch records Normal.
        await ctx.Mount.BeginSlewRaDecAsync(4.3, 20.0, ct);
        for (var i = 0; i < 600 && await ctx.Mount.IsSlewingAsync(ct); i++)
        {
            await ctx.Session.PollDeviceStatesAsync(ct);
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(200), ct);
        }
        (await ctx.Mount.IsSlewingAsync(ct)).ShouldBeFalse("the goto must land, or nothing is latched");
        await ctx.Session.PollDeviceStatesAsync(ct);
        (await ctx.Session.GetSideOfPierAsync(ct)).ShouldBe(PointingState.Normal,
            "premise: the goto landed Normal and the poll latched it");

        // A fresh test session has never initialised a mount, so tracking is off and the meridian test
        // would be silent for that reason instead of the one under test. On AFTER the slew, so the goto
        // cannot trip the limit on its way.
        await ctx.Mount.SetTrackingAsync(true, ct);

        // Now point EAST by SYNC. Nothing slewed, so the latch holds -- but the driver's computed
        // answer flips, which is the whole difference this test turns on.
        await ctx.Mount.SyncRaDecAsync(5.6, 20.0, ct);
        await ctx.Session.PollDeviceStatesAsync(ct);

        (await ctx.Mount.GetHourAngleAsync(ct)).ShouldBeLessThan(-0.8,
            "premise: the mount is well east of the meridian");
        (await ctx.Mount.GetSideOfPierAsync(ct)).ShouldBe(PointingState.ThroughThePole,
            "premise: the driver COMPUTES its state from the hour angle, so it now reports the wrong side");
        (await ctx.Session.GetSideOfPierAsync(ct)).ShouldBe(PointingState.Normal,
            "premise: the session's canonical answer is unmoved -- no slew, so the tube did not change sides");

        var verdict = ctx.Session.MountLimitVerdict;
        verdict.Kind.ShouldBe(MountLimitKind.Meridian,
            $"the tube is swinging back toward the pier and only the verified state shows it: {verdict.Describe()}");
    }

    /// <summary>
    /// The limit is the ULTIMATE clamp, end to end: the user has set the flip LATER than the mechanical limit
    /// (earliest 60 min, action at 20 min), so the flip window collapses and the imaging loop sits in the
    /// pre-flip obstruction pause while the mount tracks on -- through the pole, counterweight rising --
    /// until the limit acts. The run must end as a LIMIT (tracking stopped, no advance, verdict measured on
    /// the RA axis since this driver models it), not as a fault and not by quietly moving to the next target.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task GivenSkywatcherWithAFlipConfiguredLaterThanTheLimitThenTheLimitStopsTheRunAsALimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(4.89, 20.0, "MeridianCrosser", null),
                WinterNightStart,
                TimeSpan.FromMinutes(90),
                AcrossMeridian: true,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            ),
            new ScheduledObservation(
                new Target(6.75, 16.7, "NeverReached", null),
                WinterNightStart + TimeSpan.FromMinutes(90),
                TimeSpan.FromMinutes(10),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };
        var lateFlip = SessionTestHelper.DefaultConfiguration with
        {
            MeridianFlipEarliestMinutesAfter = 60,
            MeridianFlipLatestMinutesAfter = 90,
        };
        var limits = new MountLimitConfiguration(Enabled: true, MeridianWarnMinutes: 10.0, MeridianActionExtraMinutes: 10.0);
        await using var ctx = await CreateWinterSessionAsync(observations, lateFlip, mountPort: "SkyWatcher", mountLimits: limits, cancellationToken: ct);
        await ctx.Mount.SetSiteLatitudeAsync(48.2, ct);
        await ctx.Mount.SetSiteLongitudeAsync(16.3, ct);
        var frames = CaptureFrames(ctx);

        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        var verdict = ctx.Session.MountLimitVerdict;
        verdict.Kind.ShouldBe(MountLimitKind.Meridian, verdict.Describe());
        verdict.Basis.ShouldBe(MountLimitBasis.AxisAngle, "the SkyWatcher driver models its axis, so the mechanical tier answered");
        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeFalse("the limit stopped the mount");
        (await ctx.Mount.GetHourAngleAsync(ct)).ShouldBeInRange(20.0 / 60.0, 30.0 / 60.0, "stopped at the action threshold, not at the flip");
        ctx.Session.MeridianFlipCount.ShouldBe(0, "the flip never got its window");
        ctx.Session.CurrentObservationIndex.ShouldBe(0, "a limit ends the run; it does not advance to the next target");
        frames.ShouldNotContain(f => f.TargetName == "NeverReached");
        ctx.Session.Phase.ShouldNotBe(SessionPhase.Failed, "nothing is broken: the rig reached the edge of where it may point");
    }

    /// <summary>
    /// P5 end to end: the MOUNT stops tracking on its own mid-observation (a GSServer / OnStep / ASCOM driver
    /// acting on its own limit). The session must read that as a limit event and end the run -- not carry on
    /// to the next target, whose EnsureTrackingAsync would switch tracking straight back on against the
    /// driver's stop, and not report a fault. This is also what pins the loop-exit look: the imaging loop's
    /// while condition leaves on the first "not tracking" read, before the detector's second poll.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task GivenTheMountStopsTrackingOnItsOwnThenTheRunEndsAsALimitAndTheNextTargetIsNeverStarted()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(3.94, 20.0, "FirstTarget", null),
                WinterNightStart,
                TimeSpan.FromMinutes(20),
                AcrossMeridian: true,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            ),
            new ScheduledObservation(
                new Target(6.75, 16.7, "SecondTarget", null),
                WinterNightStart + TimeSpan.FromMinutes(20),
                TimeSpan.FromMinutes(10),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };
        // No limits configured at all: the driver's own limit exists whether or not ours does.
        await using var ctx = await CreateWinterSessionAsync(observations, mountPort: "SkyWatcher", cancellationToken: ct);
        await ctx.Mount.SetSiteLatitudeAsync(48.2, ct);
        await ctx.Mount.SetSiteLongitudeAsync(16.3, ct);
        var frames = CaptureFrames(ctx);
        // The "driver" stops tracking after the third frame of the first target. Issued from the PUMP
        // thread between two advances, when the loop is parked in its sleep -- from the loop's own
        // FrameWritten handler the stop landed mid-iteration at an undefined point relative to the poll
        // and the goto-completion hook, and the test passed or failed on that timing alone.
        ctx.TimeProvider.ExternalTimePump = true;
        var loopTask = ctx.Track(Task.Run(async () => await ctx.Session.ObservationLoopAsync(ct), ct));
        var stopped = false;

        // Paced to the loop, NOT a hand-rolled Advance loop. This test's premise is "stop the mount
        // once three frames of the first target exist", so it needs the loop to actually receive its
        // ticks. A raw pump free-runs: it charges 5 s of fake time per iteration whether or not the
        // loop observed one, the 10-minute observation window elapses while the loop has seen a
        // handful of ticks, and the whole two-target schedule finishes having written ONE frame. That
        // is how this went red on CI -- FirstTarget=1, so the premise never fired and `stopped` was
        // still false at the assertion. The probe makes the budget bound a stall instead.
        await ctx.TimeProvider.PumpUntilCompletedAsync(loopTask, TimeSpan.FromSeconds(5), TimeSpan.FromHours(4),
            onIteration: async _ =>
            {
                if (!stopped && frames.Count(f => f.TargetName == "FirstTarget") >= 3)
                {
                    await ctx.Mount.SetTrackingAsync(false, ct);
                    stopped = true;
                }
            },
            progress: () => ctx.Session.ImagingLoopTicks, cancellationToken: ct);

        loopTask.IsCompleted.ShouldBeTrue("observation loop should have completed within the pumped window");
        await loopTask;
        output.WriteLine($"verdict: {ctx.Session.MountLimitVerdict.Describe()}; frames: {string.Join(", ", frames.GroupBy(f => f.TargetName).Select(g => $"{g.Key}={g.Count()}"))}; tracking={await ctx.Mount.IsTrackingAsync(ct)}");

        stopped.ShouldBeTrue("premise: the mount was stopped from outside");
        ctx.Session.MountLimitVerdict.Kind.ShouldBe(MountLimitKind.DriverEnforced, ctx.Session.MountLimitVerdict.Describe());
        (await ctx.Mount.IsTrackingAsync(ct)).ShouldBeFalse("the session must not fight the driver's stop by re-enabling tracking");
        frames.ShouldNotContain(f => f.TargetName == "SecondTarget", "a limit ends the run; the next target is never started");
        ctx.Session.CurrentObservationIndex.ShouldBe(0);
        ctx.Session.Phase.ShouldNotBe(SessionPhase.Failed);
    }

    /// <summary>
    /// Branch coverage for <see cref="Session.WaitForScheduledStartAsync"/> without the time pump:
    /// past start -> StartedLate, start within the lead window (== now and just inside lead) ->
    /// Proceed (no sleep), start beyond session end -> SessionEnded. The actual parked wait is
    /// exercised end-to-end by <see cref="GivenSecondObservationStartsLaterWhenLoopRunsThenImagingWaitsForScheduledStart"/>.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GivenScheduledStartWhenWaitForScheduledStartThenBranchOutcomesAreCorrect()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(5.588, -5.391, "M42", null),
                WinterNightStart,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0)
        };

        await using var ctx = await CreateWinterSessionAsync(observations, cancellationToken: ct);

        // CreateWinterSessionAsync advances fake time slightly (focuser-move SleepAsync loop), so
        // anchor the branch boundaries on the live clock rather than WinterNightStart.
        var now = await ctx.Session.GetMountUtcNowAsync(ct);
        var sessionEnd = now.AddHours(8);
        var lead = SessionConfiguration.DefaultScheduledStartLeadTime;

        ScheduledObservation Obs(DateTimeOffset start) => new(
            new Target(5.588, -5.391, "M42", null),
            start,
            TimeSpan.FromMinutes(15),
            AcrossMeridian: false,
            FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
            Gain: 0,
            Offset: 0);

        // Start one hour in the past -> behind schedule, proceed immediately.
        (await ctx.Session.WaitForScheduledStartAsync(Obs(now - TimeSpan.FromHours(1)), sessionEnd, ct))
            .ShouldBe(Session.ScheduledStartOutcome.StartedLate);

        // Start exactly now -> within the lead window, proceed without sleeping.
        (await ctx.Session.WaitForScheduledStartAsync(Obs(now), sessionEnd, ct))
            .ShouldBe(Session.ScheduledStartOutcome.Proceed);

        // Start in the future but still inside the lead window -> proceed without sleeping.
        (await ctx.Session.WaitForScheduledStartAsync(Obs(now + lead - TimeSpan.FromMinutes(1)), sessionEnd, ct))
            .ShouldBe(Session.ScheduledStartOutcome.Proceed);

        // Lead-adjusted start beyond session end -> skip the observation.
        (await ctx.Session.WaitForScheduledStartAsync(Obs(sessionEnd.AddHours(1)), sessionEnd, ct))
            .ShouldBe(Session.ScheduledStartOutcome.SessionEnded);

        // No frames should have been produced by direct calls to the wait helper.
        ctx.Session.TotalFramesWritten.ShouldBe(0);
    }

    /// <summary>
    /// Two visible targets where the second is scheduled 45 min later than the first. The loop must
    /// image the first immediately, then <em>wait</em> until the second's (Start - lead) before
    /// imaging it -- the headline behaviour of docs/plans/scheduled-starts.md.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenSecondObservationStartsLaterWhenLoopRunsThenImagingWaitsForScheduledStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var config = SessionTestHelper.DefaultConfiguration with { MinHeightAboveHorizon = 10 };

        var laterStart = WinterNightStart + TimeSpan.FromMinutes(45);

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(5.588, -5.391, "M42", null),
                WinterNightStart,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0),
            new ScheduledObservation(
                new Target(3.791, 24.105, "M45", null),
                laterStart,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0)
        };

        await using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);
        await ctx.Mount.EnsureTrackingAsync(cancellationToken: ct);

        var frames = CaptureFrames(ctx);

        // when
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // then
        var all = frames.ToArray();
        var m42 = all.Where(f => f.TargetName == "M42").ToArray();
        var m45 = all.Where(f => f.TargetName == "M45").ToArray();

        m42.Length.ShouldBeGreaterThan(0, "M42 (immediate start) should have produced frames");
        m45.Length.ShouldBeGreaterThan(0, "M45 (later start) should have produced frames after the wait");

        var lead = SessionConfiguration.DefaultScheduledStartLeadTime;
        var firstM45 = m45.Min(f => f.Timestamp);
        var lastM42 = m42.Max(f => f.Timestamp);

        firstM45.ShouldBeGreaterThanOrEqualTo(laterStart - lead,
            "M45 must not be imaged before its scheduled start minus lead");
        lastM42.ShouldBeLessThan(laterStart - lead,
            "M42 (immediate) must finish well before M45's scheduled window opens");

        ctx.Session.CurrentObservationIndex.ShouldBeGreaterThanOrEqualTo(2,
            "both observations should have been advanced through");

        output.WriteLine($"M42 frames: {m42.Length} (last @ {lastM42:o})");
        output.WriteLine($"M45 frames: {m45.Length} (first @ {firstM45:o}, scheduled start {laterStart:o})");
    }

    /// <summary>
    /// The second observation's start lies beyond the session end (morning twilight). The loop must
    /// image the first, then end cleanly without slewing to or imaging the second.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenStartBeyondSessionEndWhenLoopRunsThenObservationSkippedCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var config = SessionTestHelper.DefaultConfiguration with { MinHeightAboveHorizon = 10 };

        // Session end is next-morning astronomical twilight (~+7h from WinterNightStart). +12h is
        // well past it, so obs[1] can never start tonight.
        var beyondSessionEnd = WinterNightStart + TimeSpan.FromHours(12);

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(5.588, -5.391, "M42", null),
                WinterNightStart,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0),
            new ScheduledObservation(
                new Target(3.791, 24.105, "LateTarget", null),
                beyondSessionEnd,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0)
        };

        await using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);
        await ctx.Mount.EnsureTrackingAsync(cancellationToken: ct);

        var frames = CaptureFrames(ctx);

        // when
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // then
        var all = frames.ToArray();
        all.ShouldContain(f => f.TargetName == "M42", "M42 should have been imaged");
        all.ShouldNotContain(f => f.TargetName == "LateTarget",
            "the beyond-session-end target must never be imaged");
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0);
        ctx.Session.CurrentObservationIndex.ShouldBe(1,
            "loop breaks at the beyond-session-end observation without advancing past it");

        output.WriteLine($"Frames written: {ctx.Session.TotalFramesWritten}");
        output.WriteLine($"Final observation index: {ctx.Session.CurrentObservationIndex}");
    }

    /// <summary>
    /// Cancelling during the scheduled-start wait must unwind the loop promptly via
    /// <see cref="OperationCanceledException"/> (chunked sleep is cancellation-responsive), without
    /// having imaged anything.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenCancellationDuringScheduledStartWaitThenLoopExitsPromptly()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var config = SessionTestHelper.DefaultConfiguration with { MinHeightAboveHorizon = 10 };

        // Single target 3 h in the future so the loop's very first action is the scheduled-start wait.
        var lateStart = WinterNightStart + TimeSpan.FromHours(3);

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(5.588, -5.391, "M42", null),
                lateStart,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0)
        };

        await using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);
        await ctx.Mount.EnsureTrackingAsync(cancellationToken: ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        ctx.TimeProvider.ExternalTimePump = true;

        var loopTask = ctx.Track(Task.Run(async () => await ctx.Session.ObservationLoopAsync(cts.Token), ctx.Token));

        // Wait until the loop is parked in the scheduled-start wait, then cancel.
        await ctx.TimeProvider.WaitForFirstWaiterAsync(loopTask, ct);
        await cts.CancelAsync();

        // then: the loop unwinds via OCE rather than spinning or hanging.
        await Should.ThrowAsync<OperationCanceledException>(async () => await loopTask);

        ctx.Session.TotalFramesWritten.ShouldBe(0, "nothing should be imaged before the scheduled start");

        output.WriteLine("Loop cancelled cleanly during scheduled-start wait.");
    }
}
