using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
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
[Collection("Session")]
public class SessionLifecycleTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset WinterNight = new(2025, 12, 15, 22, 0, 0, TimeSpan.Zero);

    // --- SessionEndTimeAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenWinterNightWhenSessionEndTimeThenReturnsNextMorningTwilight()
    {
        // Dec 15, 22:00 UTC from Vienna — astronomical twilight rise on Dec 16 ~05:00–06:00 UTC
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        var startTime = ctx.External.TimeProvider.GetUtcNow().UtcDateTime;
        var endTime = await ctx.Session.SessionEndTimeAsync(startTime, ct);

        endTime.ShouldBeGreaterThan(startTime);
        var duration = endTime - startTime;
        duration.TotalHours.ShouldBeGreaterThan(5, "winter night should be longer than 5 hours");
        duration.TotalHours.ShouldBeLessThan(12, "but shorter than 12 hours");

        output.WriteLine($"Start: {startTime:u}, End: {endTime:u}, Duration: {duration}");
    }

    // --- WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenObservationAlreadyStartedWhenWaitForDarkThenReturnsImmediately()
    {
        // Observation started 30 minutes before the session's "now" — should skip immediately
        var ct = TestContext.Current.CancellationToken;
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(3.7886, 24.1167, "M45", null),
                WinterNight - TimeSpan.FromMinutes(30), // started 30 min ago
                TimeSpan.FromMinutes(60),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(120)),
                Gain: 0,
                Offset: 0
            )
        };
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, observations: observations, cancellationToken: ct);

        var timeBefore = ctx.External.TimeProvider.GetUtcNow();

        await ctx.Session.WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(ct);

        var timeAfter = ctx.External.TimeProvider.GetUtcNow();
        var elapsed = timeAfter - timeBefore;
        elapsed.TotalSeconds.ShouldBeLessThan(1, "should return immediately when observation already started");
    }

    // --- CoolCamerasToAmbientAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenCooledCameraWhenCoolToAmbientThenReturnsSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        // Cool camera down first using thresPower=80 (same as RunAsync) to fully ramp
        await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            TimeSpan.FromSeconds(60), 80, SetupointDirection.Down, ct);

        var tempAfterCooldown = await ctx.Camera.GetCCDTemperatureAsync(ct);
        tempAfterCooldown.ShouldBeLessThan(0, "camera should be cooled below 0°C");

        // CoolCamerasToAmbientAsync warms back to heatsink temp (20°C)
        var result = await ctx.Session.CoolCamerasToAmbientAsync(TimeSpan.FromSeconds(60));

        result.ShouldBeTrue("ambient warmup should report success");

        var ambientTemp = await ctx.Camera.GetHeatSinkTemperatureAsync(ct);
        output.WriteLine($"After cooldown: {tempAfterCooldown:F1}°C, ambient={ambientTemp}°C");
    }

    // --- CalibrateGuiderAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenConnectedMountWhenCalibrateGuiderThenSlewsAndStartsGuiding()
    {
        // Use winter night when dec=0 near meridian is well above horizon from Vienna
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // Run calibration — slews 30 min east of meridian at dec=0, then starts guiding
        var calibrateTask = Task.Run(async () => await ctx.Session.CalibrateGuiderAsync(ct), ct);

        while (!calibrateTask.IsCompleted && !ct.IsCancellationRequested)
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

    [Fact(Timeout = 120_000)]
    public async Task GivenFreshSessionWhenInitialisationThenAllDevicesConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        // Run initialisation — connects, unparks (no-op), sets UTC, cools to sensor temp, opens covers (null=ok)
        var initTask = Task.Run(async () => await ctx.Session.InitialisationAsync(ct), ct);

        while (!initTask.IsCompleted && !ct.IsCancellationRequested)
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

    [Fact(Timeout = 120_000)]
    public async Task GivenActiveSessionWhenFinaliseThenShutdownCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

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

        while (!finaliseTask.IsCompleted && !ct.IsCancellationRequested)
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

    // --- MoveTelescopeCoversToStateAsync ---

    /// <summary>
    /// Creates a Session whose OTA has a FakeCoverDriver attached.
    /// </summary>
    private async Task<(Session Session, FakeExternal External, FakeCoverDriver Cover)> CreateSessionWithCoverAsync(CancellationToken ct)
    {
        var external = new FakeExternal(output, now: WinterNight);
        var sp = external.BuildServiceProvider();
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        var coverDevice = new FakeDevice(DeviceType.CoverCalibrator, 1);

        var camera = new Camera(cameraDevice, sp);
        var focuser = new Focuser(focuserDevice, sp);
        var cover = new Cover(coverDevice, sp);

        await camera.Driver.ConnectAsync(ct);
        await focuser.Driver.ConnectAsync(ct);

        var cameraDriver = (FakeCameraDriver)camera.Driver;
        cameraDriver.BinX = 1;
        cameraDriver.NumX = 512;
        cameraDriver.NumY = 512;

        var ota = new OTA("Test Telescope", 1000, camera, cover, focuser,
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
        var mount = new Mount(mountDevice, sp);
        var guider = new Guider(guiderDevice, sp);

        await mount.Driver.ConnectAsync(ct);
        await guider.Driver.ConnectAsync(ct);
        await ((FakeGuider)guider.Driver).ConnectEquipmentAsync(ct);
        await mount.Driver.SetUTCDateAsync(external.TimeProvider.GetUtcNow().UtcDateTime, ct);

        var setup = new Setup(mount, guider, new GuiderSetup(), [ota]);
        var config = SessionTestHelper.DefaultConfiguration;
        var session = new Session(setup, config, new FakePlateSolver(), external, new ScheduledObservationTree(SessionTestHelper.DefaultScheduledObservations));

        var coverDriver = (FakeCoverDriver)cover.Driver;
        return (session, external, coverDriver);
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenClosedCoverWhenOpenThenCoverOpens()
    {
        var ct = TestContext.Current.CancellationToken;
        var (session, external, coverDriver) = await CreateSessionWithCoverAsync(ct);

        // Cover starts Closed
        (await coverDriver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.Closed);

        // Open covers — needs time pump for the Moving → Open transition and calibrator check
        var openTask = Task.Run(async () => await session.MoveTelescopeCoversToStateAsync(CoverStatus.Open, ct), ct);

        while (!openTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await external.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        openTask.IsCompleted.ShouldBeTrue("MoveTelescopeCoversToStateAsync should complete within timeout");
        var result = await openTask;

        result.ShouldBeTrue("opening covers should succeed");
        (await coverDriver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.Open);

        output.WriteLine("Cover opened successfully");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenOpenCoverWhenCloseThenCoverCloses()
    {
        var ct = TestContext.Current.CancellationToken;
        var (session, external, coverDriver) = await CreateSessionWithCoverAsync(ct);

        // Open the cover first
        await coverDriver.BeginOpen(ct);
        await external.SleepAsync(TimeSpan.FromSeconds(10), ct); // let timer fire
        (await coverDriver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.Open);

        // Close covers
        var closeTask = Task.Run(async () => await session.MoveTelescopeCoversToStateAsync(CoverStatus.Closed, ct), ct);

        while (!closeTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await external.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        closeTask.IsCompleted.ShouldBeTrue("MoveTelescopeCoversToStateAsync should complete within timeout");
        var result = await closeTask;

        result.ShouldBeTrue("closing covers should succeed");
        (await coverDriver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.Closed);

        output.WriteLine("Cover closed successfully");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenCalibratorOnWhenOpenCoverThenCalibratorTurnedOffFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var (session, external, coverDriver) = await CreateSessionWithCoverAsync(ct);

        // Turn calibrator on
        await coverDriver.BeginCalibratorOn(128, ct);
        (await coverDriver.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Ready);
        (await coverDriver.GetBrightnessAsync(ct)).ShouldBe(128);

        // Open cover — should turn off calibrator first, then open
        var openTask = Task.Run(async () => await session.MoveTelescopeCoversToStateAsync(CoverStatus.Open, ct), ct);

        while (!openTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await external.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        openTask.IsCompleted.ShouldBeTrue("should complete within timeout");
        var result = await openTask;

        result.ShouldBeTrue("opening should succeed");
        (await coverDriver.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Off, "calibrator should be off after opening");
        (await coverDriver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.Open);

        output.WriteLine("Calibrator turned off and cover opened");
    }

    // --- GuiderFocusLoopAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenConnectedGuiderWhenGuiderFocusLoopThenPlateSolveSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

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

    [Fact(Timeout = 120_000)]
    public async Task GivenSyntheticStarsWhenInitialRoughFocusThenDetectsStars()
    {
        var ct = TestContext.Current.CancellationToken;
        // LX200 mount needed: InitialRoughFocusAsync slews internally via WaitForSlewCompleteAsync
        // which requires the serial protocol's timer-based slew to interleave with SleepAsync pumping.
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, mountPort: "LX200", cancellationToken: ct);

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

        while (!roughFocusTask.IsCompleted && !ct.IsCancellationRequested)
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

    [Fact(Timeout = 120_000)]
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
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(subExposure),
                Gain: 0,
                Offset: 0
            )
        };

        // Use fresh session — RunAsync handles all setup internally
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
        var mount = new Mount(mountDevice, sp);
        var guider = new Guider(guiderDevice, sp);

        var setup = new Setup(mount, guider, new GuiderSetup(), [ota]);
        var plateSolver = new FakePlateSolver();
        var config = SessionTestHelper.DefaultConfiguration;

        var session = new Session(setup, config, plateSolver, external, new ScheduledObservationTree(observations));

        // Move focuser to best focus before RunAsync
        var focuserDriver = (FakeFocuserDriver)focuser.Driver;
        await focuserDriver.BeginMoveAsync(1000, ct);

        // RunAsync on background thread, pump time from test thread
        var runTask = Task.Run(async () => await session.RunAsync(ct), ct);

        var maxPumps = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxPumps && !runTask.IsCompleted && !ct.IsCancellationRequested; i++)
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
        var outputDir = external.AppDataFolder;
        if (outputDir.Exists)
        {
            foreach (var file in outputDir.GetFiles("*", System.IO.SearchOption.AllDirectories))
            {
                file.Delete();
            }
        }
    }

    // --- GetMountUtcNowAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenConnectedMountWhenGetMountUtcNowThenReturnsTimeProviderTime()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        var mountTime = await ctx.Session.GetMountUtcNowAsync(ct);
        var providerTime = ctx.External.TimeProvider.GetUtcNow().UtcDateTime;

        // Mount time should be close to the time provider's time (may differ by serial round-trip)
        Math.Abs((mountTime - providerTime).TotalSeconds).ShouldBeLessThan(5);

        // Advance time and verify mount time follows
        await ctx.External.SleepAsync(TimeSpan.FromMinutes(10), ct);
        var mountTimeAfter = await ctx.Session.GetMountUtcNowAsync(ct);
        mountTimeAfter.ShouldBeGreaterThan(mountTime);

        output.WriteLine($"Mount time: {mountTime:u} → {mountTimeAfter:u}");
    }

    // --- WriteImageToFitsFileAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenImageWhenWriteToFitsThenFileCreatedOnDisk()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        // Create a small synthetic image with valid metadata (Filter.Name must not be empty)
        var array = SyntheticStarFieldRenderer.Render(64, 64, defocusSteps: 0, exposureSeconds: 1, noiseSeed: 42);
        var meta = new ImageMeta("FakeCamera", WinterNight, TimeSpan.FromSeconds(30), FrameType.Light,
            "TestTelescope", 3.8f, 3.8f, 1000, 1000, Filter.Unknown, 1, 1, -10f, SensorType.Monochrome, 0, 0, RowOrder.TopDown, 48.2f, 16.3f);
        var image = new Image([array], BitDepth.Float32, 1f, 0f, 0f, meta);

        var observation = new ScheduledObservation(
            new Target(5.0, 20.0, "TestTarget", null),
            WinterNight, TimeSpan.FromMinutes(5),
            AcrossMeridian: false, FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(30)), Gain: 0, Offset: 0);

        var imageWrite = new QueuedImageWrite(image, observation, WinterNight, 1, TimeSpan.FromSeconds(30), CameraIndex: 0);
        await ctx.Session.WriteImageToFitsFileAsync(imageWrite);

        // Verify the FITS file was created
        var outputDir = ctx.External.ImageOutputFolder;
        var fitsFiles = outputDir.GetFiles("*.fits", System.IO.SearchOption.AllDirectories);
        fitsFiles.Length.ShouldBeGreaterThan(0, "should have written at least one FITS file");

        output.WriteLine($"FITS written: {fitsFiles[0].FullName} ({fitsFiles[0].Length} bytes)");
    }

    // --- EstimateTimeUntilTargetRisesAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenRisingTargetWhenEstimateRiseTimeThenReturnsPositiveTimeSpan()
    {
        // At Dec 15 22:00 UTC from Vienna, Seagull Nebula (RA=7.06, Dec=-10.45) is low and rising.
        // It should clear 30° within a few hours.
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        var target = new Target(7.06, -10.45, "SeagullNebula", null);
        var result = await ctx.Session.EstimateTimeUntilTargetRisesAsync(target, 30, TimeSpan.FromHours(4), ct);

        result.ShouldNotBeNull("rising target should have an estimated rise time");
        result.Value.TotalMinutes.ShouldBeGreaterThan(0, "should need some time to rise above 30°");
        result.Value.TotalHours.ShouldBeLessThan(4, "should rise within the 4-hour lookahead");

        output.WriteLine($"Seagull Nebula rises above 20° in {result.Value.TotalMinutes:F0} minutes");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenSettingTargetWhenEstimateRiseTimeThenReturnsNull()
    {
        // At Dec 15 22:00 UTC from Vienna, M45 (RA=3.79, Dec=24.12) is at alt ~64° and setting.
        // It won't rise — it's past the meridian and descending.
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        var target = new Target(3.79, 24.12, "M45", null);
        var result = await ctx.Session.EstimateTimeUntilTargetRisesAsync(target, 70, TimeSpan.FromHours(2), ct);

        result.ShouldBeNull("setting target should return null (not rising)");

        output.WriteLine("M45 is setting — correctly returned null");
    }
}
