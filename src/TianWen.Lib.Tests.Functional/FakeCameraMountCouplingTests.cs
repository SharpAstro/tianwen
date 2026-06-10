using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Part 2 of the fake-mount misalignment work: the guide camera couples its
/// rendered star field to the connected mount's misalignment drift. The camera
/// self-resolves the mount from the device hub (mirroring how it self-resolves
/// the catalog DB), so the session / shared layer stays unaware of the coupling.
/// These tests exercise the real <see cref="DeviceHub"/> resolution path.
/// </summary>
public class FakeCameraMountCouplingTests(ITestOutputHelper output)
{
    private const double TestAzMisalignArcmin = 30.0;
    private const double TestAltMisalignArcmin = -10.0;

    /// <summary>
    /// Builds a service provider that registers a real <see cref="DeviceHub"/> so a
    /// camera connected through it can self-resolve the connected mount. The hub is
    /// constructed by DI with this same provider, so the camera's
    /// <see cref="FakeDeviceDriverBase.ServiceProvider"/> resolves the very same hub.
    /// </summary>
    private ServiceProvider BuildHubServiceProvider(FakeExternal external) =>
        new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(external.TimeProvider)
            // Route driver logs to the test output so the new GuideLoop telemetry is visible
            // when running this scenario headlessly. Keep the per-frame FakeCamera "rendering
            // N stars" Debug spam out of the way (Information) so the guide telemetry reads cleanly.
            .AddLogging(b => b
                .SetMinimumLevel(LogLevel.Debug)
                .AddFilter("FakeDevice", LogLevel.Information)
                .AddProvider(new XUnitLoggerProvider(output, false)))
            .AddSingleton<IDeviceHub, DeviceHub>()
            .BuildServiceProvider();

    private static async Task<IMountDriver> ConnectMisalignedMountAsync(IDeviceHub hub, CancellationToken ct,
        double azArcmin = TestAzMisalignArcmin, double altArcmin = TestAltMisalignArcmin)
    {
        var mountDevice = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "port", "SkyWatcher" },
            { "latitude", "48.2" },
            { "longitude", "16.3" },
            { "polarMisalignmentAzArcmin", azArcmin.ToString() },
            { "polarMisalignmentAltArcmin", altArcmin.ToString() }
        });
        var mount = (IMountDriver)await hub.ConnectAsync(mountDevice, ct);
        await mount.SetSiteLatitudeAsync(48.2, ct);
        await mount.SetSiteLongitudeAsync(16.3, ct);
        return mount;
    }

    /// <summary>
    /// Arm-and-abort an exposure: this triggers the async mount-pointing snapshot in
    /// <see cref="FakeCameraDriver.StartExposureAsync"/> without paying for a rendered
    /// frame (the drift offset is what we assert on, not the pixels).
    /// </summary>
    private static async Task SnapshotPointingAsync(FakeCameraDriver camera, CancellationToken ct)
    {
        await camera.StartExposureAsync(TimeSpan.FromSeconds(2), FrameType.Light, ct);
        await camera.AbortExposureAsync(ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenGuideCameraCoupledToMisalignedMountWhenTrackingThenStarFieldDriftsWithMount()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        await using var sp = BuildHubServiceProvider(external);
        var hub = sp.GetRequiredService<IDeviceHub>();

        var mount = await ConnectMisalignedMountAsync(hub, ct);

        // Guide camera: the URI names it "GuideCam" so the driver both picks the
        // IMX178M preset and couples to the mount. FL=130mm @ 2.4um -> ~3.8"/px.
        var guideCamUri = new Uri("camera://FakeDevice/FakeGuideCam1#Fake Guide Cam");
        var guideCam = (FakeCameraDriver)await hub.ConnectAsync(new FakeDevice(guideCamUri), ct);
        guideCam.FocalLength = 130;

        // Plate-solve sync away from the pole: static offset learned, drift armed.
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        // First guide exposure captures the zero-drift reference -> star centred.
        await SnapshotPointingAsync(guideCam, ct);
        var atRef = guideCam.CurrentMountDriftPixels;
        Math.Abs(atRef.X).ShouldBeLessThan(0.05, "the guide star starts centred at acquisition");
        Math.Abs(atRef.Y).ShouldBeLessThan(0.05);

        // Track 5 sidereal minutes -> the misaligned mount drifts (mostly Dec); the
        // next snapshot reflects it on the guide sensor.
        external.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        await SnapshotPointingAsync(guideCam, ct);
        var at5 = guideCam.CurrentMountDriftPixels;
        var mag5 = Math.Sqrt(at5.X * at5.X + at5.Y * at5.Y);
        output.WriteLine($"5min guide-star drift: X={at5.X:F2}px Y={at5.Y:F2}px mag={mag5:F2}px");

        // ~28" Dec / 3.8"/px ~ 7.5px after 5 min: a real, mostly-Dec, non-runaway drift.
        mag5.ShouldBeGreaterThan(1.0, "the guide star must drift as the mount does");
        mag5.ShouldBeLessThan(60.0, "drift must stay realistic, not run away");
        Math.Abs(at5.Y).ShouldBeGreaterThan(Math.Abs(at5.X), "polar-misalignment drift is predominantly in Dec");

        // Drift accumulates with tracking time.
        external.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        await SnapshotPointingAsync(guideCam, ct);
        var at10 = guideCam.CurrentMountDriftPixels;
        var mag10 = Math.Sqrt(at10.X * at10.X + at10.Y * at10.Y);
        output.WriteLine($"10min guide-star drift: mag={mag10:F2}px");
        mag10.ShouldBeGreaterThan(mag5, "guide-star drift grows with tracking time");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMainImagingCameraWhenTrackingMisalignedMountThenNoCouplingDrift()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        await using var sp = BuildHubServiceProvider(external);
        var hub = sp.GetRequiredService<IDeviceHub>();

        var mount = await ConnectMisalignedMountAsync(hub, ct);

        // A main imaging camera (URI does NOT name it GuideCam). It renders at its
        // stamped Target and must not double-count the mount drift, or the session's
        // plate-solve centering loop would fight a phantom offset.
        var mainCam = (FakeCameraDriver)await hub.ConnectAsync(new FakeDevice(DeviceType.Camera, 1), ct);
        mainCam.FocalLength = 600;

        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        await SnapshotPointingAsync(mainCam, ct);
        external.TimeProvider.Advance(TimeSpan.FromMinutes(10));
        await SnapshotPointingAsync(mainCam, ct);

        var drift = mainCam.CurrentMountDriftPixels;
        drift.X.ShouldBe(0.0, "the main imaging camera does not couple to the mount drift");
        drift.Y.ShouldBe(0.0);
    }

    /// <summary>
    /// The end-to-end scenario the user hit in the GUI: the built-in guider calibrating
    /// and guiding against the misaligned <see cref="FakeSkywatcherMountDriver"/> through
    /// a coupled <see cref="FakeCameraDriver"/> guide camera, with ST-4 pulses routed to
    /// the camera (<c>pulseGuideSource=Camera</c> -- the Test profile's setting). Asserts
    /// the loop calibrates, reaches Guiding, and then STAYS locked with bounded RMS for
    /// minutes while the mount keeps drifting -- the corrections null the misalignment drift.
    /// <para>
    /// Requires <see cref="FakeTimeProviderWrapper.ExternalTimePump"/> so this pump is the
    /// SOLE clock: otherwise the guide loop's own <c>SleepAsync</c> calls advance fake time
    /// too, and the two competing clocks scramble the exposure/correction cadence into a
    /// spurious limit cycle (a test artifact, not a real guiding fault).
    /// </para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenBuiltInGuiderWithMisalignedSkywatcherAndCameraPulseWhenCalibrateAndGuideThenStaysLockedWhileMountDrifts()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        await using var sp = BuildHubServiceProvider(external);
        var hub = sp.GetRequiredService<IDeviceHub>();

        // Misaligned SkyWatcher, tracking, centred away from the pole (alignment learned
        // -> the residual polar-axis drift is now what the guider must chase).
        var mount = await ConnectMisalignedMountAsync(hub, ct);
        await mount.SetTrackingAsync(true, ct);
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        // Guide camera: URI names it GuideCam (couples to the mount drift); shrink the
        // readout so the per-frame render stays cheap across the long calibrate+guide loop.
        var guideCam = (FakeCameraDriver)await hub.ConnectAsync(
            new FakeDevice(new Uri("camera://FakeDevice/FakeGuideCam1#Fake Guide Cam")), ct);
        guideCam.FocalLength = 130;
        guideCam.BinX = 1;
        guideCam.NumX = 800;
        guideCam.NumY = 600;

        // Built-in guider with ST-4 routed to the camera -- exactly the Test profile.
        var guiderDevice = new BuiltInGuiderDevice(
            new Uri("guider://BuiltInGuiderDevice/builtin?pulseGuideSource=Camera#Built-in Guider"));
        var guider = new BuiltInGuiderDriver(guiderDevice, sp);
        await guider.ConnectAsync(ct);
        guider.LinkDevices(mount, guideCam);

        string? guidingError = null;
        guider.GuidingErrorEvent += (_, e) => guidingError = e.Message;

        // Drive time SOLELY from this pump now -- otherwise the guide loop's own
        // SleepAsync calls would also advance fake time, racing the pump and
        // scrambling the exposure<->correction timing.
        timeProvider.ExternalTimePump = true;

        // Kick off calibration + guiding (runs as a background task).
        await guider.GuideAsync(settlePixels: 1.5, settleTime: 3.0, settleTimeout: 90.0, ct);

        // Cooperative time pump: advance fake time so exposures complete and the loop
        // iterates, yielding to the background task between steps. Never SleepAsync here.
        var increment = TimeSpan.FromMilliseconds(250);
        var pumped = TimeSpan.Zero;
        var cap = TimeSpan.FromMinutes(10);
        while (pumped < cap && guidingError is null && !await guider.IsGuidingAsync(ct))
        {
            timeProvider.Advance(increment);
            pumped += increment;
            await Task.Delay(1, ct);
        }

        // Calibration must have acquired a star, measured rates/angle, and settled --
        // i.e. the built-in guider calibrates cleanly against the misaligned SkyWatcher
        // + coupled guide cam with ST-4 on the camera (the exact Test-profile scenario).
        guidingError.ShouldBeNull("calibration must not raise an error (acquire star + measure displacement)");
        (await guider.IsGuidingAsync(ct)).ShouldBeTrue(
            "the guider must reach the Guiding state after calibrating + settling against the misaligned mount");

        // At settle the lifetime RMS is tiny -- both axes converged cleanly.
        var settleStats = await guider.GetStatsAsync(ct);
        settleStats.ShouldNotBeNull();
        output.WriteLine(
            $"At-settle: TotalRMS={settleStats.TotalRMS:F2}\" RaRMS={settleStats.RaRMS:F2}\" DecRMS={settleStats.DecRMS:F2}\"");
        settleStats.TotalRMS.ShouldBeLessThan(3.0, "calibration + settle must converge cleanly (sub-3\" RMS)");

        // Guide for ~2 more minutes of fake time: the misaligned mount keeps drifting
        // (mostly Dec) the whole time, but the guider's camera ST-4 corrections null it,
        // so guiding stays locked. (Earlier this looked unstable -- that was a test bug:
        // without ExternalTimePump the guide loop's own SleepAsync calls advanced fake
        // time AND so did the pump, two clocks racing. With a single clock the loop is
        // rock-solid; the GuideLoop itself was never at fault.)
        for (var i = 0; i < 700 && guidingError is null && !ct.IsCancellationRequested; i++)
        {
            timeProvider.Advance(increment);
            await Task.Delay(1, ct);
        }
        guidingError.ShouldBeNull("guiding must stay healthy while the mount drifts");
        (await guider.IsGuidingAsync(ct)).ShouldBeTrue("guiding must stay locked, not fall out");

        var sustainedStats = await guider.GetStatsAsync(ct);
        sustainedStats.ShouldNotBeNull();
        output.WriteLine(
            $"Sustained: TotalRMS={sustainedStats.TotalRMS:F2}\" RaRMS={sustainedStats.RaRMS:F2}\" " +
            $"DecRMS={sustainedStats.DecRMS:F2}\" PeakRa={sustainedStats.PeakRa:F2}\" PeakDec={sustainedStats.PeakDec:F2}\"");
        // The corrections track the drift -> bounded RMS, NOT the runaway we saw with the
        // broken two-clock harness. Generous bound: the mount drift + PE residual + centroid
        // noise should stay well under a few arcsec on this coarse (~3.8"/px) guide scope.
        sustainedStats.TotalRMS.ShouldBeLessThan(5.0, "sustained guiding must stay locked against the drift");

        // Instrumentation: an occasional re-lock onto another star is normal (a star can leave the
        // frame, a satellite can pass), but REPEATED swapping is the instability the user reported --
        // each swap resets the guide reference and jumps the error graph ("the selected guide star
        // keeps changing" + the Dec flip-flop). Over a ~2-minute locked run with bounded RMS the lock
        // should hold steady, so swaps should be rare; a storm of them would fail this.
        output.WriteLine(
            $"Instrumentation: starLost={guider.GuideStarLostEvents} reacq={guider.GuideReacquisitionEvents} differentStar={guider.GuideDifferentStarReacquisitions}");
        guider.GuideDifferentStarReacquisitions.ShouldBeLessThanOrEqualTo(2,
            "an isolated re-lock is acceptable, but repeatedly swapping the guide star while supposedly locked is the reported instability");

        await guider.StopCaptureAsync(TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>
    /// Recovery validation via a simulated cloud: a cloud sits over the guide star during the first
    /// calibration attempt so it cannot acquire; the driver RETRIES rather than abandoning the
    /// session, and once the cloud clears the retry calibrates cleanly and guiding starts. Daytime is
    /// irrelevant here -- fake devices, controllable cloud coverage, fake time. The companion to the
    /// divergence-recovery path: this exercises calibration-retry-on-transient-failure.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenCloudDuringFirstCalibrationWhenItClearsThenRetrySucceedsAndGuides()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        await using var sp = BuildHubServiceProvider(external);
        var hub = sp.GetRequiredService<IDeviceHub>();

        var mount = await ConnectMisalignedMountAsync(hub, ct);
        await mount.SetTrackingAsync(true, ct);
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        var guideCam = (FakeCameraDriver)await hub.ConnectAsync(
            new FakeDevice(new Uri("camera://FakeDevice/FakeGuideCam1#Fake Guide Cam")), ct);
        guideCam.FocalLength = 130;
        guideCam.BinX = 1;
        guideCam.NumX = 800;
        guideCam.NumY = 600;
        guideCam.CloudCoverage = 0.99; // overcast -> the first calibration attempt cannot acquire a star

        var guiderDevice = new BuiltInGuiderDevice(
            new Uri("guider://BuiltInGuiderDevice/builtin?pulseGuideSource=Camera#Built-in Guider"));
        var guider = new BuiltInGuiderDriver(guiderDevice, sp);
        await guider.ConnectAsync(ct);
        guider.LinkDevices(mount, guideCam);

        string? guidingError = null;
        guider.GuidingErrorEvent += (_, e) => guidingError = e.Message;

        timeProvider.ExternalTimePump = true;
        await guider.GuideAsync(settlePixels: 1.5, settleTime: 3.0, settleTimeout: 90.0, ct);

        var increment = TimeSpan.FromMilliseconds(250);
        var pumped = TimeSpan.Zero;
        var cleared = false;
        while (pumped < TimeSpan.FromMinutes(10) && guidingError is null && !await guider.IsGuidingAsync(ct))
        {
            // The cloud clears a few seconds in -- after the first attempt has failed, during the
            // retry wait -- so the second calibration attempt finds the star.
            if (!cleared && pumped > TimeSpan.FromSeconds(3))
            {
                guideCam.CloudCoverage = 0.0;
                cleared = true;
            }
            timeProvider.Advance(increment);
            pumped += increment;
            await Task.Delay(1, ct);
        }

        output.WriteLine($"clearedCloud={cleared} reachedGuiding={await guider.IsGuidingAsync(ct)} guidingError={guidingError ?? "(none)"} pumped={pumped.TotalSeconds:F0}s");
        cleared.ShouldBeTrue("the first attempt must have stayed cloudy long enough that the cloud-clear path was exercised (else the cloud was too weak to block acquisition)");
        guidingError.ShouldBeNull("a transient cloud during the first calibration must not abort the session -- the retry recovers");
        (await guider.IsGuidingAsync(ct)).ShouldBeTrue("once the cloud clears, the calibration retry must succeed and guiding must start");

        await guider.StopCaptureAsync(TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>
    /// User-requested ("delete the model and get a new sample"). Trains a FRESH neural model from
    /// scratch in the misaligned-Skywatcher sim (empty profile -> InitializeRandom + online learning)
    /// over a long run so the blend ramps in and the model learns. Asserts a fresh .ngm is saved (the
    /// new sample) and that guiding never suffers the catastrophic Dec divergence the CORRUPT loaded
    /// model caused (568" / kill-switch in HarmfulLoadedNeuralModelIsHardDisabledBySafetyNet) -- i.e.
    /// the corrupt model was the problem, not neural guiding in general.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task FreshNeuralModelTrainsStablyAndSavesNewSample()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        await using var sp = BuildHubServiceProvider(external);
        var hub = sp.GetRequiredService<IDeviceHub>();

        var mount = await ConnectMisalignedMountAsync(hub, ct);
        await mount.SetTrackingAsync(true, ct);
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        var guideCam = (FakeCameraDriver)await hub.ConnectAsync(
            new FakeDevice(new Uri("camera://FakeDevice/FakeGuideCam1#Fake Guide Cam")), ct);
        guideCam.FocalLength = 130;
        guideCam.BinX = 1;
        guideCam.NumX = 800;
        guideCam.NumY = 600;

        // Empty profile -> no .ngm on disk -> the driver initialises a FRESH model and trains it
        // online. Neural forced ON to exercise training (it's opt-in/default-off now).
        var guiderDevice = new BuiltInGuiderDevice(
            new Uri("guider://BuiltInGuiderDevice/builtin?pulseGuideSource=Camera&useNeuralGuider=true#Built-in Guider"));
        var guider = new BuiltInGuiderDriver(guiderDevice, sp);
        await guider.ConnectAsync(ct);
        guider.LinkDevices(mount, guideCam);

        string? guidingError = null;
        guider.GuidingErrorEvent += (_, e) => guidingError = e.Message;

        timeProvider.ExternalTimePump = true;
        await guider.GuideAsync(settlePixels: 1.5, settleTime: 3.0, settleTimeout: 90.0, ct);

        var increment = TimeSpan.FromMilliseconds(250);
        var pumped = TimeSpan.Zero;
        var cap = TimeSpan.FromMinutes(10);
        while (pumped < cap && guidingError is null && !await guider.IsGuidingAsync(ct))
        {
            timeProvider.Advance(increment);
            pumped += increment;
            await Task.Delay(1, ct);
        }
        (await guider.IsGuidingAsync(ct)).ShouldBeTrue("must reach Guiding before the training pump");

        // Pump ~1050s so the blend ramp completes and the fresh model trains + saves online.
        for (var i = 0; i < 4200 && guidingError is null && !ct.IsCancellationRequested; i++)
        {
            timeProvider.Advance(increment);
            await Task.Delay(1, ct);
        }

        var stats = await guider.GetStatsAsync(ct);
        stats.ShouldNotBeNull();

        await guider.StopCaptureAsync(TimeSpan.FromSeconds(5), ct);

        var ngDir = new System.IO.DirectoryInfo(System.IO.Path.Combine(external.ProfileFolder.FullName, "NeuralGuider"));
        var savedSamples = ngDir.Exists ? ngDir.GetFiles("*.ngm") : System.Array.Empty<System.IO.FileInfo>();

        output.WriteLine(
            $"Fresh model: NeuralActive={guider.GuideNeuralActive} DecRMS={stats.DecRMS:F2}\" PeakDec={stats.PeakDec:F2}\" " +
            $"savedSamples={savedSamples.Length} starLost={guider.GuideStarLostEvents}");

        guidingError.ShouldBeNull("fresh-model guiding must not raise a fatal error");
        savedSamples.Length.ShouldBeGreaterThan(0, "online learning must save a fresh model sample (the 'new sample')");
        stats.DecRMS.ShouldBeLessThan(60.0,
            "a fresh model must not reproduce the corrupt model's catastrophic Dec divergence (568\")");
    }

    /// <summary>
    /// Regression for the "guider flatlines after slewing to the target" session bug: the
    /// zero-drift reference was captured once per driver lifetime, so after a GOTO the random
    /// star field rendered with an offset equal to the whole slew (tens of thousands of pixels)
    /// and every post-slew guide frame was starless. A pointing jump far beyond anything drift
    /// can produce must re-baseline the reference so the drift term restarts near zero — while
    /// ordinary tracking drift (the signal the guider nulls) must survive untouched.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GivenGuideCameraWhenMountGotosToNewTargetThenDriftReferenceRebaselines()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        await using var sp = BuildHubServiceProvider(external);
        var hub = sp.GetRequiredService<IDeviceHub>();

        var mount = await ConnectMisalignedMountAsync(hub, ct);
        await mount.SetTrackingAsync(true, ct);

        var guideCam = (FakeCameraDriver)await hub.ConnectAsync(
            new FakeDevice(new Uri("camera://FakeDevice/FakeGuideCam1#Fake Guide Cam")), ct);
        guideCam.FocalLength = 130;

        await mount.SyncRaDecAsync(6.0, 45.0, ct);
        await SnapshotPointingAsync(guideCam, ct); // zero-drift reference at the calibration spot

        // Track 5 minutes: genuine misalignment drift accumulates and must NOT re-baseline.
        external.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        await SnapshotPointingAsync(guideCam, ct);
        var beforeSlew = guideCam.CurrentMountDriftPixels;
        Math.Sqrt(beforeSlew.X * beforeSlew.X + beforeSlew.Y * beforeSlew.Y)
            .ShouldBeGreaterThan(1.0, "ordinary tracking drift must survive (no spurious re-baseline)");

        // GOTO to the actual target (the user's failing scenario: calibrate near one pointing,
        // then slew away). 2° is far beyond the 10' slew-detection threshold.
        await mount.BeginSlewRaDecAsync(6.2, 43.0, ct);
        for (var i = 0; i < 600 && await mount.IsSlewingAsync(ct); i++)
        {
            external.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, ct);
        }
        (await mount.IsSlewingAsync(ct)).ShouldBeFalse("slew must complete within the pumped window");

        await SnapshotPointingAsync(guideCam, ct);
        var afterSlew = guideCam.CurrentMountDriftPixels;
        var magAfter = Math.Sqrt(afterSlew.X * afterSlew.X + afterSlew.Y * afterSlew.Y);
        output.WriteLine($"drift after GOTO: X={afterSlew.X:F2}px Y={afterSlew.Y:F2}px (thousands of px before the fix)");
        magAfter.ShouldBeLessThan(1.0,
            "the GOTO must re-baseline the zero-drift reference so the field starts fresh at the new target");
    }

    /// <summary>
    /// Regression for "guiding starts on a starless field and flatlines silently": with a
    /// reusable in-memory calibration, the guide-start path skipped any acquisition check and
    /// called SetLockPosition on nothing — the loop then reported healthy Guiding/Settling with
    /// an empty graph forever. Acquisition is now gated: bounded retries, then a loud
    /// GuidingErrorEvent and a return to Idle.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenCloudWhenGuidingRestartsWithReusedCalibrationThenAcquisitionFailsLoudly()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        await using var sp = BuildHubServiceProvider(external);
        var hub = sp.GetRequiredService<IDeviceHub>();

        var mount = await ConnectMisalignedMountAsync(hub, ct);
        await mount.SetTrackingAsync(true, ct);
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        var guideCam = (FakeCameraDriver)await hub.ConnectAsync(
            new FakeDevice(new Uri("camera://FakeDevice/FakeGuideCam1#Fake Guide Cam")), ct);
        guideCam.FocalLength = 130;
        guideCam.BinX = 1;
        guideCam.NumX = 800;
        guideCam.NumY = 600;

        // 2 attempts, 1s retry delay: keep the deliberately-failing path fast (advanced knobs).
        var guiderDevice = new BuiltInGuiderDevice(new Uri(
            "guider://BuiltInGuiderDevice/builtin?pulseGuideSource=Camera&maxCalibrationAttempts=2&calibrationRetryDelaySeconds=1#Built-in Guider"));
        var guider = new BuiltInGuiderDriver(guiderDevice, sp);
        await guider.ConnectAsync(ct);
        guider.LinkDevices(mount, guideCam);

        string? guidingError = null;
        guider.GuidingErrorEvent += (_, e) => guidingError = e.Message;

        timeProvider.ExternalTimePump = true;
        await guider.GuideAsync(settlePixels: 1.5, settleTime: 3.0, settleTimeout: 90.0, ct);

        var increment = TimeSpan.FromMilliseconds(250);
        var pumped = TimeSpan.Zero;
        while (pumped < TimeSpan.FromMinutes(10) && guidingError is null && !await guider.IsGuidingAsync(ct))
        {
            timeProvider.Advance(increment);
            pumped += increment;
            await Task.Delay(1, ct);
        }
        guidingError.ShouldBeNull("the clean first calibration must succeed");
        (await guider.IsGuidingAsync(ct)).ShouldBeTrue("must reach Guiding so a calibration is cached for reuse");

        // Stop (slew-to-target in a real session), cloud over, restart with the cached calibration.
        await guider.StopCaptureAsync(TimeSpan.FromSeconds(5), ct);

        // Drain the loop's in-flight exposure before restarting — a real session slews for a
        // minute between stop and restart, so the camera is never still Exposing at re-start.
        for (var i = 0; i < 40; i++)
        {
            timeProvider.Advance(increment);
            await Task.Delay(1, ct);
        }

        // Yank the whole field off the sensor (raw ST-4 mega-pulse, ~2 px/s × 800 s ≈ 1600 px on a
        // 600 px-high readout): acquisition now faces genuinely starless frames — deterministic,
        // unlike cloud attenuation which caps at 90% and lets the brightest star through.
        await guideCam.PulseGuideAsync(TianWen.DAL.GuideDirection.North, TimeSpan.FromSeconds(800), ct);
        await guider.GuideAsync(settlePixels: 1.5, settleTime: 3.0, settleTimeout: 90.0, ct);

        pumped = TimeSpan.Zero;
        while (pumped < TimeSpan.FromMinutes(5) && guidingError is null)
        {
            timeProvider.Advance(increment);
            pumped += increment;
            await Task.Delay(1, ct);
        }

        guidingError.ShouldNotBeNull("starting to guide on a starless field must fail loudly, not flatline");
        guidingError.ShouldContain("no usable guide star");
        (await guider.IsGuidingAsync(ct)).ShouldBeFalse("the driver must return to Idle after giving up");
    }

    /// <summary>
    /// Star loss during guiding must surface as the PHD2-style "LostLock" app state (red label +
    /// notification upstream) instead of a healthy-looking Guiding/Settling with a flat graph —
    /// and must clear back to Guiding once the tracker re-acquires.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenCloudDuringGuidingThenStatusReportsLostLockAndRecoversOnClear()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        await using var sp = BuildHubServiceProvider(external);
        var hub = sp.GetRequiredService<IDeviceHub>();

        var mount = await ConnectMisalignedMountAsync(hub, ct);
        await mount.SetTrackingAsync(true, ct);
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        var guideCam = (FakeCameraDriver)await hub.ConnectAsync(
            new FakeDevice(new Uri("camera://FakeDevice/FakeGuideCam1#Fake Guide Cam")), ct);
        guideCam.FocalLength = 130;
        guideCam.BinX = 1;
        guideCam.NumX = 800;
        guideCam.NumY = 600;

        var guiderDevice = new BuiltInGuiderDevice(
            new Uri("guider://BuiltInGuiderDevice/builtin?pulseGuideSource=Camera#Built-in Guider"));
        var guider = new BuiltInGuiderDriver(guiderDevice, sp);
        await guider.ConnectAsync(ct);
        guider.LinkDevices(mount, guideCam);

        string? guidingError = null;
        guider.GuidingErrorEvent += (_, e) => guidingError = e.Message;

        timeProvider.ExternalTimePump = true;
        await guider.GuideAsync(settlePixels: 1.5, settleTime: 3.0, settleTimeout: 90.0, ct);

        var increment = TimeSpan.FromMilliseconds(250);
        var pumped = TimeSpan.Zero;
        while (pumped < TimeSpan.FromMinutes(10) && guidingError is null && !await guider.IsGuidingAsync(ct))
        {
            timeProvider.Advance(increment);
            pumped += increment;
            await Task.Delay(1, ct);
        }
        guidingError.ShouldBeNull();
        (await guider.IsGuidingAsync(ct)).ShouldBeTrue("must reach Guiding before the cloud rolls in");
        (await guider.GetStatusAsync(ct)).AppState.ShouldBe("Guiding");

        // Yank the whole field off the sensor via a raw ST-4 mega-pulse on the camera (the fake
        // applies rate × duration instantly, ~2 px/s × 800 s ≈ 1600 px on a 600 px-high readout):
        // every star leaves the frame, so the tracker loses the star persistently — the
        // deterministic stand-in for clouds/obstruction (cloud attenuation caps at 90%, which a
        // bright locked star survives).
        await guideCam.PulseGuideAsync(TianWen.DAL.GuideDirection.North, TimeSpan.FromSeconds(800), ct);
        string? appState = null;
        pumped = TimeSpan.Zero;
        while (pumped < TimeSpan.FromMinutes(3))
        {
            timeProvider.Advance(increment);
            pumped += increment;
            await Task.Delay(1, ct);
            (appState, _) = await guider.GetStatusAsync(ct);
            if (appState == "LostLock")
            {
                break;
            }
        }
        appState.ShouldBe("LostLock", "a starless guide frame must surface as LostLock, not a healthy-looking flatline");

        // The field returns: the tracker re-acquires and the state returns to Guiding.
        await guideCam.PulseGuideAsync(TianWen.DAL.GuideDirection.South, TimeSpan.FromSeconds(800), ct);
        pumped = TimeSpan.Zero;
        while (pumped < TimeSpan.FromMinutes(3))
        {
            timeProvider.Advance(increment);
            pumped += increment;
            await Task.Delay(1, ct);
            (appState, _) = await guider.GetStatusAsync(ct);
            if (appState == "Guiding")
            {
                break;
            }
        }
        appState.ShouldBe("Guiding", "once the cloud clears the tracker re-acquires and the lost-lock flag clears");

        await guider.StopCaptureAsync(TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>
    /// With a catalog DB wired, the guide camera projects REAL catalog stars at the coupled
    /// mount's live pointing (guide-scope cone error + camera roll applied), so it produces an
    /// acquirable star field at ANY pointing — including immediately after a GOTO, which was the
    /// random-field fallback's failure mode.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenCatalogDbThenGuideCamRendersAcquirableStarsAtAnyMountPointing()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var db = new CelestialObjectDB();
        await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
        external.CelestialObjectDB = db;

        await using var sp = BuildHubServiceProvider(external);
        var hub = sp.GetRequiredService<IDeviceHub>();

        var mount = await ConnectMisalignedMountAsync(hub, ct);
        await mount.SetTrackingAsync(true, ct);
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        var guideCam = (FakeCameraDriver)await hub.ConnectAsync(
            new FakeDevice(new Uri("camera://FakeDevice/FakeGuideCam1#Fake Guide Cam")), ct);
        guideCam.FocalLength = 130;
        guideCam.BinX = 1;
        guideCam.NumX = 800;
        guideCam.NumY = 600;

        IExternal externalItf = external; // ImageReadyPollInterval is a default interface member
        var frame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(
            guideCam, TimeSpan.FromSeconds(2), external.TimeProvider, externalItf.ImageReadyPollInterval, ct);
        var tracker = new GuiderCentroidTracker(maxStars: 1);
        tracker.ProcessFrame(frame.GetChannelArray(0));
        frame.Release();
        tracker.IsAcquired.ShouldBeTrue("catalog stars must render at the initial pointing");

        // GOTO elsewhere and capture again — the field must follow the mount.
        await mount.BeginSlewRaDecAsync(6.2, 43.0, ct);
        for (var i = 0; i < 600 && await mount.IsSlewingAsync(ct); i++)
        {
            external.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, ct);
        }
        (await mount.IsSlewingAsync(ct)).ShouldBeFalse("slew must complete within the pumped window");

        var frame2 = await BuiltInGuiderDriver.CaptureGuideFrameAsync(
            guideCam, TimeSpan.FromSeconds(2), external.TimeProvider, externalItf.ImageReadyPollInterval, ct);
        var tracker2 = new GuiderCentroidTracker(maxStars: 1);
        tracker2.ProcessFrame(frame2.GetChannelArray(0));
        frame2.Release();
        tracker2.IsAcquired.ShouldBeTrue(
            "the star field must follow the mount to the new pointing — this was the starless-after-slew bug");
    }
}
