using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Tests;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Tests for the observable session surface: phase transitions, abort lifecycle,
/// cooling samples, and events (PhaseChanged, FrameWritten, FocusHistory).
/// </summary>
[Collection("Session")]
public class SessionPhaseTests(ITestOutputHelper output)
{
    // Winter night in Vienna: astro dark at ~17:30 UTC, twilight at ~04:30 UTC
    private static readonly DateTimeOffset WinterNight = new DateTimeOffset(2025, 12, 15, 17, 30, 0, TimeSpan.Zero);

    [Fact(Timeout = 120_000)]
    public async Task RunAsync_PhasesTransitionInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(3.7886, 24.1167, "M45", null),
                WinterNight,
                TimeSpan.FromMinutes(3),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        await using var ctx = await CreateWinterSessionAsync(observations, ct);

        // Record phase transitions
        var phases = new ConcurrentQueue<SessionPhase>();
        ctx.Session.PhaseChanged += (_, e) =>
        {
            output.WriteLine($"Phase: {e.OldPhase} → {e.NewPhase}");
            phases.Enqueue(e.NewPhase);
        };

        await RunToEndAsync(ctx, ctx.Token, ct);

        // Verify phase order
        var phaseList = phases.ToArray();
        output.WriteLine($"Phases recorded: {string.Join(" → ", phaseList)}");

        phaseList.ShouldContain(SessionPhase.Initialising);
        phaseList.ShouldContain(SessionPhase.Cooling);
        phaseList.ShouldContain(SessionPhase.Finalising);

        // Initialising should come before Cooling
        Array.IndexOf(phaseList, SessionPhase.Initialising)
            .ShouldBeLessThan(Array.IndexOf(phaseList, SessionPhase.Cooling),
                "Initialising should precede Cooling");
    }

    [Fact(Timeout = 60_000)]
    public async Task AbortDuringCooling_StopsRampAndWarmsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        // Use winter night so InitialisationAsync succeeds and we reach Cooling
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(3.7886, 24.1167, "M45", null),
                WinterNight,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(30)),
                Gain: 0,
                Offset: 0
            )
        };
        await using var ctx = await CreateWinterSessionAsync(observations, ct);

        var phases = new ConcurrentQueue<SessionPhase>();
        ctx.Session.PhaseChanged += (_, e) =>
        {
            output.WriteLine($"Phase: {e.OldPhase} → {e.NewPhase}");
            phases.Enqueue(e.NewPhase);
        };

        // Cancel once we've been in Cooling for a few ramp steps
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        ctx.Session.PhaseChanged += (_, e) =>
        {
            if (e.NewPhase == SessionPhase.Cooling)
            {
                // Schedule cancellation 3 seconds into cooling (after a few ramp steps)
                ctx.TimeProvider.CreateTimer(
                    _ => cts.Cancel(), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        };

        // Run session: will enter Cooling then get cancelled, and ends once Finalise has warmed the camera
        await RunToEndAsync(ctx, cts.Token, ct);

        var phaseList = phases.ToArray();
        output.WriteLine($"Phases: {string.Join(" → ", phaseList)}");

        // Should have entered Cooling, then Aborted, then Finalising
        phaseList.ShouldContain(SessionPhase.Cooling, "should have entered Cooling phase");
        phaseList.ShouldContain(SessionPhase.Aborted, "should have transitioned to Aborted");
        phaseList.ShouldContain(SessionPhase.Finalising, "should have entered Finalising");

        // After Finalise, cooler should be off (warmup completed)
        var coolerOn = await ctx.Camera.GetCoolerOnAsync(ct);
        output.WriteLine($"Cooler on after finalise: {coolerOn}");

        // CCD temperature should be back near ambient (warmup ramp completed)
        var finalTemp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Final CCD temp: {finalTemp:F1} °C");
        finalTemp.ShouldBeGreaterThan(-5, "CCD should have warmed back toward ambient after Finalise");
    }

    [Fact(Timeout = 60_000)]
    public async Task AbortDuringCooling_CoolingSamplesRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(3.7886, 24.1167, "M45", null),
                WinterNight,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(30)),
                Gain: 0,
                Offset: 0
            )
        };
        await using var ctx = await CreateWinterSessionAsync(observations, ct);

        // Cancel once we've been in Cooling for a few ramp steps
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        ctx.Session.PhaseChanged += (_, e) =>
        {
            if (e.NewPhase == SessionPhase.Cooling)
            {
                ctx.TimeProvider.CreateTimer(
                    _ => cts.Cancel(), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        };

        await RunToEndAsync(ctx, cts.Token, ct);

        // Cooling samples should have been recorded during cooldown and warmup
        var samples = ctx.Session.CoolingSamples;
        output.WriteLine($"Cooling samples: {samples.Length}");
        samples.Length.ShouldBeGreaterThan(0, "should have recorded cooling samples");

        // First sample should be near ambient, later samples should show temperature drop
        var firstTemp = samples[0].TemperatureC;
        output.WriteLine($"First sample temp: {firstTemp:F1} °C");
        firstTemp.ShouldBeGreaterThan(10, "first cooling sample should be near ambient");
    }

    [Fact(Timeout = 60_000)]
    public async Task PhaseChanged_EventFires_WithCorrectOldAndNewPhase()
    {
        var ct = TestContext.Current.CancellationToken;
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(3.7886, 24.1167, "M45", null),
                WinterNight,
                TimeSpan.FromMinutes(3),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(30)),
                Gain: 0,
                Offset: 0
            )
        };
        await using var ctx = await CreateWinterSessionAsync(observations, ct);

        var transitions = new ConcurrentQueue<(SessionPhase Old, SessionPhase New)>();
        ctx.Session.PhaseChanged += (_, e) =>
        {
            transitions.Enqueue((e.OldPhase, e.NewPhase));
        };

        // Cancel quickly: we just need the first few transitions
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        using var cancelTimer = ctx.TimeProvider.CreateTimer(
            _ => cts.Cancel(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        await RunToEndAsync(ctx, cts.Token, ct);

        var transitionList = transitions.ToArray();
        output.WriteLine($"Transitions: {string.Join(", ", Array.ConvertAll(transitionList, t => $"{t.Old}→{t.New}"))}");

        transitionList.Length.ShouldBeGreaterThanOrEqualTo(2, "should have at least 2 transitions");

        // First transition: NotStarted → Initialising
        transitionList[0].Old.ShouldBe(SessionPhase.NotStarted);
        transitionList[0].New.ShouldBe(SessionPhase.Initialising);
    }

    [Fact(Timeout = 120_000)]
    public async Task FocusHistory_PopulatedAfterAutoFocus()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(3.7886, 24.1167, "M45", null),
                WinterNight,
                TimeSpan.FromMinutes(3),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        await using var ctx = await CreateWinterSessionAsync(observations, ct);

        // Stopped once it leaves AutoFocus, so the run never reaches the imaging loop.
        var leftAutoFocus = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        ctx.Session.PhaseChanged += (_, e) =>
        {
            output.WriteLine($"Phase: {e.OldPhase} → {e.NewPhase}");
            if (e.OldPhase == SessionPhase.AutoFocus)
            {
                leftAutoFocus = true;
                cts.Cancel();
            }
        };

        await RunToEndAsync(ctx, cts.Token, ct);

        leftAutoFocus.ShouldBeTrue("the run must get through AutoFocus; the session's log above says where it stopped");

        // A run is recorded only when the V-curve's fit converges (Session.AutoFocusAsync), so leaving the phase proves
        // nothing by itself: it is left the same way when the fit fails. The samples of a fit that failed stay the
        // session's until the next run is recorded, so they are the diagnosis.
        var history = ctx.Session.FocusHistory;
        history.Length.ShouldBe(1,
            $"AutoFocus must converge and record its run; it fitted {ctx.Session.ActiveFocusSamples.Length} samples " +
            $"({string.Join(", ", ctx.Session.ActiveFocusSamples.Select(static s => $"{s.Position}: {s.Hfd:F2}"))})");

        var first = history[0];
        output.WriteLine($"First focus run: OTA={first.OtaName}, Filter={first.FilterName}, Pos={first.BestPosition}, HFD={first.BestHfd:F2}, Curve points={first.Curve.Length}");
        first.BestPosition.ShouldBeGreaterThan(0, "best focus position should be positive");
        first.BestHfd.ShouldBeGreaterThan(0, "best HFD should be positive");
        first.Curve.Length.ShouldBeGreaterThan(0, "focus curve should have sample points");
    }

    [Fact(Timeout = 120_000)]
    public async Task FrameWritten_EventFires_WithExposureLogEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var subExposure = TimeSpan.FromSeconds(30);
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(3.7886, 24.1167, "M45", null),
                WinterNight,
                TimeSpan.FromMinutes(5),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        await using var ctx = await CreateWinterSessionAsync(observations, ct);

        var frameEvents = new ConcurrentQueue<ExposureLogEntry>();
        ctx.Session.FrameWritten += (_, e) =>
        {
            output.WriteLine($"Frame written: {e.Entry.TargetName} {e.Entry.FilterName} #{e.Entry.FrameNumber}");
            frameEvents.Enqueue(e.Entry);
        };

        await RunToEndAsync(ctx, ctx.Token, ct);

        // Verify frames were written
        ctx.Session.TotalFramesWritten.ShouldBeGreaterThan(0, "session should have written frames");
        frameEvents.Count.ShouldBeGreaterThan(0, "FrameWritten event should have fired");

        // Verify exposure log matches events
        var log = ctx.Session.ExposureLog;
        log.Length.ShouldBe(frameEvents.Count, "ExposureLog count should match FrameWritten event count");

        var firstEntry = log[0];
        firstEntry.TargetName.ShouldNotBeNullOrEmpty("target name should be set");
        firstEntry.Exposure.ShouldBeGreaterThan(TimeSpan.Zero, "exposure should be positive");

        output.WriteLine($"Total frames: {log.Length}, first: {firstEntry.TargetName} {firstEntry.FilterName} HFD={firstEntry.MedianHfd:F2} stars={firstEntry.StarCount}");
    }

    /// <summary>
    /// Runs the session to its end on the cooperative pump (<c>docs/architecture/session-test-harness.md</c>): the clock
    /// moves only once the run is parked on it, and the budget bounds a STALL, read off the run's phases and the frames
    /// it takes, never the run's length. These tests used to sleep on the clock from the test thread beside the run,
    /// which advanced it whether or not the run had been scheduled (#1017). Each advance goes to the next instant the run
    /// waits for (#1122), so no test chooses a step: they used to, trading a whole run's real time against how late a
    /// fine poll woke (a 1 s step cost a minute, a 30 s one let a 100 ms focuser poll oversleep by 30 s).
    /// </summary>
    private async Task RunToEndAsync(SessionTestContext ctx, CancellationToken runToken, CancellationToken ct)
    {
        var phaseChanges = 0;
        ctx.Session.PhaseChanged += (_, _) => Interlocked.Increment(ref phaseChanges);

        ctx.TimeProvider.ExternalTimePump = true;
        try
        {
            var runTask = ctx.Track(Task.Run(async () => await ctx.Session.RunAsync(runToken), ctx.Token));
            var pumped = await ctx.TimeProvider.PumpUntilCompletedAsync(runTask, TimeSpan.FromHours(1),
                progress: () => Volatile.Read(ref phaseChanges) * 1_000_000L + ctx.Session.LastCapturedImageNumber(0),
                cancellationToken: ct);
            await runTask;
            output.WriteLine($"Pumped {pumped} of fake time");
        }
        finally
        {
            ctx.TimeProvider.ExternalTimePump = false;
        }
    }

    /// <summary>
    /// Creates a session configured for a winter night (astro dark already started)
    /// with the focuser at best focus so RoughFocus/AutoFocus can succeed.
    /// </summary>
    private async Task<SessionTestContext> CreateWinterSessionAsync(
        ScheduledObservation[] observations, CancellationToken ct)
    {
        var timeProvider = new FakeTimeProviderWrapper(WinterNight);
        var external = new FakeExternal(output, timeProvider);
        // The session's own log in the test output, so a failure says what the run did (#1017).
        var sp = external.BuildServiceProvider(logTo: output);
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        var camera = new Camera(cameraDevice, sp);
        var focuser = new Focuser(focuserDevice, sp);

        await camera.Driver.ConnectAsync(ct);
        await focuser.Driver.ConnectAsync(ct);

        var cameraDriver = (FakeCameraDriver)camera.Driver;
        cameraDriver.BinX = 1;
        cameraDriver.NumX = 512;
        cameraDriver.NumY = 512;
        cameraDriver.TrueBestFocus = 1000;
        cameraDriver.FocusPosition = 1000;

        var focuserDriver = (FakeFocuserDriver)focuser.Driver;
        await focuserDriver.BeginMoveAsync(1000, ct);

        var ota = new OTA("Test Telescope", 1000, camera, Cover: null, focuser,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true),
            FilterWheel: null, Switches: null);

        var mountDevice = new FakeDevice(DeviceType.Mount, 1,
            new System.Collections.Specialized.NameValueCollection
            {
                { "latitude", "48.2" },
                { "longitude", "16.3" }
            });
        var guiderDevice = new FakeDevice(DeviceType.Guider, 1);
        var mount = new Mount(mountDevice, sp);
        var guider = new Guider(guiderDevice, sp);

        // Don't pre-connect mount/guider: InitialisationAsync handles it
        var setup = new Setup(mount, guider, new GuiderSetup(), [ota]);
        var plateSolver = new FakePlateSolver();
        var config = SessionTestHelper.DefaultConfiguration;

        var session = new Session(setup, config, plateSolver, external, sp, new ScheduledObservationTree(observations));

        return new SessionTestContext(session, external, timeProvider, cameraDriver, focuserDriver, mount.Driver, TestCancellation: ct);
    }
}
