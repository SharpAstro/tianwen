using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What holds a colour twin's mosaics while its three passes run at once: the lead bound and the in-order drain, against
/// fake passes that deposit a blank frame, so nothing here renders a frame or depends on timing.
/// </summary>
public class BayerMosaicAssemblerTests
{
    private const int Width = 2;
    private const int Height = 2;
    private static readonly (int X, int Y)[] PhaseOf = [(0, 0)];

    private static void Deposit(BayerMosaicAssembler assembler, int index, CancellationToken ct)
        => assembler.Deposit(index, new ushort[Width * Height], Width, Height, PhaseOf, ct);

    [Fact(Timeout = 30_000)]
    public async Task APassAtTheLeadBoundWaitsForTheSlowestAndIsEndedByCancellation()
    {
        const int maxLead = 4;
        var ct = TestContext.Current.CancellationToken;
        var assembler = new BayerMosaicAssembler(20, Width * Height, 2, maxLead, (_, _) => { });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var deposited = new SemaphoreSlim(0);
        var fast = Task.Factory.StartNew(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                Deposit(assembler, i, stop.Token);
                deposited.Release();
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // The other pass has deposited nothing, so frame 0 is not written and the fast pass can take frames 0 to maxLead - 1 only.
        for (var i = 0; i < maxLead; i++)
        {
            await deposited.WaitAsync(ct);
        }
        assembler.Next.ShouldBe(0);
        await stop.CancelAsync();
        // Without the bound the pass would run on to frame 19 and finish; with it, it is waiting at frame maxLead and cancelling ends it.
        await Should.ThrowAsync<OperationCanceledException>(() => fast);
        deposited.CurrentCount.ShouldBe(0);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheSlowestPassFinishingAFrameReleasesTheWaitingOne()
    {
        const int maxLead = 4;
        var ct = TestContext.Current.CancellationToken;
        var written = new List<int>();
        var assembler = new BayerMosaicAssembler(maxLead + 1, Width * Height, 2, maxLead, (i, _) => written.Add(i));
        var deposited = new SemaphoreSlim(0);
        var fast = Task.Factory.StartNew(() =>
        {
            for (var i = 0; i <= maxLead; i++)
            {
                Deposit(assembler, i, ct);
                deposited.Release();
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        for (var i = 0; i < maxLead; i++)
        {
            await deposited.WaitAsync(ct);
        }

        // Frame 0 completes: the writer advances and the wait on frame maxLead ends.
        Deposit(assembler, 0, ct);
        await fast;
        for (var i = 1; i <= maxLead; i++)
        {
            Deposit(assembler, i, ct);
        }

        assembler.Next.ShouldBe(maxLead + 1);
        written.ShouldBe(Enumerable.Range(0, maxLead + 1));
    }

    [Fact(Timeout = 60_000)]
    public async Task ThreadsDepositingAtOnceWriteEveryFrameOnceInOrderWithTheLeadBounded()
    {
        const int count = 400;
        const int maxLead = 8;
        var ct = TestContext.Current.CancellationToken;
        var written = new List<int>();
        var assembler = new BayerMosaicAssembler(count, Width * Height, 3, maxLead, (i, _) => written.Add(i));
        var passes = Enumerable.Range(0, 3).Select(_ => Task.Factory.StartNew(() =>
        {
            for (var i = 0; i < count; i++)
            {
                Deposit(assembler, i, ct);
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(passes);
        assembler.Drain();

        assembler.Next.ShouldBe(count);
        written.ShouldBe(Enumerable.Range(0, count));
    }
}
