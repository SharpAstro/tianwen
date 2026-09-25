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
}
