using NSubstitute;
using Shouldly;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A compensated move commands the focuser and polls it only through the <see cref="FocuserMotion"/> it is
/// handed, which is how the session's resilient reads reach the move-wait (#781). The driver here throws
/// on any direct call, so a helper that bypassed the motion fails the test.
/// </summary>
public class BacklashCompensationMotionTests
{
    [Fact]
    public async Task EveryMoveAndPollGoesThroughTheSuppliedMotionNeverTheDriver()
    {
        var ct = TestContext.Current.CancellationToken;
        var focuser = Substitute.For<IFocuserDriver>();
        focuser.MaxStep.Returns(10_000);
        focuser.GetIsMovingAsync(Arg.Any<CancellationToken>()).Returns<ValueTask<bool>>(_ => throw new IOException("polled the driver directly"));
        focuser.BeginMoveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("moved the driver directly"));

        var moves = new List<int>();
        var polls = 0;
        var motion = new FocuserMotion(
            (position, _) => { moves.Add(position); return ValueTask.CompletedTask; },
            _ => ValueTask.FromResult(++polls % 2 == 1)); // each move: moving once, then arrived

        // Against the preferred (outward, positive) direction, so the move overshoots and comes back.
        await BacklashCompensation.MoveWithCompensationAsync(
            focuser, motion, targetPosition: 800, currentPosition: 1000, backlashStepsIn: 20, backlashStepsOut: 20,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true), new FakeTimeProviderWrapper(), ct);

        moves.ShouldBe([780, 800]);
        polls.ShouldBe(4);
    }

    [Theory]
    [InlineData(0, new[] { 0, 5 })]
    [InlineData(-1000, new[] { -15, 5 })]
    public async Task AnOvershootBelowZeroStopsAtTheFocusersOwnFloor(int minStep, int[] expectedMoves)
    {
        // From 50 down to 5 against the preferred (outward, positive) direction, with 20 steps of backlash: the
        // overshoot wants -15. A focuser that counts from 0 stops it at 0; one whose counter goes negative (a QHY
        // QFOC, #1763) takes it, so its backlash is taken out like any other's.
        var ct = TestContext.Current.CancellationToken;
        var focuser = Substitute.For<IFocuserDriver>();
        focuser.MinStep.Returns(minStep);
        focuser.MaxStep.Returns(10_000);

        var moves = new List<int>();
        var polls = 0;
        var motion = new FocuserMotion(
            (position, _) => { moves.Add(position); return ValueTask.CompletedTask; },
            _ => ValueTask.FromResult(++polls % 2 == 1));

        await BacklashCompensation.MoveWithCompensationAsync(
            focuser, motion, targetPosition: 5, currentPosition: 50, backlashStepsIn: 20, backlashStepsOut: 20,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true), new FakeTimeProviderWrapper(), ct);

        moves.ShouldBe(expectedMoves);
    }
}
