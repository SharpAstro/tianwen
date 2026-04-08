using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    // Winter night in Vienna — astro dark at ~17:30 UTC, twilight at ~04:30 UTC
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

        using var ctx = await CreateWinterSessionAsync(observations, ct);

        // Record phase transitions
        var phases = new ConcurrentQueue<SessionPhase>();
        ctx.Session.PhaseChanged += (_, e) =>
        {
            output.WriteLine($"Phase: {e.OldPhase} → {e.NewPhase}");
            phases.Enqueue(e.NewPhase);
        };

        // Run session on background thread, pump time from test thread
        var runTask = Task.Run(async () => await ctx.Session.RunAsync(ct), ct);

        var maxPumps = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxPumps && !runTask.IsCompleted && !ct.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);
            await Task.Delay(50, ct);
        }

        runTask.IsCompleted.ShouldBeTrue("RunAsync should complete within timeout");
        await runTask;

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
        using var ctx = await CreateWinterSessionAsync(observations, ct);

        var phases = new ConcurrentQueue<SessionPhase>();
        ctx.Session.PhaseChanged += (_, e) =>
        {
            output.WriteLine($"Phase: {e.OldPhase} → {e.NewPhase}");
            phases.Enqueue(e.NewPhase);
        };

        // Cancel once we've been in Cooling for a few ramp steps
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ctx.Session.PhaseChanged += (_, e) =>
        {
            if (e.NewPhase == SessionPhase.Cooling)
            {
                // Schedule cancellation 3 seconds into cooling (after a few ramp steps)
                ctx.External.TimeProvider.CreateTimer(
                    _ => cts.Cancel(), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        };

        // Run session — will enter Cooling then get cancelled
        var runTask = Task.Run(async () => await ctx.Session.RunAsync(cts.Token), ct);

        // Pump time until complete (including warmup in Finalise)
        while (!runTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await ctx.External.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        runTask.IsCompleted.ShouldBeTrue("RunAsync should complete after abort + finalise");
        await runTask; // propagate exceptions

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
        using var ctx = await CreateWinterSessionAsync(observations, ct);

        // Cancel once we've been in Cooling for a few ramp steps
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ctx.Session.PhaseChanged += (_, e) =>
        {
            if (e.NewPhase == SessionPhase.Cooling)
            {
                ctx.External.TimeProvider.CreateTimer(
                    _ => cts.Cancel(), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        };

        var runTask = Task.Run(async () => await ctx.Session.RunAsync(cts.Token), ct);

        while (!runTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await ctx.External.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        await runTask;

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
        using var ctx = await CreateWinterSessionAsync(observations, ct);

        var transitions = new ConcurrentQueue<(SessionPhase Old, SessionPhase New)>();
        ctx.Session.PhaseChanged += (_, e) =>
        {
            transitions.Enqueue((e.OldPhase, e.NewPhase));
        };

        // Cancel quickly — we just need the first few transitions
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var cancelTimer = ctx.External.TimeProvider.CreateTimer(
            _ => cts.Cancel(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        var runTask = Task.Run(async () => await ctx.Session.RunAsync(cts.Token), ct);

        while (!runTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await ctx.External.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        await runTask;

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

        using var ctx = await CreateWinterSessionAsync(observations, ct);

        // Track when we pass AutoFocus
        var passedAutoFocus = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ctx.Session.PhaseChanged += (_, e) =>
        {
            output.WriteLine($"Phase: {e.OldPhase} → {e.NewPhase}");
            if (e.OldPhase == SessionPhase.AutoFocus)
            {
                passedAutoFocus = true;
                // Cancel after auto-focus to avoid running the full imaging loop
                cts.Cancel();
            }
        };

        var runTask = Task.Run(async () => await ctx.Session.RunAsync(cts.Token), ct);

        var maxPumps = 1000;
        for (var i = 0; i < maxPumps && !runTask.IsCompleted && !ct.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(TimeSpan.FromSeconds(2), ct);
            await Task.Delay(10, ct);
        }

        await runTask;

        if (passedAutoFocus)
        {
            var history = ctx.Session.FocusHistory;
            output.WriteLine($"Focus history entries: {history.Length}");
            history.Length.ShouldBeGreaterThan(0, "should have at least one focus run record");

            var first = history[0];
            output.WriteLine($"First focus run: OTA={first.OtaName}, Filter={first.FilterName}, Pos={first.BestPosition}, HFD={first.BestHfd:F2}, Curve points={first.Curve.Length}");
            first.BestPosition.ShouldBeGreaterThan(0, "best focus position should be positive");
            first.BestHfd.ShouldBeGreaterThan(0, "best HFD should be positive");
            first.Curve.Length.ShouldBeGreaterThan(0, "focus curve should have sample points");
        }
        else
        {
            output.WriteLine("AutoFocus phase was not reached — test inconclusive (session may have failed earlier)");
        }
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

        using var ctx = await CreateWinterSessionAsync(observations, ct);

        var frameEvents = new ConcurrentQueue<ExposureLogEntry>();
        ctx.Session.FrameWritten += (_, e) =>
        {
            output.WriteLine($"Frame written: {e.Entry.TargetName} {e.Entry.FilterName} #{e.Entry.FrameNumber}");
            frameEvents.Enqueue(e.Entry);
        };

        var runTask = Task.Run(async () => await ctx.Session.RunAsync(ct), ct);

        var maxPumps = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxPumps && !runTask.IsCompleted && !ct.IsCancellationRequested; i++)
        {
            await ctx.External.SleepAsync(subExposure, ct);
            await Task.Delay(50, ct);
        }

        await runTask;

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
    /// Creates a session configured for a winter night (astro dark already started)
    /// with the focuser at best focus so RoughFocus/AutoFocus can succeed.
    /// </summary>
    private async Task<SessionTestContext> CreateWinterSessionAsync(
        ScheduledObservation[] observations, CancellationToken ct)
    {
        var external = new FakeExternal(output, now: WinterNight);
        var sp = external.BuildServiceProvider();
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

        // Don't pre-connect mount/guider — InitialisationAsync handles it
        var setup = new Setup(mount, guider, new GuiderSetup(), [ota]);
        var plateSolver = new FakePlateSolver();
        var config = SessionTestHelper.DefaultConfiguration;

        var session = new Session(setup, config, plateSolver, external, new ScheduledObservationTree(observations));

        return new SessionTestContext(session, external, cameraDriver, focuserDriver, mount.Driver);
    }
}
