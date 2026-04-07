using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Scheduling")]
public class PlateSolveHistoryTests(ITestOutputHelper output)
{
    [Fact(Timeout = 30_000)]
    public async Task PlateSolveAndSync_RecordsSuccessInHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        ctx.Session.PlateSolveHistory.ShouldBeEmpty();

        var solved = await ctx.Session.PlateSolveAndSyncAsync(0, TimeSpan.FromSeconds(1), ct);
        solved.ShouldBeTrue();

        var history = ctx.Session.PlateSolveHistory;
        history.Length.ShouldBe(1);

        var record = history[0];
        record.Succeeded.ShouldBeTrue();
        record.Context.ShouldBe(PlateSolveContext.MountSync);
        record.OtaName.ShouldBe("Test Telescope");
        record.Solution.ShouldNotBeNull();
    }

    [Fact(Timeout = 30_000)]
    public async Task PlateSolveAndSyncCore_WithCenteringContext_RecordsCorrectContext()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        var (solved, ra, dec) = await ctx.Session.PlateSolveAndSyncCoreAsync(0, TimeSpan.FromSeconds(1), PlateSolveContext.Centering, ct);
        solved.ShouldBeTrue();

        var record = ctx.Session.PlateSolveHistory.ShouldHaveSingleItem();
        record.Context.ShouldBe(PlateSolveContext.Centering);
        record.Succeeded.ShouldBeTrue();
        record.Solution.ShouldNotBeNull();
        double.IsNaN(ra).ShouldBeFalse();
        double.IsNaN(dec).ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task PlateSolveCompleted_EventFires()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        PlateSolveRecord? received = null;
        ctx.Session.PlateSolveCompleted += (_, e) => received = e.Record;

        await ctx.Session.PlateSolveAndSyncAsync(0, TimeSpan.FromSeconds(1), ct);

        received.ShouldNotBeNull();
        received.Value.Succeeded.ShouldBeTrue();
        received.Value.OtaName.ShouldBe("Test Telescope");
    }

    [Fact(Timeout = 30_000)]
    public async Task MultipleSolves_AccumulateInHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        await ctx.Session.PlateSolveAndSyncAsync(0, TimeSpan.FromSeconds(1), ct);
        await ctx.Session.PlateSolveAndSyncCoreAsync(0, TimeSpan.FromSeconds(1), PlateSolveContext.Centering, ct);

        var history = ctx.Session.PlateSolveHistory;
        history.Length.ShouldBe(2);
        history[0].Context.ShouldBe(PlateSolveContext.MountSync);
        history[1].Context.ShouldBe(PlateSolveContext.Centering);
        history[1].Timestamp.ShouldBeGreaterThanOrEqualTo(history[0].Timestamp);
    }
}
