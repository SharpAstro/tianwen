using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
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
    private static ServiceProvider BuildHubServiceProvider(FakeExternal external) =>
        new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(external.TimeProvider)
            .AddLogging()
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

        await guider.StopCaptureAsync(TimeSpan.FromSeconds(5), ct);
    }
}
