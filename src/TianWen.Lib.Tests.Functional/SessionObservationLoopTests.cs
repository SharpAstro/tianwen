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
///   M45 (RA=3.79) HA≈0.8h, alt≈64° — high, visible
///   M42 (RA=5.59) HA≈−1.0h, alt≈36° — near transit, visible
///   Seagull (RA=7.06) HA≈−2.5h, alt≈23° — rising, visible above 15°
///   Sagittarius (RA=18.0, Dec=−30°) alt≈−66° — well below horizon
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
        CancellationToken cancellationToken = default)
    {
        var config = configuration ?? SessionTestHelper.DefaultConfiguration;

        var ctx = await SessionTestHelper.CreateSessionAsync(
            output, config, observations, now: now ?? WinterNightStart, focalLength: 480, mountPort: mountPort, cancellationToken: cancellationToken);

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

        var loopTask = Task.Run(async () => await ctx.Session.ObservationLoopAsync(cancellationToken), cancellationToken);

        // Pump time in small increments — the obs loop yields on SleepAsync until
        // we advance past its target time, ensuring deterministic sequencing.
        await ctx.TimeProvider.PumpUntilCompletedAsync(loopTask, TimeSpan.FromSeconds(5), TimeSpan.FromHours(24), cancellationToken: cancellationToken);

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
        // given — M42 transit altitude from Vienna ≈ 36.4°. With min alt 30°, M42 drops below
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

        using var ctx = await CreateWinterSessionAsync(observations, config, now: nearDropStart, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // when
        await RunObservationLoopWithTimePumpAsync(ctx, subExposure, ct);

        // then — some frames captured, but imaging stopped early due to altitude
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
    /// A fork/equatorial (<see cref="AlignmentMode.Polar"/>) or Alt-Az mount never meridian-flips —
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

        using var ctx = await CreateWinterSessionAsync(observations, mountPort: null, cancellationToken: ct);

        // Make the (otherwise German) fake report a non-German alignment — a fork or Alt-Az mount.
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
    /// pier-side change. The old code re-commanded a (no-op) flip every tick — aborting every exposure,
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

        using var ctx = await CreateWinterSessionAsync(observations, mountPort: "SkyWatcher", cancellationToken: ct);

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

        using var ctx = await CreateWinterSessionAsync(observations, cancellationToken: ct);

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

        using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);
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

        using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);
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

        using var ctx = await CreateWinterSessionAsync(observations, config, cancellationToken: ct);
        await ctx.Mount.EnsureTrackingAsync(cancellationToken: ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ctx.TimeProvider.ExternalTimePump = true;

        var loopTask = Task.Run(async () => await ctx.Session.ObservationLoopAsync(cts.Token), ct);

        // Wait until the loop is parked in the scheduled-start wait, then cancel.
        await ctx.TimeProvider.WaitForFirstWaiterAsync(loopTask, ct);
        await cts.CancelAsync();

        // then — the loop unwinds via OCE rather than spinning or hanging.
        await Should.ThrowAsync<OperationCanceledException>(async () => await loopTask);

        ctx.Session.TotalFramesWritten.ShouldBe(0, "nothing should be imaged before the scheduled start");

        output.WriteLine("Loop cancelled cleanly during scheduled-start wait.");
    }
}
