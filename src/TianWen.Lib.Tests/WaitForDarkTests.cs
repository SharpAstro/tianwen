using Shouldly;
using System;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync.
/// The method now waits until 10 minutes before the first scheduled observation
/// start time, rather than recomputing twilight from SOFA.
/// </summary>
public class WaitForDarkTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task GivenObservationAlreadyStartedWhenWaitForDarkThenSkipsImmediately()
    {
        // given — observation start was 30 min ago
        var now = new DateTimeOffset(2025, 12, 15, 20, 0, 0, TimeSpan.Zero);
        var obsStart = now - TimeSpan.FromMinutes(30);
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await CreateSessionAsync(obsStart, now, ct);

        var before = ctx.External.TimeProvider.GetUtcNow();

        // when
        await ctx.Session.WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(ct);

        // then
        var waited = ctx.External.TimeProvider.GetUtcNow() - before;
        output.WriteLine($"Waited: {waited}");
        waited.TotalMinutes.ShouldBeLessThan(1, "should skip when observation already started");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenObservationIn2HoursWhenWaitForDarkThenWaitsCorrectDuration()
    {
        // given — observation starts in 2 hours
        var now = new DateTimeOffset(2025, 12, 15, 18, 0, 0, TimeSpan.Zero);
        var obsStart = now + TimeSpan.FromHours(2);
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await CreateSessionAsync(obsStart, now, ct);

        var before = ctx.External.TimeProvider.GetUtcNow();

        // when
        await ctx.Session.WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(ct);

        // then — should wait ~1h50m (2h - 10min)
        var waited = ctx.External.TimeProvider.GetUtcNow() - before;
        output.WriteLine($"Waited: {waited}");
        waited.TotalMinutes.ShouldBeGreaterThan(100, "should wait until 10min before start");
        waited.TotalMinutes.ShouldBeLessThan(120, "should not wait past observation start");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenObservationIn5MinutesWhenWaitForDarkThenSkips()
    {
        // given — observation starts in 5 minutes (within the 10-min window)
        var now = new DateTimeOffset(2025, 12, 15, 20, 0, 0, TimeSpan.Zero);
        var obsStart = now + TimeSpan.FromMinutes(5);
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await CreateSessionAsync(obsStart, now, ct);

        var before = ctx.External.TimeProvider.GetUtcNow();

        // when
        await ctx.Session.WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(ct);

        // then — 5 min < 10 min buffer, diff is negative, should skip
        var waited = ctx.External.TimeProvider.GetUtcNow() - before;
        output.WriteLine($"Waited: {waited}");
        waited.TotalMinutes.ShouldBeLessThan(1, "should skip when within 10-min buffer");
    }

    private static async Task<SessionTestContext> CreateSessionAsync(
        DateTimeOffset observationStart, DateTimeOffset now, CancellationToken ct)
    {
        var external = new FakeExternal(null!, now: now);

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

        var ota = new OTA("Test Telescope", 1000, camera, Cover: null, focuser,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true),
            FilterWheel: null, Switches: null);

        var mountDevice = new FakeDevice(DeviceType.Mount, 1,
            new NameValueCollection { { "latitude", "48.2" }, { "longitude", "16.3" } });
        var guiderDevice = new FakeDevice(DeviceType.Guider, 1);
        var mount = new Mount(mountDevice, external);
        var guider = new Guider(guiderDevice, external);

        await mount.Driver.ConnectAsync(ct);
        await mount.Driver.SetUTCDateAsync(now.UtcDateTime, ct);

        var setup = new Setup(mount, guider, new GuiderSetup(), [ota]);
        var plateSolver = new FakePlateSolver();
        var config = SessionTestHelper.DefaultConfiguration;

        var observations = new[]
        {
            new ScheduledObservation(
                new Target(6.75, 16.7, "M42", null),
                observationStart,
                TimeSpan.FromMinutes(30),
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(120)),
                Gain: 0,
                Offset: 0
            )
        };

        var session = new Session(setup, config, plateSolver, external, new ScheduledObservationTree(observations));

        return new SessionTestContext(session, external, cameraDriver, (FakeFocuserDriver)focuser.Driver, mount.Driver);
    }
}
