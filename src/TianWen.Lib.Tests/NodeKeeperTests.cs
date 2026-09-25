using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// When a keeper starts its node again (P1 of docs/plans/hardware-in-the-server.md, #917, decision 10): after a crash,
/// but not after a clean stop or a start that never happened, and not after a second crash within a few minutes,
/// since a driver that crashes the node would crash every node started after it. The real processes are
/// <c>NodeKeeperProcessTests</c>; this is the rule, on a clock the test turns.
/// </summary>
public class NodeKeeperTests
{
    private const int Crashed = -1;

    /// <summary>A keeper whose node ends with each exit in turn, the clock moved on by the paired gap BEFORE it ends.</summary>
    private static (NodeKeeper Keeper, Func<int> Runs) KeeperOf(FakeTimeProvider clock, params (TimeSpan Before, int Exit)[] exits)
    {
        var queue = new Queue<(TimeSpan Before, int Exit)>(exits);
        var runs = 0;
        var keeper = new NodeKeeper(_ =>
        {
            runs++;
            var (before, exit) = queue.Dequeue();
            clock.Advance(before);
            return Task.FromResult(exit);
        }, clock, NullLogger.Instance);
        return (keeper, () => runs);
    }

    [Fact]
    public async Task ANodeThatCrashesIsStartedAgain()
    {
        var clock = new FakeTimeProvider();
        var (keeper, runs) = KeeperOf(clock, (TimeSpan.FromHours(3), Crashed), (TimeSpan.FromHours(1), NodeExitCodes.Stopped));

        (await keeper.RunAsync(TestContext.Current.CancellationToken)).ShouldBe(NodeExitCodes.Stopped);

        runs().ShouldBe(2);
    }

    [Fact]
    public async Task ASecondCrashWithinTheWindowLeavesTheNodeDown()
    {
        var clock = new FakeTimeProvider();
        var (keeper, runs) = KeeperOf(clock, (TimeSpan.FromHours(3), Crashed), (TimeSpan.FromMinutes(1), Crashed), (TimeSpan.Zero, NodeExitCodes.Stopped));

        (await keeper.RunAsync(TestContext.Current.CancellationToken)).ShouldBe(NodeExitCodes.CrashLoop);

        runs().ShouldBe(2, "the node that crashed twice in a minute is not started a third time");
    }

    [Fact]
    public async Task CrashesFurtherApartThanTheWindowAreEachStartedAgain()
    {
        var clock = new FakeTimeProvider();
        var apart = NodeKeeper.CrashLoopWindow + TimeSpan.FromSeconds(1);
        var (keeper, runs) = KeeperOf(clock, (apart, Crashed), (apart, Crashed), (apart, Crashed), (apart, NodeExitCodes.Stopped));

        (await keeper.RunAsync(TestContext.Current.CancellationToken)).ShouldBe(NodeExitCodes.Stopped);

        runs().ShouldBe(4);
    }

    [Theory]
    [InlineData(NodeExitCodes.Stopped)]
    [InlineData(NodeExitCodes.InvalidArguments)]
    [InlineData(NodeExitCodes.AlreadyRunning)]
    [InlineData(NodeExitCodes.CouldNotStart)]
    public async Task ANodeThatStoppedOrNeverStartedIsNotStartedAgain(int exit)
    {
        var clock = new FakeTimeProvider();
        var (keeper, runs) = KeeperOf(clock, (TimeSpan.Zero, exit), (TimeSpan.Zero, NodeExitCodes.Stopped));

        (await keeper.RunAsync(TestContext.Current.CancellationToken)).ShouldBe(exit);

        runs().ShouldBe(1);
    }
}
