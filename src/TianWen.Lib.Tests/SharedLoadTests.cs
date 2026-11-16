using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="SharedLoad"/> is the one load behind a process-wide database (the filter curves): started by the
/// first caller, shared by the ones that come while it runs, never cancelled by any of them, and started again
/// after a failure. Each test builds a load of its own, so none depends on what another test loaded first, which
/// a test through the static database could not avoid (#2162's review).
/// </summary>
public class SharedLoadTests
{
    [Fact(Timeout = 30_000)]
    public async Task CallersThatComeWhileItRunsShareOneLoad()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = new SharedLoad(async () =>
        {
            Interlocked.Increment(ref started);
            await release.Task;
        });

        // Eight threads ask at once. Each outer task ends when its caller HAS asked and holds the load's task, so
        // all eight are in before the load is let go.
        var asking = Enumerable.Range(0, 8).Select(_ => Task.Run(() => new[] { load.RunAsync().AsTask() }, ct)).ToArray();
        var waiting = (await Task.WhenAll(asking).WaitAsync(ct)).Select(held => held[0]).ToArray();
        load.IsDone.ShouldBeFalse("the load has not run to its end yet");
        waiting.ShouldAllBe(t => !t.IsCompleted);

        release.SetResult();
        await Task.WhenAll(waiting).WaitAsync(ct);

        started.ShouldBe(1);
        load.IsDone.ShouldBeTrue();

        await load.RunAsync();
        started.ShouldBe(1, "a finished load is not run again");
    }

    [Fact(Timeout = 30_000)]
    public async Task AFailedLoadIsSeenByItsCallersAndStartedAgainByTheNext()
    {
        var runs = 0;
        var load = new SharedLoad(() =>
        {
            return Interlocked.Increment(ref runs) == 1
                ? Task.FromException(new InvalidOperationException("the fetch dropped"))
                : Task.CompletedTask;
        });

        InvalidOperationException? seen = null;
        try
        {
            await load.RunAsync();
        }
        catch (InvalidOperationException ex)
        {
            seen = ex;
        }

        seen.ShouldNotBeNull().Message.ShouldBe("the fetch dropped");
        load.IsDone.ShouldBeFalse();

        await load.RunAsync();

        runs.ShouldBe(2);
        load.IsDone.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task ItIsDoneOnlyOnceTheLoadHasRunToItsEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = new SharedLoad(async () =>
        {
            entered.SetResult();
            await release.Task;
        });

        var running = load.RunAsync().AsTask();
        await entered.Task.WaitAsync(ct);
        load.IsDone.ShouldBeFalse("the data is not there while the load is still running");

        release.SetResult();
        await running.WaitAsync(ct);
        load.IsDone.ShouldBeTrue();
    }
}
