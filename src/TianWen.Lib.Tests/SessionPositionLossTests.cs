using Shouldly;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Skywatcher;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A run whose mount loses its power (#1954): the poll finds it, and the run asks rather than guesses. A yes syncs the mount
/// from its last known position and points the target again; nobody to ask, the unattended answer (decline) ends the run
/// with the loss as its reason. Neither reads the power loss's stop as a mount limit.
/// </summary>
[Collection("Session")]
public class SessionPositionLossTests(ITestOutputHelper output)
{
    private async Task<(SessionTestContext Ctx, FakeSkywatcherSerialDevice Board, (int Ra, int Dec) Before)> TrackingRigThatLosesPowerAsync(CancellationToken ct)
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, mountPort: "SkyWatcher", cancellationToken: ct);
        await ctx.Mount.SetSiteLatitudeAsync(48.2, ct);
        await ctx.Mount.SetSiteLongitudeAsync(16.3, ct);
        var lst = await ctx.Mount.GetSiderealTimeAsync(ct);
        // A sync west of the meridian, away from home, which the driver writes down as the last known position.
        await ctx.Mount.SyncRaDecAsync(((lst - 1.0) % 24.0 + 24.0) % 24.0, 45.0, ct);
        await ctx.Mount.SetTrackingAsync(true, ct);
        await ctx.Session.PollDeviceStatesAsync(ct);
        ctx.Session.MountLimitVerdict.IsBreached.ShouldBeFalse("premise: tracking, nothing wrong");

        var board = ((SkywatcherMountDriverBase<FakeDevice>)ctx.Mount).SerialConnection.ShouldBeOfType<FakeSkywatcherSerialDevice>();
        // The sync wrote the position down; tracking has turned RA a little since, which the next restore does not chase.
        var lastKnown = ((SkywatcherMountDriverBase<FakeDevice>)ctx.Mount).PositionLoss;
        lastKnown.ShouldBeNull("premise");
        var before = board.Steps;
        board.PowerCycle();
        return (ctx, board, before);
    }

    [Fact(Timeout = 60_000)]
    public async Task APowerLossMidRunAsksAndAYesRestoresTheMountAndPointsTheTargetAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ctx, board, before) = await TrackingRigThatLosesPowerAsync(ct);
        string? asked = null;
        ctx.Session.PromptRequested += (_, e) =>
        {
            asked = e.ContinueLabel;
            e.Respond(true);
        };

        await ctx.Session.PollDeviceStatesAsync(ct);

        asked.ShouldBe("Sync from last known position");
        ctx.Mount.PositionLoss.ShouldBeNull("restored");
        (board.Steps.Dec / 9024000.0 * 360.0).ShouldBe(before.Dec / 9024000.0 * 360.0, 0.001, "the Dec axis back where it was");
        ctx.Session.RepointAfterPositionRestorePending.ShouldBeTrue("the power loss stopped tracking: the target is pointed again");
        ctx.Session.PositionLostEndsRun.ShouldBeFalse();

        // Not tracking until the repoint's goto, and that is no limit the mount enforced.
        for (var i = 0; i < 3; i++)
        {
            await ctx.Session.PollDeviceStatesAsync(ct);
        }
        ctx.Session.MountLimitVerdict.IsBreached.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task APowerLossMidRunWithNobodyToAskEndsTheRunWithTheLossAsItsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var (ctx, board, _) = await TrackingRigThatLosesPowerAsync(ct);

        await ctx.Session.PollDeviceStatesAsync(ct);
        await ctx.Session.PollDeviceStatesAsync(ct);

        ctx.Session.PositionLostEndsRun.ShouldBeTrue();
        ctx.Session.FailureReason.ShouldNotBeNull().ShouldContain("lost power");
        ctx.Mount.PositionLoss.ShouldNotBeNull("nothing restored it");
        board.Steps.ShouldBe((0, 0), "and nothing moved it");
        ctx.Session.MountLimitVerdict.IsBreached.ShouldBeFalse("a power loss, not a limit");
    }
}
