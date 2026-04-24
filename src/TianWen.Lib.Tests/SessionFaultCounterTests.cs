using Shouldly;
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
}
