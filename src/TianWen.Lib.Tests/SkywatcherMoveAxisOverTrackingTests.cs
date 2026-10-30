using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Skywatcher;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A held button on a tracking Skywatcher (the owner, 2026-10-20: "slew speed 1 and 2 ... should behave more like guide pulses, not
/// stop tracking when moving"): an RA move-axis rides on tracking, its rate added to sidereal, and its stop returns the axis to
/// sidereal tracking. A slow move the tracking way changes only the step period, as an RA pulse does; one that reverses the axis stops
/// and restarts it; neither ever leaves the mount untracked.
/// </summary>
[Collection("Skywatcher")]
public class SkywatcherMoveAxisOverTrackingTests(ITestOutputHelper output)
{
    private static readonly double Sidereal = MountJog.RateDegPerSec(1);

    private async Task<(IMountDriver Driver, FakeSkywatcherSerialDevice Serial)> ConnectAsync(double latitude, bool tracking, CancellationToken ct)
    {
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var sp = new FakeExternal(output, timeProvider).BuildServiceProvider();
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "latitude", latitude.ToString(CultureInfo.InvariantCulture) },
            { "longitude", "16.3" },
            { "elevation", "200" },
            { "port", "SkyWatcher" },
        });
        var driver = new Mount(device, sp).Driver;
        await driver.ConnectAsync(ct);
        // The site as a session pushes it: the hemisphere decides which way the RA worm tracks.
        await driver.SetSiteLatitudeAsync(latitude, ct);
        if (tracking)
        {
            await driver.SetTrackingAsync(true, ct);
        }
        var serial = ((SkywatcherMountDriverBase<FakeDevice>)driver).SerialConnection.ShouldBeOfType<FakeSkywatcherSerialDevice>();
        return (driver, serial);
    }

    private static int Stops(FakeSkywatcherSerialDevice serial) => serial.CommandLogSnapshot.Count(c => c.StartsWith(":K1", StringComparison.Ordinal));

    [Theory(Timeout = 30_000)]
    [InlineData(GuideDirection.West, 1, 2.0)]
    [InlineData(GuideDirection.West, 2, 3.0)]
    [InlineData(GuideDirection.West, 32, 33.0)]
    [InlineData(GuideDirection.East, 0.5, 0.5)]
    public async Task ASlowMoveTheTrackingWayOnlyChangesTheRateAndItsStopRestoresSidereal(GuideDirection direction, double speed, double expectedTimesSidereal)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectAsync(48.2, tracking: true, ct);
        var stopsBefore = Stops(serial);
        var (axis, rate) = MountJog.AxisRateFor(direction, speed, 48.2);

        await driver.MoveAxisAsync(axis, rate, ct);

        var moving = serial.RaMotion;
        (moving.Running, moving.Forward, moving.HighSpeed).ShouldBe((true, true, false), "still turning the tracking way at low speed");
        moving.DegPerSec.ShouldBe(Sidereal * expectedTimesSidereal, Sidereal * 0.01);
        Stops(serial).ShouldBe(stopsBefore, "no stop: the step period changes live, as a pulse's does");

        await driver.MoveAxisAsync(axis, 0, ct);

        serial.RaMotion.DegPerSec.ShouldBe(Sidereal, Sidereal * 0.01);
        Stops(serial).ShouldBe(stopsBefore, "the release only restores the step period");
        (await driver.IsTrackingAsync(ct)).ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task EastAtOneSiderealHaltsTheAxisAgainstTheGroundWithoutAStop()
    {
        // Tracking cancelled: the mount stands still against the ground, so the sky drifts through the view at sidereal, which is
        // the East move at 1x. The axis is not stopped (no :K, no transient): it creeps at a thousandth of sidereal.
        var ct = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectAsync(48.2, tracking: true, ct);
        var stopsBefore = Stops(serial);

        await driver.MoveAxisAsync(TelescopeAxis.Primary, -Sidereal, ct);

        serial.RaMotion.Running.ShouldBeTrue("the boards cannot encode a zero period, so it creeps");
        serial.RaMotion.DegPerSec.ShouldBeLessThan(Sidereal * 0.01);
        Stops(serial).ShouldBe(stopsBefore);
        await driver.MoveAxisAsync(TelescopeAxis.Primary, 0, ct);
        serial.RaMotion.DegPerSec.ShouldBe(Sidereal, Sidereal * 0.01);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(2, 1.0)]
    [InlineData(8, 7.0)]
    public async Task EastFasterThanSiderealReversesTheAxisAndItsStopResumesTrackingForward(double speed, double expectedTimesSidereal)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectAsync(48.2, tracking: true, ct);
        var (axis, rate) = MountJog.AxisRateFor(GuideDirection.East, speed, 48.2);

        await driver.MoveAxisAsync(axis, rate, ct);

        var moving = serial.RaMotion;
        (moving.Running, moving.Forward).ShouldBe((true, false), "against tracking: the axis turns back");
        moving.DegPerSec.ShouldBe(Sidereal * expectedTimesSidereal, Sidereal * expectedTimesSidereal * 0.01);

        await driver.MoveAxisAsync(axis, 0, ct);

        var after = serial.RaMotion;
        (after.Running, after.Forward, after.HighSpeed).ShouldBe((true, true, false));
        after.DegPerSec.ShouldBe(Sidereal, Sidereal * 0.01);
        (await driver.IsTrackingAsync(ct)).ShouldBeTrue("tracking is never left off");
    }

    [Fact(Timeout = 30_000)]
    public async Task AMovePastGssLowSpeedMarginRunsHighSpeedAndItsStopResumesTracking()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectAsync(48.2, tracking: true, ct);

        await driver.MoveAxisAsync(TelescopeAxis.Primary, 3.0, ct);   // the fast slew, about 720x

        var moving = serial.RaMotion;
        (moving.Running, moving.Forward, moving.HighSpeed).ShouldBe((true, true, true));
        moving.DegPerSec.ShouldBe(3.0 + Sidereal, 0.05);

        await driver.MoveAxisAsync(TelescopeAxis.Primary, 0, ct);

        var after = serial.RaMotion;
        (after.Running, after.Forward, after.HighSpeed).ShouldBe((true, true, false));
        after.DegPerSec.ShouldBe(Sidereal, Sidereal * 0.01);
    }

    [Fact(Timeout = 30_000)]
    public async Task InTheSouthWestIsStillFasterThanTrackingWhichRunsInReverse()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectAsync(-33.9, tracking: true, ct);
        serial.RaMotion.Forward.ShouldBeFalse("southern tracking runs the worm in reverse");
        var (axis, rate) = MountJog.AxisRateFor(GuideDirection.West, 1, -33.9);

        await driver.MoveAxisAsync(axis, rate, ct);

        (serial.RaMotion.Forward, serial.RaMotion.HighSpeed).ShouldBe((false, false));
        serial.RaMotion.DegPerSec.ShouldBe(Sidereal * 2, Sidereal * 0.01);
        await driver.MoveAxisAsync(axis, 0, ct);
        serial.RaMotion.DegPerSec.ShouldBe(Sidereal, Sidereal * 0.01);
    }

    [Fact(Timeout = 30_000)]
    public async Task OnAMountNotTrackingAMoveRunsAtItsRateAndItsStopStopsTheAxis()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectAsync(48.2, tracking: false, ct);

        await driver.MoveAxisAsync(TelescopeAxis.Primary, Sidereal, ct);
        serial.RaMotion.DegPerSec.ShouldBe(Sidereal, Sidereal * 0.01, "no tracking to ride on: the rate as given");

        await driver.MoveAxisAsync(TelescopeAxis.Primary, 0, ct);
        Stops(serial).ShouldBeGreaterThan(0);
        (await driver.IsTrackingAsync(ct)).ShouldBeFalse("a mount not tracking is not started by a move");
    }

    [Theory]
    [InlineData(GuideDirection.West, 48.2, TelescopeAxis.Primary, 1)]
    [InlineData(GuideDirection.East, 48.2, TelescopeAxis.Primary, -1)]
    [InlineData(GuideDirection.West, -33.9, TelescopeAxis.Primary, -1)]
    [InlineData(GuideDirection.North, -33.9, TelescopeAxis.Seconary, 1)]
    [InlineData(GuideDirection.South, 48.2, TelescopeAxis.Seconary, -1)]
    public void ADirectionIsTheAxisAndTheSignAPulseInItWouldTake(GuideDirection direction, double latitude, TelescopeAxis axis, int sign)
    {
        var (got, rate) = MountJog.AxisRateFor(direction, 2, latitude);

        got.ShouldBe(axis);
        rate.ShouldBe(sign * MountJog.RateDegPerSec(2));
    }

    [Fact(Timeout = 30_000)]
    public async Task EveryHeldButtonSpeedIsOneTheSkywatcherMovesAtExactly()
    {
        // The node refuses a rate outside the mount's AxisRates, compared as doubles: one list, so they cannot disagree.
        // Max is the one exception: the node clamps it to the mount's top, so it is never compared with the list.
        var (driver, _) = await ConnectAsync(48.2, tracking: false, TestContext.Current.CancellationToken);

        foreach (var multiple in MountJog.SiderealMultiples)
        {
            if (multiple == MountJog.Max)
            {
                continue;
            }

            var rate = MountJog.RateDegPerSec(multiple);
            driver.AxisRates(TelescopeAxis.Primary).ShouldContain(r => r.Mininum <= rate && rate <= r.Maximum, $"{multiple}x on RA");
            driver.AxisRates(TelescopeAxis.Seconary).ShouldContain(r => r.Mininum <= rate && rate <= r.Maximum, $"{multiple}x on Dec");
        }
    }
}
