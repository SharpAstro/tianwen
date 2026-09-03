using Shouldly;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// What <see cref="FakeTimeProviderWrapper.PumpUntilCompletedAsync"/>'s budget is allowed to mean.
    /// <para>
    /// The loop below is shaped like <c>Session.ImagingLoopAsync</c> in the two ways that matter, and
    /// both are load-bearing rather than scenery. It parks on a <see cref="PeriodicTimer"/> fed by the
    /// fake clock, which registers NO <see cref="FakeTimeProviderWrapper.SleepAsync"/> waiter and
    /// COALESCES -- a tick that fires while its continuation is still queued is dropped, not queued
    /// behind the last one. And a second task sits parked in <c>SleepAsync</c> for the whole test,
    /// which is what a fake guider's capture loop and a fake camera do in every session test. That
    /// second task is why the pump's waiter pacing cannot save it: <c>WaiterCount</c> is global, so
    /// "is anyone parked?" answers yes whether or not the loop being driven has caught up.
    /// </para>
    /// <para>
    /// So an advance the loop does not observe is budget spent for nothing, and how many of those
    /// there are is a property of the thread pool. That is why the first two tests below differ ONLY
    /// in whether a progress probe is supplied: the no-probe case is the old pump exactly, and it is
    /// kept green here as the shape of the CI failure rather than deleted.
    /// </para>
    /// </summary>
    public class FakeTimePumpTests
    {
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

        /// <summary>Ticks the loop must observe before it returns. Deliberately more than the budget.</summary>
        private const int LoopTicks = 400;

        /// <summary>
        /// A quarter of what the loop needs, so a cap that bounds the RUN can never see it finish
        /// while a cap that bounds a STALL tolerates 100 consecutive dropped ticks before giving up.
        /// </summary>
        private static readonly TimeSpan Budget = Tick * 100;

        /// <summary>
        /// Parks in <c>SleepAsync</c> for the duration, standing in for the fake guider and camera that
        /// keep <see cref="FakeTimeProviderWrapper.WaiterCount"/> off zero in every real session test.
        /// </summary>
        private static Task ParkedDeviceAsync(FakeTimeProviderWrapper time, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                try
                {
                    await time.SleepAsync(TimeSpan.FromDays(1), ct);
                }
                catch (OperationCanceledException)
                {
                    // The test is over; this is the only way out of a day-long park.
                }
            }, CancellationToken.None);
        }

        private static Task TickingLoopAsync(FakeTimeProviderWrapper time, StrongBox<long> observed, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                using var ticker = new PeriodicTimer(Tick, time.System);
                while (Interlocked.Read(ref observed.Value) < LoopTicks)
                {
                    await ticker.WaitForNextTickAsync(ct);
                    Interlocked.Increment(ref observed.Value);
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// The failure this whole change is about, pinned rather than described. Nothing is wrong with
        /// the loop -- it ticks every time it is given one -- yet the pump gives up, because with no
        /// probe the cap is charged for every advance including the ones the loop never saw.
        /// </summary>
        [Fact(Timeout = 60_000)]
        public async Task WithNoProgressProbeTheBudgetBoundsTheRunAndAHealthyLoopTripsIt()
        {
            var ct = TestContext.Current.CancellationToken;
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero))
            {
                ExternalTimePump = true
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var parked = ParkedDeviceAsync(time, cts.Token);
            var observed = new StrongBox<long>(0);
            var loop = TickingLoopAsync(time, observed, cts.Token);

            var thrown = await Should.ThrowAsync<TimeoutException>(
                () => time.PumpUntilCompletedAsync(loop, Tick, Budget, cancellationToken: ct));

            thrown.Message.ShouldContain("measured the RUN");
            loop.IsCompleted.ShouldBeFalse("the loop is healthy and still ticking; the CAP is what ran out");
            Interlocked.Read(ref observed.Value).ShouldBeGreaterThan(0, "it was never stuck -- it was making progress the whole time");

            await cts.CancelAsync();
            await parked;
        }

        /// <summary>
        /// The same loop, the same budget, the same runner -- only now the pump can see it moving, so
        /// the budget resets on progress and the loop is allowed to finish. Fake time spent will
        /// exceed <see cref="Budget"/> several times over, which is the point.
        /// </summary>
        [Fact(Timeout = 60_000)]
        public async Task WithAProgressProbeTheSameLoopIsAllowedToFinish()
        {
            var ct = TestContext.Current.CancellationToken;
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero))
            {
                ExternalTimePump = true
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var parked = ParkedDeviceAsync(time, cts.Token);
            var observed = new StrongBox<long>(0);
            var loop = TickingLoopAsync(time, observed, cts.Token);

            var pumped = await time.PumpUntilCompletedAsync(
                loop, Tick, Budget,
                progress: () => Interlocked.Read(ref observed.Value),
                cancellationToken: ct);

            loop.IsCompleted.ShouldBeTrue("a loop that keeps progressing must never trip a stall budget");
            await loop;
            Interlocked.Read(ref observed.Value).ShouldBe(LoopTicks);
            pumped.ShouldBeGreaterThan(Budget, "the run legitimately costs more fake time than the budget, which now bounds a STALL");

            await cts.CancelAsync();
            await parked;
        }

        /// <summary>
        /// The one thing the cap was ever meant to catch, kept catchable: a loop that is parked and
        /// going nowhere. Without this the change would have traded a false red for a missed one.
        /// </summary>
        [Fact(Timeout = 60_000)]
        public async Task ALoopThatHasGenuinelyStoppedStillTripsTheBudget()
        {
            var ct = TestContext.Current.CancellationToken;
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero))
            {
                ExternalTimePump = true
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var parked = ParkedDeviceAsync(time, cts.Token);
            var wedged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loop = wedged.Task;

            var thrown = await Should.ThrowAsync<TimeoutException>(
                () => time.PumpUntilCompletedAsync(
                    loop, Tick, Budget,
                    progress: () => 0L,
                    cancellationToken: ct));

            thrown.Message.ShouldContain("a stall, not a starved runner");
            loop.IsCompleted.ShouldBeFalse();

            wedged.SetResult();
            await cts.CancelAsync();
            await parked;
        }
    }
}
