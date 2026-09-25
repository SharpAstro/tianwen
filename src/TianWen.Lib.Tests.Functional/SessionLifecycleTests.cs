using Microsoft.Extensions.DependencyInjection;
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
        // Dec 15, 22:00 UTC from Vienna; astronomical twilight rise on Dec 16 ~05:00–06:00 UTC
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        var startTime = ctx.TimeProvider.GetUtcNow().UtcDateTime;
        var endTime = await ctx.Session.SessionEndTimeAsync(startTime, ct);

        endTime.ShouldBeGreaterThan(startTime);
        var duration = endTime - startTime;
        duration.TotalHours.ShouldBeGreaterThan(5, "winter night should be longer than 5 hours");
        duration.TotalHours.ShouldBeLessThan(12, "but shorter than 12 hours");

        output.WriteLine($"Start: {startTime:u}, End: {endTime:u}, Duration: {duration}");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenHighLatitudeSummerNoAstroDarkWhenSessionEndTimeThenFallsBackInsteadOfThrowing()
    {
        // 50.9N / 8.2E (the user's German test site) on the June solstice: the sun bottoms at
        // ~-15.7 deg, so astronomical twilight (-18) is NEVER reached. A bare
        // EventTimes(AstronomicalTwilight) call returns zero rise events -> the old code threw
        // "Failed to retrieve astro event time". CalculateNightWindow falls back to
        // amateur-astronomical/nautical twilight, so the session gets a real (short) night window
        // and runs instead of crashing.
        var ct = TestContext.Current.CancellationToken;
        var solsticeEvening = new DateTimeOffset(2026, 6, 20, 22, 0, 0, TimeSpan.FromHours(2)); // 22:00 CEST
        await using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, now: solsticeEvening, latitude: 50.9, longitude: 8.2, cancellationToken: ct);

        var startTime = ctx.TimeProvider.GetUtcNow().UtcDateTime;
        var endTime = await ctx.Session.SessionEndTimeAsync(startTime, ct); // must not throw

        endTime.ShouldBeGreaterThan(startTime);
        var duration = endTime - startTime;
        duration.TotalHours.ShouldBeGreaterThan(0.5, "a high-latitude summer night via the twilight fallback is still a real (if short) window");
        duration.TotalHours.ShouldBeLessThan(12, "and never a full astronomical night this far north at the solstice");

        output.WriteLine($"Start: {startTime:u}, End: {endTime:u}, Duration: {duration}");
    }

    // --- WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenObservationAlreadyStartedWhenWaitForDarkThenReturnsImmediately()
    {
        // Observation started 30 minutes before the session's "now"; should skip immediately
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
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, observations: observations, cancellationToken: ct);

        var timeBefore = ctx.TimeProvider.GetUtcNow();

        await ctx.Session.WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(ct);

        var timeAfter = ctx.TimeProvider.GetUtcNow();
        var elapsed = timeAfter - timeBefore;
        elapsed.TotalSeconds.ShouldBeLessThan(1, "should return immediately when observation already started");
    }

    // --- CoolCamerasToAmbientAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenCooledCameraWhenCoolToAmbientThenReturnsSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

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

    // Both hemispheres: Vienna (northern) and Melbourne (southern). The calibration must slew
    // EAST of the meridian (before crossing) in BOTH, so the GEM stays on its pre-flip pier side
    // for the whole calibration. "East/before crossing" is HA < 0 in either hemisphere (the
    // convention is absolute); only the apparent left/right motion mirrors south of the equator.
    [Theory(Timeout = 120_000)]
    [InlineData(48.2, 16.3)]                  // Vienna, northern
    [InlineData(-37.8743502, 145.1668205)]    // Melbourne, southern
    public async Task GivenConnectedMountWhenCalibrateGuiderThenSlewsEastOfMeridianAndStartsGuiding(double latitude, double longitude)
    {
        // Use winter night when dec=0 near meridian is well above horizon. dec=0 culminates at a
        // fixed altitude (90 - |lat|) regardless of season, so it is well clear of the horizon from
        // both sites, and CalibrateGuiderAsync does not gate on darkness.
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, now: WinterNight, latitude: latitude, longitude: longitude, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // Run calibration: slews 30 min east of meridian at dec=0, then starts guiding
        var calibrateTask = ctx.Track(Task.Run(async () => await ctx.Session.CalibrateGuiderAsync(ctx.Token), ctx.Token));

        while (!calibrateTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        calibrateTask.IsCompleted.ShouldBeTrue("CalibrateGuiderAsync should complete within timeout");
        await calibrateTask; // propagate any exceptions

        // The calibration target must sit EAST of the meridian (HA < 0): it is approaching the
        // meridian but has not crossed it, so the mount stays on its pre-flip pier side. Assert the
        // COMMANDED hour angle the fake mount captured at slew time -- NOT the live HA read back
        // after calibration. The live read is clock-drift-exposed: HA = LST - RA grows with fake
        // time, and on this auto-advancing FakeTimeProvider the concurrent GuideStatsPoller
        // free-spins while the guider settle task waits for thread-pool scheduling, advancing fake
        // time 2s per iteration bounded only by CI load -- a loaded runner measured +0.94h (1.44h
        // of drift, ~2600 poller iterations) on a slew that correctly targeted -0.5h. The commanded
        // value is drift-immune and still pins the exact regression: the old bug commanded +0.5h,
        // WEST -- past the meridian on the opposite pier side -- failing this in BOTH hemispheres.
        var commandedHa = ((FakeMountDriver)ctx.Mount).LastCommandedHourAngle;
        output.WriteLine($"Commanded calibration hour angle: {commandedHa:F3}h ({(commandedHa < 0 ? "EAST, before crossing" : "WEST, after crossing")}) at lat={latitude}");
        commandedHa.ShouldBeLessThan(0.0, $"calibration must slew EAST of the meridian (HA < 0, before crossing), but commanded HA={commandedHa:F3}h");
        commandedHa.ShouldBe(-0.5, 0.1, $"calibration target should be ~30 min east of the meridian, but commanded HA={commandedHa:F3}h");

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
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        // Run initialisation: connects, unparks (no-op), sets UTC, cools to sensor temp, opens covers (null=ok)
        var initTask = ctx.Track(Task.Run(async () => await ctx.Session.InitialisationAsync(ctx.Token), ctx.Token));

        while (!initTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromSeconds(1), ct);
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

    /// <summary>
    /// A run's devices connect THROUGH the hub, so a node holds one driver per device (P0b item 11 of
    /// docs/plans/hardware-in-the-server.md, #752). The session connected drivers of its own unless the hub
    /// already held them connected, so on a server, where nothing pre-connects, the session and the hub were
    /// two driver worlds: /devices said the session's devices were disconnected, the Alpaca plane could not see
    /// them, and an Alpaca Connected=true opened a second driver on a device the session was driving.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenNothingPreConnectedWhenInitialisationThenTheHubHoldsTheSessionsOwnDrivers()
    {
        var ct = TestContext.Current.CancellationToken;
        // Uncoupled, so the helper puts nothing in the hub: the server's shape, where nothing pre-connects.
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, coupleCameraToMount: false, cancellationToken: ct);
        var hub = ctx.Session.ServiceProvider.GetRequiredService<IDeviceHub>();

        await InitialiseAsync(ctx, ct);

        var setup = ctx.Session.Setup;
        var telescope = setup.Telescopes[0];
        HeldByTheHub(hub, setup.Mount.Device, setup.Mount.Driver);
        HeldByTheHub(hub, setup.Guider.Device, setup.Guider.Driver);
        HeldByTheHub(hub, telescope.Camera.Device, telescope.Camera.Driver);
        HeldByTheHub(hub, telescope.Focuser.ShouldNotBeNull().Device, telescope.Focuser.Driver);

        // What an Alpaca Connected=true does: it asks the hub, which answers with the session's own driver.
        (await hub.ConnectAsync(telescope.Camera.Device, ct)).ShouldBeSameAs(telescope.Camera.Driver);

        // And the helper's "uncoupled" still holds with the mount in the hub now: the opt-out sits on each
        // camera's own driver, the instance the hub adopted, which is what the loop tests that opt out rely on.
        ((FakeCameraDriver)telescope.Camera.Driver).ResolveCoupledMount().ShouldBeNull();
        ((FakeCameraDriver)setup.GuiderSetup.Camera.ShouldNotBeNull().Driver).ResolveCoupledMount().ShouldBeNull();
    }

    private static void HeldByTheHub(IDeviceHub hub, DeviceBase device, IDeviceDriver driver)
    {
        hub.TryGetConnectedDriver<IDeviceDriver>(device.DeviceUri, out var held).ShouldBeTrue($"{device.DisplayName} is in the hub");
        held.ShouldBeSameAs(driver, $"the hub holds the session's own {device.DisplayName} driver, not a second one");
    }

    // --- Which site a run is on (#798) ---

    // The fake mount is in Vienna until something gives it another site, and the profile's is Melbourne, so
    // the two can never be mistaken for each other.
    private static readonly SiteCoordinates Vienna = new(48.2, 16.3, 200);
    private static readonly SiteCoordinates Melbourne = new(-37.8136, 144.9631, 31);

    /// <summary>
    /// A run whose request names no site still evaluates its HORIZON limit, on the site its mount is on. The
    /// limit poll computed the altitude from the configured site alone, so with none the altitude was NaN,
    /// which <c>MountLimits.Evaluate</c> reads as "skip the horizon test". Since #799 that is every server run
    /// started without a site under the mount-wins default: the meridian limit working, the horizon limit off.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenNoConfiguredSiteWhenInitialisedThenTheHorizonLimitIsEvaluatedOnTheMountsSite()
    {
        var ct = TestContext.Current.CancellationToken;
        // The meridian limit out of the way: 5 h past the meridian would trip it too, and it wins a tie.
        var limits = new MountLimitConfiguration(Enabled: true, MeridianWarnMinutes: 600);
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, mountLimits: limits, cancellationToken: ct);
        double.IsNaN(ctx.Session.Configuration.SiteLatitude).ShouldBeTrue("premise: the request names no site");

        await InitialiseAsync(ctx, ct);

        // Low in the west, placed by SYNC: 5 h past the meridian at dec -10 is 2.3 deg up from Vienna, under
        // the 10 deg floor, and descending, which is when the horizon test applies.
        await ctx.Mount.SetTrackingAsync(true, ct);
        var lst = await ctx.Mount.GetSiderealTimeAsync(ct);
        await ctx.Mount.SyncRaDecAsync(((lst - 5.0) % 24.0 + 24.0) % 24.0, -10.0, ct);
        await ctx.Session.PollDeviceStatesAsync(ct);

        ctx.Session.MountState.Altitude.ShouldBe(2.3, 1.0, "the altitude on the mount's site");
        ctx.Session.MountLimitVerdict.Kind.ShouldBe(MountLimitKind.Horizon, ctx.Session.MountLimitVerdict.Describe());
    }

    /// <summary>
    /// A run whose request names no site gives a mount with no site of its own the profile's, whatever the
    /// tie-breaker says: the rule the GUI applied when a mount connected, which a run on the server never did.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenNoConfiguredSiteAndAMountWithNoneWhenInitialisedThenTheMountTakesTheProfilesSite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, profileSite: Melbourne, cancellationToken: ct);
        // What a mount never given a site reports (SkyWatcher, iOptron).
        await ctx.Mount.SetSiteLatitudeAsync(double.NaN, ct);
        await ctx.Mount.SetSiteLongitudeAsync(double.NaN, ct);

        await InitialiseAsync(ctx, ct);

        (await ctx.Mount.GetSiteAsync(ct)).ShouldBe(Melbourne);
    }

    /// <summary>The same with the profile as the site's authority: the mount's own site gives way to it.</summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenNoConfiguredSiteAndTheProfileAsTheAuthorityWhenInitialisedThenTheMountTakesTheProfilesSite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight,
            profileSite: Melbourne, siteTieBreaker: SiteTieBreaker.Profile, cancellationToken: ct);
        (await ctx.Mount.GetSiteAsync(ct)).ShouldBe(Vienna, "premise: the mount has a site of its own");

        await InitialiseAsync(ctx, ct);

        (await ctx.Mount.GetSiteAsync(ct)).ShouldBe(Melbourne);
    }

    /// <summary>Under the mount-wins default, a mount with a site of its own keeps it.</summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenNoConfiguredSiteAndTheMountAsTheAuthorityWhenInitialisedThenTheMountKeepsItsOwnSite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, profileSite: Melbourne, cancellationToken: ct);

        await InitialiseAsync(ctx, ct);

        (await ctx.Mount.GetSiteAsync(ct)).ShouldBe(Vienna);
    }

    /// <summary>A site the request names is the run's, over the profile's and the mount's alike.</summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenAConfiguredSiteWhenInitialisedThenTheMountTakesItWhateverTheProfileSays()
    {
        var ct = TestContext.Current.CancellationToken;
        var configuration = SessionTestHelper.DefaultConfiguration with { SiteLatitude = 51.4779, SiteLongitude = -0.0015 };
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, configuration: configuration, now: WinterNight,
            profileSite: Melbourne, siteTieBreaker: SiteTieBreaker.Profile, cancellationToken: ct);

        await InitialiseAsync(ctx, ct);

        var site = (await ctx.Mount.GetSiteAsync(ct)).ShouldNotBeNull();
        (site.Latitude, site.Longitude).ShouldBe((51.4779, -0.0015));
    }

    /// <summary>
    /// With no site anywhere a run cannot plan its night, and says what to do about it. It used to fail deep in
    /// SOFA with "Site longitude has not been set", from a transform built on the mount's NaN site.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GivenNoSiteAnywhereWhenPlanningTheNightThenTheRunFailsSayingToSetOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);
        await ctx.Mount.SetSiteLatitudeAsync(double.NaN, ct);
        await ctx.Mount.SetSiteLongitudeAsync(double.NaN, ct);

        await InitialiseAsync(ctx, ct);
        ctx.Session.Site.ShouldBeNull("premise: the request, the mount and the profile name none");

        var failure = await Should.ThrowAsync<SessionFailedException>(ctx.Session.SessionEndTimeAsync(WinterNight.UtcDateTime, ct).AsTask());
        failure.Message.ShouldContain("Set the site");
    }

    private static async Task InitialiseAsync(SessionTestContext ctx, CancellationToken ct)
    {
        var initTask = ctx.Track(Task.Run(async () => await ctx.Session.InitialisationAsync(ctx.Token), ctx.Token));
        while (!initTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        (await initTask).ShouldBeTrue("initialisation should succeed");
    }

    // --- Finalise ---

    /// <summary>
    /// Finalise must COMMAND the mount to stop tracking, not merely observe that it has.
    /// </summary>
    /// <remarks>
    /// The step logged "Finalise: stopping tracking..." and then only read <c>IsTracking</c>;
    /// <c>SetTrackingAsync(false)</c> was called nowhere in the shutdown path. It was invisible on any
    /// mount that can park, because ASCOM's <c>Park</c> stops tracking by definition and the SkyWatcher
    /// driver halts both axes on the way home -- so the motor did stop and the report's line happened to
    /// come out true. On a mount with <c>CanPark == false</c> (the iOptron SkyGuider Pro) nothing in the
    /// finaliser ever stopped it, and a finished session left the mount tracking until the battery died
    /// or the payload met the tripod.
    /// <para>Hence <c>CanPark = false</c> here: with park available the fake's own <c>ParkAsync</c> clears
    /// the flag and this test passes with the bug still in place.</para>
    /// </remarks>
    [Fact(Timeout = 120_000)]
    public async Task GivenAMountThatCannotParkWhenFinaliseThenTrackingIsStillStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        var mount = (FakeMountDriver)ctx.Mount;
        mount.CanPark = false;
        mount.CanSetTracking.ShouldBeTrue("the mount must be able to stop tracking for this to be its job");

        await ((IMountDriver)mount).EnsureTrackingAsync(cancellationToken: ct);
        (await mount.IsTrackingAsync(ct)).ShouldBeTrue("mount should be tracking before finalise");

        var finaliseTask = ctx.Track(Task.Run(async () => await ctx.Session.Finalise(ctx.Token), ctx.Token));
        while (!finaliseTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        finaliseTask.IsCompleted.ShouldBeTrue("Finalise should complete within timeout");
        await finaliseTask;

        (await mount.AtParkAsync(ct)).ShouldBeFalse("precondition: this mount cannot park, so park cannot be what stopped it");
        (await mount.IsTrackingAsync(ct)).ShouldBeFalse("Finalise must stop tracking on a mount that cannot be parked");
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenActiveSessionWhenFinaliseThenShutdownCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // Start guiding so Finalise has something to stop
        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        await guider.GuideAsync(0.3, 3, 30, ct);
        await ctx.TimeProvider.SleepAsync(TimeSpan.FromSeconds(25), ct); // let guider settle

        (await guider.IsGuidingAsync(ct)).ShouldBeTrue("guider should be guiding before finalise");
        (await mount.IsTrackingAsync(ct)).ShouldBeTrue("mount should be tracking before finalise");

        // Run finalise with time pump
        var finaliseTask = ctx.Track(Task.Run(async () => await ctx.Session.Finalise(ctx.Token), ctx.Token));

        while (!finaliseTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromSeconds(1), ct);
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
    private async Task<(Session Session, FakeTimeProviderWrapper TimeProvider, FakeExternal External, FakeCoverDriver Cover)> CreateSessionWithCoverAsync(CancellationToken ct)
    {
        var timeProvider = new FakeTimeProviderWrapper(WinterNight);
        var external = new FakeExternal(output, timeProvider);
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
        await mount.Driver.SetUTCDateAsync(timeProvider.GetUtcNow().UtcDateTime, ct);

        var setup = new Setup(mount, guider, new GuiderSetup(), [ota]);
        var config = SessionTestHelper.DefaultConfiguration;
        var session = new Session(setup, config, new FakePlateSolver(), external, sp, new ScheduledObservationTree(SessionTestHelper.DefaultScheduledObservations));

        var coverDriver = (FakeCoverDriver)cover.Driver;
        return (session, timeProvider, external, coverDriver);
    }

    [Fact(Timeout = 120_000)]
    public async Task GivenClosedCoverWhenOpenThenCoverOpens()
    {
        var ct = TestContext.Current.CancellationToken;
        var (session, timeProvider, external, coverDriver) = await CreateSessionWithCoverAsync(ct);

        // Cover starts Closed
        (await coverDriver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.Closed);

        // Open covers: needs time pump for the Moving → Open transition and calibrator check
        // No SessionTestContext here (this test builds its own session), so own the background
        // task directly: teardown cancels work.Token and awaits it, which keeps the loop from
        // logging into a finished test.
        await using var work = new BackgroundWork(ct);
        var openTask = work.Track(Task.Run(async () => await session.MoveTelescopeCoversToStateAsync(CoverStatus.Open, work.Token), work.Token));

        while (!openTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await timeProvider.SleepAsync(TimeSpan.FromSeconds(1), ct);
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
        var (session, timeProvider, external, coverDriver) = await CreateSessionWithCoverAsync(ct);

        // Open the cover first
        await coverDriver.BeginOpen(ct);
        await timeProvider.SleepAsync(TimeSpan.FromSeconds(10), ct); // let timer fire
        (await coverDriver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.Open);

        // Close covers
        // No SessionTestContext here (this test builds its own session), so own the background
        // task directly: teardown cancels work.Token and awaits it, which keeps the loop from
        // logging into a finished test.
        await using var work = new BackgroundWork(ct);
        var closeTask = work.Track(Task.Run(async () => await session.MoveTelescopeCoversToStateAsync(CoverStatus.Closed, work.Token), work.Token));

        while (!closeTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await timeProvider.SleepAsync(TimeSpan.FromSeconds(1), ct);
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
        var (session, timeProvider, external, coverDriver) = await CreateSessionWithCoverAsync(ct);

        // Turn calibrator on
        await coverDriver.BeginCalibratorOn(128, ct);
        (await coverDriver.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Ready);
        (await coverDriver.GetBrightnessAsync(ct)).ShouldBe(128);

        // Open cover: should turn off calibrator first, then open
        // No SessionTestContext here (this test builds its own session), so own the background
        // task directly: teardown cancels work.Token and awaits it, which keeps the loop from
        // logging into a finished test.
        await using var work = new BackgroundWork(ct);
        var openTask = work.Track(Task.Run(async () => await session.MoveTelescopeCoversToStateAsync(CoverStatus.Open, work.Token), work.Token));

        while (!openTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await timeProvider.SleepAsync(TimeSpan.FromSeconds(1), ct);
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
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

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

        output.WriteLine($"Guider focus loop succeeded, plate solved at RA={mountRa:F4}h, Dec={mountDec:F2}°");
    }

    // --- InitialRoughFocusAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenSyntheticStarsWhenInitialRoughFocusThenDetectsStars()
    {
        var ct = TestContext.Current.CancellationToken;
        // LX200 mount needed: InitialRoughFocusAsync slews internally via WaitForSlewCompleteAsync
        // which requires the serial protocol's timer-based slew to interleave with SleepAsync pumping.
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, mountPort: "LX200", cancellationToken: ct);

        // Enable synthetic star field rendering at best focus
        ctx.Camera.TrueBestFocus = 1000;
        ctx.Camera.FocusPosition = 1000;

        await ctx.Focuser.BeginMoveAsync(1000, ct);
        while (await ctx.Focuser.GetIsMovingAsync(ct))
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), ct);
        }

        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        // InitialRoughFocusAsync: slews to zenith, guider plate solve, then takes
        // short exposures looking for ≥15 stars. With synthetic star field at best
        // focus (1000mm FL, 512×512 sensor), we should detect enough stars.
        var roughFocusTask = ctx.Track(Task.Run(
            async () => await ctx.Session.InitialRoughFocusAsync(ctx.Token), ctx.Token));

        while (!roughFocusTask.IsCompleted && !ct.IsCancellationRequested)
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromSeconds(1), ct);
            await Task.Delay(10, ct);
        }

        roughFocusTask.IsCompleted.ShouldBeTrue("InitialRoughFocusAsync should complete within timeout");
        var result = await roughFocusTask;

        result.ShouldBeTrue("rough focus should succeed with synthetic star field at best focus");

        output.WriteLine("Initial rough focus completed, enough stars detected");
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

        // Use fresh session: RunAsync handles all setup internally
        var timeProvider = new FakeTimeProviderWrapper(WinterNight);
        var external = new FakeExternal(output, timeProvider);
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

        var session = new Session(setup, config, plateSolver, external, sp, new ScheduledObservationTree(observations));

        // Move focuser to best focus before RunAsync
        var focuserDriver = (FakeFocuserDriver)focuser.Driver;
        await focuserDriver.BeginMoveAsync(1000, ct);

        // RunAsync on background thread, pump time from test thread
        // No SessionTestContext here (this test builds its own session), so own the background
        // task directly: teardown cancels work.Token and awaits it, which keeps the loop from
        // logging into a finished test.
        await using var work = new BackgroundWork(ct);
        var runTask = work.Track(Task.Run(async () => await session.RunAsync(work.Token), work.Token));

        var maxPumps = (int)(TimeSpan.FromHours(24) / subExposure);
        for (var i = 0; i < maxPumps && !runTask.IsCompleted && !ct.IsCancellationRequested; i++)
        {
            await timeProvider.SleepAsync(subExposure, ct);
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
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        var mountTime = await ctx.Session.GetMountUtcNowAsync(ct);
        var providerTime = ctx.TimeProvider.GetUtcNow().UtcDateTime;

        // Mount time should be close to the time provider's time (may differ by serial round-trip)
        Math.Abs((mountTime - providerTime).TotalSeconds).ShouldBeLessThan(5);

        // Advance time and verify mount time follows
        await ctx.TimeProvider.SleepAsync(TimeSpan.FromMinutes(10), ct);
        var mountTimeAfter = await ctx.Session.GetMountUtcNowAsync(ct);
        mountTimeAfter.ShouldBeGreaterThan(mountTime);

        output.WriteLine($"Mount time: {mountTime:u} → {mountTimeAfter:u}");
    }

    // --- WriteImageToFitsFileAsync ---

    [Fact(Timeout = 120_000)]
    public async Task GivenImageWhenWriteToFitsThenFileCreatedOnDisk()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

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
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

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
        // It won't rise: it's past the meridian and descending.
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, now: WinterNight, cancellationToken: ct);

        var target = new Target(3.79, 24.12, "M45", null);
        var result = await ctx.Session.EstimateTimeUntilTargetRisesAsync(target, 70, TimeSpan.FromHours(2), ct);

        result.ShouldBeNull("setting target should return null (not rising)");

        output.WriteLine("M45 is setting, correctly returned null");
    }
}
