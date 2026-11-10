using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The fake focuser and filter wheel (<see cref="FakePositionBasedDriver"/>) move by the time that ELAPSED between timer callbacks,
/// at 200 steps a second, and not by one step per callback (#2022): on the real clock Windows fires a 5 ms timer every 15.6 ms,
/// so a step per callback ran at about 64 steps a second. The tests' fake clock fires the timer exactly on its period, which is
/// why no other test could see it; the late-tick tests here drive the callback from a clock a test moves by hand.
/// </summary>
public class FakePositionBasedDriverElapsedTests(ITestOutputHelper output)
{
    private static readonly TimeSpan FiveMilliseconds = TimeSpan.FromMilliseconds(5);

    /// <summary>A tick that arrives late: Windows' timer resolution, 15.6 ms, as ticks.</summary>
    private static readonly TimeSpan LateTick = TimeSpan.FromTicks(156_000);

    private async Task<(FakeFocuserDriver Focuser, int Start)> FocuserAsync(ITimeProvider clock, CancellationToken ct)
    {
        var services = new ServiceCollection()
            .AddSingleton<IExternal>(new FakeExternal(output))
            .AddSingleton(clock)
            .AddLogging()
            .BuildServiceProvider();
        var focuser = new FakeFocuserDriver(new FakeDevice(DeviceType.Focuser, 1), services);
        await focuser.ConnectAsync(ct);
        return (focuser, await focuser.GetPositionAsync(ct));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(40)]
    [InlineData(-25)]
    public async Task AMoveOnTheFakeClockTakesFiveMillisecondsAStep(int steps)
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 11, 10, 22, 0, 0, TimeSpan.Zero));
        var (focuser, start) = await FocuserAsync(time, ct);
        var count = Math.Abs(steps);
        var direction = Math.Sign(steps);

        await focuser.BeginMoveAsync(start + steps, ct);
        (await focuser.GetIsMovingAsync(ct)).ShouldBeTrue();

        time.Advance(FiveMilliseconds);
        (await focuser.GetPositionAsync(ct)).ShouldBe(start + direction, "one callback on the period is one step");

        time.Advance(FiveMilliseconds * (count - 2));
        (await focuser.GetPositionAsync(ct)).ShouldBe(start + direction * (count - 1), "a step short of the target after N-1 periods");
        (await focuser.GetIsMovingAsync(ct)).ShouldBeTrue();

        time.Advance(FiveMilliseconds);
        (await focuser.GetPositionAsync(ct)).ShouldBe(start + steps, "N steps take N x 5 ms");
        (await focuser.GetIsMovingAsync(ct)).ShouldBeFalse("and the move ends on the callback that arrives");

        time.Advance(TimeSpan.FromSeconds(1));
        (await focuser.GetPositionAsync(ct)).ShouldBe(start + steps, "a finished move stays where it ended");
    }

    [Fact(Timeout = 30_000)]
    public async Task TheFilterWheelTurnsAtTheSameRate()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 11, 10, 22, 0, 0, TimeSpan.Zero));
        var wheel = new FakeFilterWheelDriver(new FakeDevice(DeviceType.FilterWheel, 1),
            new FakeExternal(output, time).BuildServiceProvider());
        await wheel.ConnectAsync(ct);

        await wheel.BeginMoveAsync(3, ct);
        time.Advance(FiveMilliseconds * 2);
        (await wheel.GetPositionAsync(ct)).ShouldBe(-1, "a wheel on its way reports no position");

        time.Advance(FiveMilliseconds);
        (await wheel.GetPositionAsync(ct)).ShouldBe(3, "three slots take 15 ms");
    }

    [Fact(Timeout = 30_000)]
    public async Task ALateTickStillAdvancesTwoHundredStepsASecondAndLandsOnTheTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new HandClock();
        var (focuser, start) = await FocuserAsync(clock, ct);

        await focuser.BeginMoveAsync(start + 100, ct);
        clock.Timers.Count.ShouldBe(1);

        // 25 callbacks, each 15.6 ms after the one before: 390 ms, which is 78 steps at 200 a second. A step a callback is 25.
        for (var callback = 1; callback <= 25; callback++)
        {
            clock.TickAfter(LateTick);
        }

        (await focuser.GetPositionAsync(ct)).ShouldBe(start + 78, "the position follows the clock, not the callback count");
        (await focuser.GetIsMovingAsync(ct)).ShouldBeTrue();

        // 100 steps are 500 ms. The 32nd callback (499.2 ms, 99.84 steps, rounded) lands on the target and stops the move,
        // and the 33rd (514.8 ms, 102.96 steps) finds its timer gone: the position must end ON the target, never past it.
        var previous = start + 78;
        for (var callback = 26; callback <= 33; callback++)
        {
            clock.TickAfter(LateTick);
            var position = await focuser.GetPositionAsync(ct);
            position.ShouldBeGreaterThanOrEqualTo(previous, "a move never goes backwards");
            position.ShouldBeLessThanOrEqualTo(start + 100, "and never past its target");
            previous = position;
        }

        previous.ShouldBe(start + 100);
        (await focuser.GetIsMovingAsync(ct)).ShouldBeFalse();
        clock.Timers[0].Disposed.ShouldBeTrue("the timer stops once the target is reached");
    }

    [Fact(Timeout = 30_000)]
    public async Task TheFractionOfAStepALateTickLeavesCarriesIntoTheNextOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new HandClock();
        var (focuser, start) = await FocuserAsync(clock, ct);

        await focuser.BeginMoveAsync(start + 500, ct);

        // 7 ms a callback is 1.4 steps. Rounded per callback that is a step each (5 in 35 ms); carried it is 1, 3, 4, 6 and 7.
        int[] expected = [1, 3, 4, 6, 7];
        foreach (var steps in expected)
        {
            clock.TickAfter(TimeSpan.FromMilliseconds(7));
            (await focuser.GetPositionAsync(ct)).ShouldBe(start + steps);
        }

        // And the rate over the whole run is the nominal one to the step: 142 callbacks of 7 ms are 994 ms, 198.8 steps.
        for (var callback = 6; callback <= 142; callback++)
        {
            clock.TickAfter(TimeSpan.FromMilliseconds(7));
        }

        (await focuser.GetPositionAsync(ct)).ShouldBe(start + 199, "994 ms at 200 steps a second is 198.8 steps, rounded");
    }

    [Fact(Timeout = 30_000)]
    public async Task AOneLateTickOwingMoreThanTheDistanceStopsOnTheTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new HandClock();
        var (focuser, start) = await FocuserAsync(clock, ct);

        await focuser.BeginMoveAsync(start - 10, ct);
        clock.TickAfter(TimeSpan.FromSeconds(1));

        (await focuser.GetPositionAsync(ct)).ShouldBe(start - 10, "a second owes 200 steps and 10 are left");
        (await focuser.GetIsMovingAsync(ct)).ShouldBeFalse();
        clock.Timers[0].Disposed.ShouldBeTrue();

        clock.Timers[0].FireEvenIfDisposed();
        (await focuser.GetPositionAsync(ct)).ShouldBe(start - 10, "a callback already on its way finds nothing to do");
    }

    [Fact(Timeout = 30_000)]
    public async Task AHaltedMoveIsNotMovedByACallbackAlreadyOnItsWay()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new HandClock();
        var (focuser, start) = await FocuserAsync(clock, ct);

        await focuser.BeginMoveAsync(start + 100, ct);
        clock.TickAfter(LateTick);
        (await focuser.GetPositionAsync(ct)).ShouldBe(start + 3);

        await focuser.BeginHaltAsync(ct);
        clock.Advance(LateTick);
        clock.Timers[0].FireEvenIfDisposed();

        (await focuser.GetPositionAsync(ct)).ShouldBe(start + 3, "a halt holds the position");
        (await focuser.GetIsMovingAsync(ct)).ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task AStaleCallbackOfAReplacedMoveNeitherStepsNorEndsTheNewerMove()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new HandClock();
        var (focuser, start) = await FocuserAsync(clock, ct);

        await focuser.BeginMoveAsync(start + 2, ct);
        await focuser.BeginMoveAsync(start + 100, ct);
        clock.Timers.Count.ShouldBe(2);

        // The first move's callback was already queued when its timer was replaced; it owes 2 steps, which would end ITS move.
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.Timers[0].FireEvenIfDisposed();

        (await focuser.GetPositionAsync(ct)).ShouldBe(start, "the stale callback moves nothing");
        (await focuser.GetIsMovingAsync(ct)).ShouldBeTrue("and does not end the newer move");
    }

    [Fact(Timeout = 30_000)]
    public async Task ANewMoveCountsItsTimeFromItsOwnStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new HandClock();
        var (focuser, start) = await FocuserAsync(clock, ct);

        await focuser.BeginMoveAsync(start + 100, ct);
        clock.TickAfter(LateTick);
        (await focuser.GetPositionAsync(ct)).ShouldBe(start + 3);

        // A second of the clock passes with no callback; a move begun after it owes nothing for it.
        clock.Advance(TimeSpan.FromSeconds(1));
        await focuser.BeginMoveAsync(start + 3 + 50, ct);
        clock.Timers.Count.ShouldBe(2, "a move has a timer of its own");
        clock.Timers[0].Disposed.ShouldBeTrue("and the move it replaces loses its own");

        clock.TickAfter(FiveMilliseconds);
        (await focuser.GetPositionAsync(ct)).ShouldBe(start + 3 + 1, "the first callback of the new move is its first step");
    }
}
