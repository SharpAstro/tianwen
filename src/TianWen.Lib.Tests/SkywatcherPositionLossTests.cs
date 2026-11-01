using Shouldly;
using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Skywatcher;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A SkyWatcher that loses power comes back reading home while it still stands where it was (#1954). It connects, but comes up
/// POSITION LOST: no goto, Home, park, flip, held move or tracking, and no mechanical axis angle, until the operator restores
/// the position from the last one the driver wrote down, from a plate solve, or by saying it was put back at home. In both
/// hemispheres, so no hemisphere assumption passes for a fact.
/// </summary>
[Collection("Skywatcher")]
public sealed class SkywatcherPositionLossTests(ITestOutputHelper output) : IDisposable
{
    private const double Melbourne = -37.8769444;
    private const double Vienna = 48.2;

    // Each test its own app data, where the driver writes the mount's last known position down.
    private readonly TempFolders _temp = new TempFolders();

    public void Dispose() => _temp.Dispose();

    private sealed record Rig(IMountDriver Driver, FakeSkywatcherSerialDevice Board, FakeTimeProviderWrapper Clock);

    private async Task<Rig> ConnectAsync(double latitude, double longitude, CancellationToken ct)
    {
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var sp = new FakeExternal(output, clock, _temp.Create("position-loss")).BuildServiceProvider();
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "latitude", latitude.ToString(CultureInfo.InvariantCulture) },
            { "longitude", longitude.ToString(CultureInfo.InvariantCulture) },
            { "port", "SkyWatcher" },
        });
        var driver = new Mount(device, sp).Driver;
        await driver.ConnectAsync(ct);
        var board = ((SkywatcherMountDriverBase<FakeDevice>)driver).SerialConnection.ShouldBeOfType<FakeSkywatcherSerialDevice>();
        return new Rig(driver, board, clock);
    }

    /// <summary>1.1 h east of the meridian on the equator: through the pole, well away from home on both axes.</summary>
    private static async Task<(double Ra, double Dec)> GotoEastAsync(Rig rig, CancellationToken ct)
    {
        var target = (CoordinateUtils.ConditionRA(await rig.Driver.GetSiderealTimeAsync(ct) + 1.1), 0.0);
        await rig.Driver.BeginSlewRaDecAsync(target.Item1, target.Item2, ct);
        for (var i = 0; i < 60 && await rig.Driver.IsSlewingAsync(ct); i++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
        }
        (await rig.Driver.IsSlewingAsync(ct)).ShouldBeFalse();
        return target;
    }

    /// <summary>Slews away, disconnects, cuts and restores the power, connects again; returns where the encoders stood.</summary>
    private static async Task<(int Ra, int Dec)> LosePowerWhileDisconnectedAsync(Rig rig, CancellationToken ct)
    {
        await GotoEastAsync(rig, ct);
        await rig.Driver.SetTrackingAsync(false, ct);
        await rig.Driver.DisconnectAsync(ct);
        var before = rig.Board.Steps;
        rig.Board.PowerCycle();
        await rig.Driver.ConnectAsync(ct);
        return before;
    }

    [Theory(Timeout = 30_000)]
    [InlineData(Melbourne, 145.1775)]
    [InlineData(Vienna, 16.3)]
    public async Task AMountThatLostPowerConnectsPositionLostAndRefusesEveryMove(double latitude, double longitude)
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ConnectAsync(latitude, longitude, ct);
        var before = await LosePowerWhileDisconnectedAsync(rig, ct);

        rig.Driver.Connected.ShouldBeTrue("an alert, not a refusal: the mount connects");
        var loss = rig.Driver.PositionLoss.ShouldNotBeNull();
        (loss.LastKnown.RaSteps, loss.LastKnown.DecSteps).ShouldBe(before);
        rig.Board.Steps.ShouldBe((0, 0), "the board reads home");

        var lst = await rig.Driver.GetSiderealTimeAsync(ct);
        (await Should.ThrowAsync<InvalidOperationException>(async () => await rig.Driver.BeginSlewRaDecAsync(lst, 0.0, ct)))
            .Message.ShouldContain("lost power");
        await Should.ThrowAsync<InvalidOperationException>(async () => await rig.Driver.FindHomeAsync(ct));
        await Should.ThrowAsync<InvalidOperationException>(async () => await rig.Driver.ParkAsync(ct));
        await Should.ThrowAsync<InvalidOperationException>(async () => await rig.Driver.MoveAxisAsync(TelescopeAxis.Primary, 3.0, ct));
        await Should.ThrowAsync<InvalidOperationException>(async () => await rig.Driver.SetTrackingAsync(true, ct));
        rig.Board.Steps.ShouldBe((0, 0), "nothing moved");

        // Stopping stays allowed, and nothing claims the mount is home or gives its mechanical angle.
        await rig.Driver.MoveAxisAsync(TelescopeAxis.Primary, 0.0, ct);
        await rig.Driver.SetTrackingAsync(false, ct);
        (await rig.Driver.AtHomeAsync(ct)).ShouldBeFalse();
        (await rig.Driver.GetAxisAngleAsync(TelescopeAxis.Primary, ct)).ShouldBeNull();
        (await rig.Driver.GetSideOfPierAsync(ct)).ShouldBe(PointingState.ThroughThePole, "where it last knew itself, not the home half");
    }

    [Theory(Timeout = 30_000)]
    [InlineData(Melbourne, 145.1775)]
    [InlineData(Vienna, 16.3)]
    public async Task ASyncFromTheLastKnownPositionPutsTheEncodersBackAndTheMountMovesAgain(double latitude, double longitude)
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ConnectAsync(latitude, longitude, ct);
        var before = await LosePowerWhileDisconnectedAsync(rig, ct);

        await rig.Driver.RestorePositionAsync(MountPositionRestore.LastKnown, ct);

        rig.Driver.PositionLoss.ShouldBeNull();
        rig.Board.Steps.ShouldBe(before);
        (await rig.Driver.GetAxisAngleAsync(TelescopeAxis.Primary, ct)).ShouldNotBeNull();
        await GotoEastAsync(rig, ct);
    }

    [Fact(Timeout = 30_000)]
    public async Task AMountPutBackAtHomeByHandKeepsTheHomeItReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ConnectAsync(Melbourne, 145.1775, ct);
        await LosePowerWhileDisconnectedAsync(rig, ct);

        await rig.Driver.RestorePositionAsync(MountPositionRestore.AtHome, ct);

        rig.Driver.PositionLoss.ShouldBeNull();
        rig.Board.Steps.ShouldBe((0, 0));
        (await rig.Driver.AtHomeAsync(ct)).ShouldBeTrue();
    }

    [Theory(Timeout = 30_000)]
    [InlineData(Melbourne, 145.1775)]
    [InlineData(Vienna, 16.3)]
    public async Task APlateSolveSyncWhileLostKeepsThePierSideTheMountLastKnew(double latitude, double longitude)
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ConnectAsync(latitude, longitude, ct);
        var target = await GotoEastAsync(rig, ct);
        await rig.Driver.SetTrackingAsync(false, ct);
        await rig.Driver.DisconnectAsync(ct);
        var before = rig.Board.Steps;
        rig.Board.PowerCycle();
        await rig.Driver.ConnectAsync(ct);

        // The solve finds the mount half an hour WEST of the meridian, through the pole: an unflipped rig that tracked past the
        // meridian. There the counterweight-down guess is the other half, so only the half the mount last knew is right.
        var lst = await rig.Driver.GetSiderealTimeAsync(ct);
        await rig.Driver.SyncRaDecAsync(CoordinateUtils.ConditionRA(lst - 0.5), target.Dec, ct);

        rig.Driver.PositionLoss.ShouldBeNull("a sync says where the mount is");
        (rig.Board.Steps.Dec / 9024000.0 * 360.0).ShouldBe(before.Dec / 9024000.0 * 360.0, 0.01,
            "the Dec axis in the half it was in, never the counterweight-down guess from the home it read");
    }

    [Fact(Timeout = 30_000)]
    public async Task AMountThatLostPowerWhileConnectedRefusesTheNextGoto()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ConnectAsync(Melbourne, 145.1775, ct);
        await GotoEastAsync(rig, ct);
        await rig.Driver.SetTrackingAsync(false, ct);
        await rig.Driver.GetRightAscensionAsync(ct);
        var before = rig.Board.Steps;

        rig.Board.PowerCycle();

        var lst = await rig.Driver.GetSiderealTimeAsync(ct);
        (await Should.ThrowAsync<InvalidOperationException>(async () => await rig.Driver.BeginSlewRaDecAsync(lst, -60.0, ct)))
            .Message.ShouldContain("lost power");
        rig.Board.Steps.ShouldBe((0, 0), "nothing moved off the home it reads");
        var loss = rig.Driver.PositionLoss.ShouldNotBeNull();
        (loss.LastKnown.RaSteps, loss.LastKnown.DecSteps).ShouldBe(before);
    }

    [Fact(Timeout = 30_000)]
    public async Task APollAfterAPowerLossWhileConnectedNeverWritesTheHomeItReadsDown()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ConnectAsync(Vienna, 16.3, ct);
        await GotoEastAsync(rig, ct);
        await rig.Driver.SetTrackingAsync(false, ct);
        await rig.Driver.GetRightAscensionAsync(ct);
        var before = rig.Board.Steps;

        rig.Board.PowerCycle();
        await rig.Driver.GetRightAscensionAsync(ct);
        await rig.Driver.GetDeclinationAsync(ct);
        await rig.Driver.DisconnectAsync(ct);
        await rig.Driver.ConnectAsync(ct);

        var loss = rig.Driver.PositionLoss.ShouldNotBeNull();
        (loss.LastKnown.RaSteps, loss.LastKnown.DecSteps).ShouldBe(before, "the position from before the cut, not the home after it");
    }

    [Fact(Timeout = 30_000)]
    public async Task AReconnectWithoutAPowerLossIsNotLost()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ConnectAsync(Melbourne, 145.1775, ct);
        await GotoEastAsync(rig, ct);
        await rig.Driver.DisconnectAsync(ct);
        await rig.Driver.ConnectAsync(ct);

        rig.Driver.PositionLoss.ShouldBeNull();
    }

    [Fact(Timeout = 30_000)]
    public async Task AMountLeftAtHomeThatLostPowerIsNotLost()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ConnectAsync(Melbourne, 145.1775, ct);
        await rig.Driver.DisconnectAsync(ct);
        rig.Board.PowerCycle();
        await rig.Driver.ConnectAsync(ct);

        rig.Driver.PositionLoss.ShouldBeNull("home is home: the encoders reading it are right");
    }
}
