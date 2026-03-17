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
/// Bottom-up tests for Session lifecycle, timing, and calibration methods.
/// All tests use the standard fake devices with Cover=null (no cover driver needed).
/// Site: Vienna 48.2°N, 16.3°E.
/// Winter tests use Dec 15 22:00 UTC (long, unambiguous astronomical night).
/// </summary>
public class SessionLifecycleTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset WinterNight = new(2025, 12, 15, 22, 0, 0, TimeSpan.Zero);

    // --- SessionEndTimeAsync ---

    [Fact]
    public async Task GivenWinterNightWhenSessionEndTimeThenReturnsNextMorningTwilight()
    {
        // Dec 15, 22:00 UTC from Vienna — astronomical twilight rise on Dec 16 ~05:00–06:00 UTC
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        var startTime = ctx.External.TimeProvider.GetUtcNow().UtcDateTime;
        var endTime = await ctx.Session.SessionEndTimeAsync(startTime, ct);

        endTime.ShouldBeGreaterThan(startTime);
        var duration = endTime - startTime;
        duration.TotalHours.ShouldBeGreaterThan(5, "winter night should be longer than 5 hours");
        duration.TotalHours.ShouldBeLessThan(12, "but shorter than 12 hours");

        output.WriteLine($"Start: {startTime:u}, End: {endTime:u}, Duration: {duration}");
    }

    // --- WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync ---

    [Fact]
    public async Task GivenTwilightAlreadyEndedWhenWaitForTwilightThenReturnsImmediately()
    {
        // Dec 15, 22:00 UTC from Vienna — amateur astronomical twilight ends ~17:00 UTC in winter
        // At 22:00 UTC it has long ended
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        var timeBefore = ctx.External.TimeProvider.GetUtcNow();

        await ctx.Session.WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(ct);

        var timeAfter = ctx.External.TimeProvider.GetUtcNow();
        var elapsed = timeAfter - timeBefore;
        elapsed.TotalSeconds.ShouldBeLessThan(1, "should return immediately when twilight has already ended");
    }

    // --- CoolCamerasToAmbientAsync ---

    [Fact]
    public async Task GivenCooledCameraWhenCoolToAmbientThenReturnsSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        // Cool camera down first using thresPower=80 (same as RunAsync) to fully ramp
        await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            TimeSpan.FromSeconds(1), 80, SetupointDirection.Down, ct);

        var tempAfterCooldown = await ctx.Camera.GetCCDTemperatureAsync(ct);
        tempAfterCooldown.ShouldBeLessThan(0, "camera should be cooled below 0°C");

        // CoolCamerasToAmbientAsync uses SetpointTempKind.Ambient which targets current CCD temp
        // (stepwise warmup approach — each iteration sets setpoint = ccdTemp, warming happens
        // naturally as the cooler reduces power). It should complete successfully.
        var result = await ctx.Session.CoolCamerasToAmbientAsync(TimeSpan.FromSeconds(1));

        result.ShouldBeTrue("ambient warmup should report success");

        var ambientTemp = await ctx.Camera.GetHeatSinkTemperatureAsync(ct);
        output.WriteLine($"After cooldown: {tempAfterCooldown:F1}°C, ambient={ambientTemp}°C");
    }

    // --- CalibrateGuiderAsync ---

    [Fact]
    public async Task GivenConnectedMountWhenCalibrateGuiderThenSlewsAndStartsGuiding()
    {
        // Use winter night when dec=0 near meridian is well above horizon from Vienna
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // Run calibration — slews 30 min east of meridian at dec=0, then starts guiding
        var calibrateTask = Task.Run(async () => await ctx.Session.CalibrateGuiderAsync(ct), ct);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!calibrateTask.IsCompleted && !timeout.IsCancellationRequested)
        {
            await ctx.External.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        calibrateTask.IsCompleted.ShouldBeTrue("CalibrateGuiderAsync should complete within timeout");
        await calibrateTask; // propagate any exceptions

        // After calibration, guider should be guiding
        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        (await guider.IsGuidingAsync(ct)).ShouldBeTrue("guider should be guiding after calibration");

        output.WriteLine("Guider calibration completed successfully");
    }

    // --- InitialisationAsync ---

    [Fact]
    public async Task GivenFreshSessionWhenInitialisationThenAllDevicesConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        // Run initialisation — connects, unparks (no-op), sets UTC, cools to sensor temp, opens covers (null=ok)
        var initTask = Task.Run(async () => await ctx.Session.InitialisationAsync(ct), ct);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!initTask.IsCompleted && !timeout.IsCancellationRequested)
        {
            await ctx.External.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        initTask.IsCompleted.ShouldBeTrue("InitialisationAsync should complete within timeout");
        var result = await initTask;

        result.ShouldBeTrue("initialisation should succeed");

        // Verify denormalized properties were set
        ctx.Camera.Telescope.ShouldBe("Test Telescope");
        ctx.Camera.FocalLength.ShouldBe(1000);
        ctx.Camera.Latitude.ShouldNotBeNull();
        ctx.Camera.Longitude.ShouldNotBeNull();

        output.WriteLine($"Camera: telescope={ctx.Camera.Telescope}, FL={ctx.Camera.FocalLength}, lat={ctx.Camera.Latitude}, lon={ctx.Camera.Longitude}");
    }

    // --- Finalise ---

    [Fact]
    public async Task GivenActiveSessionWhenFinaliseThenShutdownCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // Start guiding so Finalise has something to stop
        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        await guider.GuideAsync(0.3, 3, 30, ct);
        await ctx.External.SleepAsync(TimeSpan.FromSeconds(25), ct); // let guider settle

        (await guider.IsGuidingAsync(ct)).ShouldBeTrue("guider should be guiding before finalise");
        (await mount.IsTrackingAsync(ct)).ShouldBeTrue("mount should be tracking before finalise");

        // Run finalise with time pump
        var finaliseTask = Task.Run(async () => await ctx.Session.Finalise(ct), ct);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!finaliseTask.IsCompleted && !timeout.IsCancellationRequested)
        {
            await ctx.External.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        finaliseTask.IsCompleted.ShouldBeTrue("Finalise should complete within timeout");
        await finaliseTask; // propagate any exceptions

        // After finalise, guider should be stopped
        (await guider.IsGuidingAsync(ct)).ShouldBeFalse("guider should not be guiding after finalise");

        // Mount should be disconnected
        mount.Connected.ShouldBeFalse("mount should be disconnected after finalise");

        output.WriteLine("Finalise shutdown completed");
    }

    // --- GuiderFocusLoopAsync ---

    [Fact]
    public async Task GivenConnectedGuiderWhenGuiderFocusLoopThenPlateSolveSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        // Set guider pointing so SaveImageAsync writes WCS headers
        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        var mountRa = await ctx.Mount.GetRightAscensionAsync(ct);
        var mountDec = await ctx.Mount.GetDeclinationAsync(ct);
        guider.PointingRA = mountRa;
        guider.PointingDec = mountDec;

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // GuiderFocusLoopAsync: LoopAsync → SaveImageAsync (FITS with WCS + stars) → SolveFileAsync
        var result = await ctx.Session.GuiderFocusLoopAsync(TimeSpan.FromMinutes(1), ct);

        result.ShouldBeTrue("guider focus loop should succeed (FITS with WCS → plate solve)");

        output.WriteLine($"Guider focus loop succeeded — plate solved at RA={mountRa:F4}h, Dec={mountDec:F2}°");
    }

    // --- InitialRoughFocusAsync ---

    [Fact]
    public async Task GivenSyntheticStarsWhenInitialRoughFocusThenDetectsStars()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        // Enable synthetic star field rendering at best focus
        ctx.Camera.TrueBestFocus = 1000;
        ctx.Camera.FocusPosition = 1000;

        await ctx.Focuser.BeginMoveAsync(1000, ct);
        while (await ctx.Focuser.GetIsMovingAsync(ct))
        {
            await ctx.External.SleepAsync(TimeSpan.FromMilliseconds(100), ct);
        }

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // InitialRoughFocusAsync: slews to zenith, guider plate solve, then takes
        // short exposures looking for ≥15 stars. With synthetic star field at best
        // focus (1000mm FL, 512×512 sensor), we should detect enough stars.
        var roughFocusTask = Task.Run(
            async () => await ctx.Session.InitialRoughFocusAsync(ct), ct);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!roughFocusTask.IsCompleted && !timeout.IsCancellationRequested)
        {
            await ctx.External.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        roughFocusTask.IsCompleted.ShouldBeTrue("InitialRoughFocusAsync should complete within timeout");
        var result = await roughFocusTask;

        result.ShouldBeTrue("rough focus should succeed with synthetic star field at best focus");

        output.WriteLine("Initial rough focus completed — enough stars detected");
    }

    // --- RunAsync (full end-to-end) ---

    [Fact]
    public async Task GivenWinterNightWithSingleTargetWhenRunAsyncThenFullSessionCompletes()
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
                SubExposure: subExposure,
                Gain: 0,
                Offset: 0
            )
        };

        // Use fresh session — RunAsync handles all setup internally
        var external = new FakeExternal(output, now: WinterNight);
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        var camera = new Camera(cameraDevice, external);
        var focuser = new Focuser(focuserDevice, external);

        await camera.Driver.ConnectAsync(ct);
        await focuser.Driver.ConnectAsync(ct);

        var cameraDriver = (FakeCameraDriver)camera.Driver;
        cameraDriver.BinX = 1;
        cameraDriver.NumX = 512;
        cameraDriver.NumY = 512;
        cameraDriver.TrueBestFocus = 1000;
        cameraDriver.FocusPosition = 1000;

        var ota = new OTA("Test Telescope", 1000, camera, Cover: null, focuser,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true),
            FilterWheel: null, Switches: null);

        var mountDevice = new FakeDevice(DeviceType.Mount, 1,
            new System.Collections.Specialized.NameValueCollection
            {
                { "port", "LX200" },
                { "latitude", "48.2" },
                { "longitude", "16.3" }
            });
        var guiderDevice = new FakeDevice(DeviceType.Guider, 1);
        var mount = new Mount(mountDevice, external);
        var guider = new Guider(guiderDevice, external);

        var setup = new Setup(mount, guider, new GuiderSetup(), [ota]);
        var plateSolver = new FakePlateSolver();
        var config = SessionTestHelper.DefaultConfiguration;

        var session = new Session(setup, config, plateSolver, external, new ScheduledObservationTree(observations));

        // Move focuser to best focus before RunAsync
        var focuserDriver = (FakeFocuserDriver)focuser.Driver;
        await focuserDriver.BeginMoveAsync(1000, ct);

        // RunAsync on background thread, pump time from test thread
        var runTask = Task.Run(async () => await session.RunAsync(ct), ct);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var maxPumps = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxPumps && !runTask.IsCompleted && !timeout.IsCancellationRequested; i++)
        {
            await external.SleepAsync(subExposure, ct);
            await Task.Delay(50, ct);
        }

        runTask.IsCompleted.ShouldBeTrue("RunAsync should complete within timeout");
        await runTask; // propagate any exceptions

        // Verify the session produced frames
        session.TotalFramesWritten.ShouldBeGreaterThan(0, "session should have written at least one frame");

        // Mount should be disconnected after Finalise
        mount.Driver.Connected.ShouldBeFalse("mount should be disconnected after session");

        output.WriteLine($"Full session completed: {session.TotalFramesWritten} frames, {session.TotalExposureTime} exposure time");

        // Cleanup
        var outputDir = external.OutputFolder;
        if (outputDir.Exists)
        {
            foreach (var file in outputDir.GetFiles("*", System.IO.SearchOption.AllDirectories))
            {
                file.Delete();
            }
        }
    }
}
