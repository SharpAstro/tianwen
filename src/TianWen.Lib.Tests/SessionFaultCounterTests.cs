using Shouldly;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

public class SessionFaultCounterTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GivenNoReconnectsWhenGetFaultCountThenReturnsZero()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        ctx.Session.GetFaultCount(ctx.Mount).ShouldBe(0);
        ctx.Session.TryFindEscalatedDriver().ShouldBeNull();
    }

    [Fact]
    public async Task GivenReconnectsBelowThresholdWhenCheckingEscalationThenNull()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        ctx.Session.OnDriverReconnect(ctx.Mount);
        ctx.Session.OnDriverReconnect(ctx.Mount);
        ctx.Session.OnDriverReconnect(ctx.Mount);

        ctx.Session.GetFaultCount(ctx.Mount).ShouldBe(3);
        // Default threshold is 5, so 3 reconnects is below it.
        ctx.Session.TryFindEscalatedDriver().ShouldBeNull();
    }

    [Fact]
    public async Task GivenReconnectsReachingThresholdWhenCheckingEscalationThenReturnsDriver()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < 5; i++)
        {
            ctx.Session.OnDriverReconnect(ctx.Mount);
        }

        ctx.Session.GetFaultCount(ctx.Mount).ShouldBe(5);
        ctx.Session.TryFindEscalatedDriver().ShouldBe(ctx.Mount);
    }

    [Fact]
    public async Task GivenFaultsAndSustainedFramesWhenDecayingThenCounterDecrements()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        // Default DeviceFaultDecayFrames is 10, so 10 successful frames decay by 1.
        ctx.Session.OnDriverReconnect(ctx.Mount);
        ctx.Session.OnDriverReconnect(ctx.Mount);
        ctx.Session.GetFaultCount(ctx.Mount).ShouldBe(2);

        for (var i = 0; i < 10; i++)
        {
            ctx.Session.DecayFaultCountersOnFrameSuccess();
        }

        ctx.Session.GetFaultCount(ctx.Mount).ShouldBe(1);

        for (var i = 0; i < 10; i++)
        {
            ctx.Session.DecayFaultCountersOnFrameSuccess();
        }

        ctx.Session.GetFaultCount(ctx.Mount).ShouldBe(0);
    }

    [Fact]
    public async Task GivenMultipleDriversWhenOnlyOneEscalatesThenReturnsThatDriver()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        ctx.Session.OnDriverReconnect(ctx.Camera);
        for (var i = 0; i < 5; i++)
        {
            ctx.Session.OnDriverReconnect(ctx.Focuser);
        }

        ctx.Session.GetFaultCount(ctx.Camera).ShouldBe(1);
        ctx.Session.GetFaultCount(ctx.Focuser).ShouldBe(5);
        ctx.Session.TryFindEscalatedDriver().ShouldBe(ctx.Focuser);
    }

    [Fact]
    public async Task GivenFailingPollWhenBelowThresholdThenReturnsFallbackAndIncrementsCounter()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        static ValueTask<double> Failing(CancellationToken _) => throw new IOException("simulated poll fault");

        var result = await ctx.Session.PollDriverReadAsync(ctx.Mount, Failing, fallback: 42.0, TestContext.Current.CancellationToken);

        result.ShouldBe(42.0);
        ctx.Session.GetConsecutivePollFailures(ctx.Mount).ShouldBe(1);
        // Below threshold (3), proactive reconnect not yet fired.
        ctx.Session.GetFaultCount(ctx.Mount).ShouldBe(0);
    }

    [Fact]
    public async Task GivenFailingPollWhenThresholdCrossedThenTriggersProactiveReconnect()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        static ValueTask<double> Failing(CancellationToken _) => throw new IOException("simulated poll fault");

        for (var i = 0; i < 3; i++)
        {
            await ctx.Session.PollDriverReadAsync(ctx.Mount, Failing, fallback: 0.0, TestContext.Current.CancellationToken);
        }

        ctx.Session.GetConsecutivePollFailures(ctx.Mount).ShouldBe(3);
        // Proactive reconnect fired exactly once on the threshold crossing.
        ctx.Session.GetFaultCount(ctx.Mount).ShouldBe(1);
    }

    [Fact]
    public async Task GivenFailingPollRepeatedlyBeyondThresholdWhenPollingThenOnlyOneProactiveReconnect()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        static ValueTask<double> Failing(CancellationToken _) => throw new IOException("simulated poll fault");

        // 5 failures in a row — threshold is 3, so reconnect should fire once, not thrice.
        for (var i = 0; i < 5; i++)
        {
            await ctx.Session.PollDriverReadAsync(ctx.Mount, Failing, fallback: 0.0, TestContext.Current.CancellationToken);
        }

        ctx.Session.GetConsecutivePollFailures(ctx.Mount).ShouldBe(5);
        ctx.Session.GetFaultCount(ctx.Mount).ShouldBe(1);
    }

    [Fact]
    public async Task GivenSuccessAfterFailuresWhenPollingThenCounterResets()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        static ValueTask<double> Failing(CancellationToken _) => throw new IOException("simulated poll fault");
        static ValueTask<double> Succeeding(CancellationToken _) => ValueTask.FromResult(123.0);

        await ctx.Session.PollDriverReadAsync(ctx.Mount, Failing, fallback: 0.0, TestContext.Current.CancellationToken);
        await ctx.Session.PollDriverReadAsync(ctx.Mount, Failing, fallback: 0.0, TestContext.Current.CancellationToken);
        ctx.Session.GetConsecutivePollFailures(ctx.Mount).ShouldBe(2);

        var result = await ctx.Session.PollDriverReadAsync(ctx.Mount, Succeeding, fallback: 0.0, TestContext.Current.CancellationToken);

        result.ShouldBe(123.0);
        ctx.Session.GetConsecutivePollFailures(ctx.Mount).ShouldBe(0);
    }

    [Fact]
    public async Task GivenIncapableDriverWhenPollDriverReadAsyncIfThenReturnsFallbackWithoutCallingOp()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        var calls = 0;
        ValueTask<double> CountedOp(CancellationToken _)
        {
            calls++;
            return ValueTask.FromResult(42.0);
        }

        var result = await ctx.Session.PollDriverReadAsyncIf(
            ctx.Mount, capable: false, CountedOp, fallback: -1.0, TestContext.Current.CancellationToken);

        result.ShouldBe(-1.0);
        calls.ShouldBe(0);
        ctx.Session.GetConsecutivePollFailures(ctx.Mount).ShouldBe(0);
    }

    [Fact]
    public async Task GivenCapableDriverWhenPollDriverReadAsyncIfThenCallsOp()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        static ValueTask<double> Succeeding(CancellationToken _) => ValueTask.FromResult(77.0);

        var result = await ctx.Session.PollDriverReadAsyncIf(
            ctx.Mount, capable: true, Succeeding, fallback: -1.0, TestContext.Current.CancellationToken);

        result.ShouldBe(77.0);
    }
}
