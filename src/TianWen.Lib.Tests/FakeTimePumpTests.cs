using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Linq;
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
                () => time.PumpUntilCompletedAsync(loop, Budget, cancellationToken: ct));

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
                loop, Budget,
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
                    loop, Budget,
                    progress: () => 0L,
                    cancellationToken: ct));

            thrown.Message.ShouldContain("a stall, not a starved runner");
            loop.IsCompleted.ShouldBeFalse();

            wedged.SetResult();
            await cts.CancelAsync();
            await parked;
        }

        /// <summary>
        /// What #1122 is for: the pump steps to the next instant anything waits for, so a 100 ms poll wakes at its
        /// own target, not at the end of a step some test chose. A coarse timer and a day-long park sit beside it,
        /// standing in for the imaging loop's tick and a parked device; neither may pull the step past the sleep.
        /// </summary>
        [Fact(Timeout = 60_000)]
        public async Task ASleepUnderThePumpWakesAtItsOwnTarget()
        {
            var ct = TestContext.Current.CancellationToken;
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero))
            {
                ExternalTimePump = true
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var start = time.GetUtcNow();
            var sleeper = Task.Run(async () =>
            {
                await time.SleepAsync(TimeSpan.FromMilliseconds(100), ct);
                return time.GetUtcNow();
            }, CancellationToken.None);

            // Nothing moves the clock until the pump runs, so the sleep's target is fixed once it is parked.
            await time.WaitForFirstWaiterAsync(sleeper, ct);
            var parked = ParkedDeviceAsync(time, cts.Token);
            using var coarseTick = time.System.CreateTimer(static _ => { }, null, Tick, Tick);

            // Read on the pump's own thread, right after each advance: what the clock was stepped to. The sleeper's own
            // read of the clock comes after it has left its park, which a later step may already have overtaken.
            var steppedTo = new ConcurrentQueue<DateTimeOffset>();
            await time.PumpUntilCompletedAsync(sleeper,
                Budget,
                onIteration: _ =>
                {
                    steppedTo.Enqueue(time.GetUtcNow());
                    return ValueTask.CompletedTask;
                },
                cancellationToken: ct);

            steppedTo.ShouldNotBeEmpty();
            steppedTo.First().ShouldBe(start + TimeSpan.FromMilliseconds(100),
                "the first advance goes to the sleep's target, not to the 5 s tick beside it");
            (await sleeper).ShouldBeGreaterThanOrEqualTo(start + TimeSpan.FromMilliseconds(100));

            await cts.CancelAsync();
            await parked;
        }

        /// <summary>
        /// A one-shot timer is an EVENT (a fake camera's exposure ending is one), and the pump steps to it: read inside
        /// the callback, which runs inside the advance, the clock says exactly when it fired. Each fire re-arms it 7 s
        /// on, longer than the coarsest step, so the pump also takes a capped step between events and must still land
        /// on every one.
        /// </summary>
        [Fact(Timeout = 60_000)]
        public async Task AOneShotTimerOnTheClockFiresAtItsOwnDueTime()
        {
            var ct = TestContext.Current.CancellationToken;
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero))
            {
                ExternalTimePump = true
            };
            var interval = TimeSpan.FromSeconds(7);
            var start = time.GetUtcNow();
            var fired = new ConcurrentQueue<DateTimeOffset>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ITimer? timer = null;
            timer = time.System.CreateTimer(_ =>
            {
                fired.Enqueue(time.GetUtcNow());
                if (fired.Count == 5)
                {
                    done.TrySetResult();
                }
                else
                {
                    timer?.Change(interval, Timeout.InfiniteTimeSpan);
                }
            }, null, interval, Timeout.InfiniteTimeSpan);

            await time.PumpUntilCompletedAsync(done.Task, Budget, cancellationToken: ct);
            timer.Dispose();

            fired.ShouldBe([.. Enumerable.Range(1, 5).Select(k => start + interval * k)]);
        }

        /// <summary>
        /// The two bounds, each pinned: a waiter due sooner than the finest step is stepped to by the finest step, one
        /// due later than the coarsest by the coarsest, and with nothing due at all the step is the coarsest. A
        /// periodic timer is a poll and never sets the step, however soon it is due: the fake mount's axis timer runs
        /// every 100 ms all night, and stepping to it is what made a whole run cost tens of thousands of advances.
        /// </summary>
        [Fact(Timeout = 60_000)]
        public async Task TheStepIsTheNextDueTimeHeldBetweenTheFinestAndTheCoarsest()
        {
            var ct = TestContext.Current.CancellationToken;
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero))
            {
                ExternalTimePump = true
            };
            var finest = TimeSpan.FromMilliseconds(10);
            var coarsest = TimeSpan.FromSeconds(5);

            time.NextStep(finest, coarsest).ShouldBe(coarsest, "nothing is due");

            using (time.CreateTimer(static _ => { }, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)))
            {
                time.NextStep(finest, coarsest).ShouldBe(coarsest, "a periodic timer is a poll, coalesced within the coarsest step");
            }

            using (time.System.CreateTimer(static _ => { }, null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan))
            {
                time.NextStep(finest, coarsest).ShouldBe(coarsest, "a timer two minutes out is stepped toward by the coarsest");
            }

            using (time.System.CreateTimer(static _ => { }, null, TimeSpan.FromMilliseconds(700), Timeout.InfiniteTimeSpan))
            {
                time.NextStep(finest, coarsest).ShouldBe(TimeSpan.FromMilliseconds(700), "a timer within the bounds is stepped to exactly");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sleeper = Task.Run(async () =>
            {
                try
                {
                    await time.SleepAsync(TimeSpan.FromMilliseconds(3), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled below once the step has been read.
                }
            }, CancellationToken.None);
            await time.WaitForFirstWaiterAsync(sleeper, ct);

            time.NextStep(finest, coarsest).ShouldBe(finest, "a sleep due in 3 ms is stepped to by the finest step");

            await cts.CancelAsync();
            await sleeper;
        }

        /// <summary>
        /// #2194: an Advance that lands between a timer's record and its inner timer must not leave the record showing a
        /// due time the inner timer does not have. The seam advances the clock there, deterministically.
        /// </summary>
        [Fact(Timeout = 30_000)]
        public void AnAdvanceBetweenTheRecordAndTheInnerTimerLeavesThemAgreeing()
        {
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var due = TimeSpan.FromSeconds(10);
            var fired = 0;
            var advancedInGap = false;
            time.BetweenRecordAndInnerTimer = () =>
            {
                if (!advancedInGap)
                {
                    advancedInGap = true;
                    time.Advance(TimeSpan.FromSeconds(6));
                }
            };

            using var timer = time.CreateTimer(_ => fired++, null, due, Timeout.InfiniteTimeSpan);

            advancedInGap.ShouldBeTrue();
            fired.ShouldBe(0);

            // The inner timer is armed from the clock as it now is (6 s on), so the record must say so: due after 5 s from now.
            time.HasTimerDueAfter(TimeSpan.FromSeconds(5)).ShouldBeTrue();

            time.Advance(TimeSpan.FromSeconds(4));
            fired.ShouldBe(0);
            time.Advance(due - TimeSpan.FromSeconds(4));
            fired.ShouldBe(1);
            time.HasTimerDueAfter(TimeSpan.Zero).ShouldBeFalse();
        }

        /// <summary>
        /// #2197: <c>ITimer.Change</c> answers false for a disposed timer, as the contract says, and does not put it
        /// back in the record. It used to answer true whatever the inner timer said.
        /// </summary>
        [Fact(Timeout = 30_000)]
        public void AChangeOnADisposedTimerAnswersFalseAndRecordsNothing()
        {
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var fired = 0;
            var timer = time.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
            timer.Change(TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan).ShouldBeTrue("a live timer takes the change");
            time.HasTimerDueAfter(TimeSpan.FromSeconds(15)).ShouldBeTrue();

            timer.Dispose();

            timer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan).ShouldBeFalse();
            time.HasTimerDueAfter(TimeSpan.Zero).ShouldBeFalse("a disposed timer is not recorded again");
            time.Advance(TimeSpan.FromSeconds(30));
            fired.ShouldBe(0);
        }

        /// <summary>
        /// #2197: a wait that began before its timer exists finishes once the timer is made. What this pins is that the
        /// wake is WIRED: the wait sleeps on the wrapper's change signal, so a timer made without signalling it would
        /// leave this test hanging to its timeout. It does not tell a woken wait from one that looked again every
        /// millisecond, as the wait used to: both finish within a millisecond of the timer, and nothing here has a
        /// clock to tell them apart with (pilot's review of #2307).
        /// </summary>
        [Fact(Timeout = 30_000)]
        public async Task AWaitForATimerIsWokenWhenTheTimerIsMade()
        {
            var ct = TestContext.Current.CancellationToken;
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

            var wait = time.WaitForTimerDueAfterAsync(TimeSpan.FromSeconds(5), ct, "the deadline under test");
            wait.IsCompleted.ShouldBeFalse("no timer exists yet, so a wait that is done already answered without looking");

            // A timer that is not the one waited for wakes the wait and must not finish it. That is not asserted: the
            // wait's continuation runs on another thread, so "still not done" read here would hold either way.
            using var near = time.CreateTimer(static _ => { }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

            using var far = time.CreateTimer(static _ => { }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
            await wait;
        }

        /// <summary>
        /// #2197: the same for a wait on a timer due inside a window, where an ADVANCE is what brings the timer into
        /// the window: nothing is made or changed, only now moves. As above, it pins that an advance wakes the wait (a
        /// missed wake hangs to the timeout), not that the wait does not poll.
        /// </summary>
        [Fact(Timeout = 30_000)]
        public async Task AWaitForATimerInsideAWindowIsWokenWhenAnAdvanceBringsItIn()
        {
            var ct = TestContext.Current.CancellationToken;
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            using var timer = time.CreateTimer(static _ => { }, null, TimeSpan.FromSeconds(100), Timeout.InfiniteTimeSpan);

            var wait = time.WaitForTimerDueWithinAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), ct, "the deadline under test");
            wait.IsCompleted.ShouldBeFalse("the timer is 100 s out, past the window");

            time.Advance(TimeSpan.FromSeconds(85));

            await wait;
        }

        /// <summary>The same window in <c>ITimer.Change</c>: the record and the inner timer are re-armed from one clock reading.</summary>
        [Fact(Timeout = 30_000)]
        public void AnAdvanceBetweenTheRecordAndTheInnerChangeLeavesThemAgreeing()
        {
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var due = TimeSpan.FromSeconds(10);
            var fired = 0;
            using var timer = time.CreateTimer(_ => fired++, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var advancedInGap = false;
            time.BetweenRecordAndInnerTimer = () =>
            {
                if (!advancedInGap)
                {
                    advancedInGap = true;
                    time.Advance(TimeSpan.FromSeconds(6));
                }
            };

            timer.Change(due, Timeout.InfiniteTimeSpan);

            advancedInGap.ShouldBeTrue();
            fired.ShouldBe(0);
            time.HasTimerDueAfter(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            time.Advance(due);
            fired.ShouldBe(1);
        }

        /// <summary>
        /// #2194, the Change path: an Advance in the gap that reaches the whole due time must not find the record showing a
        /// due time the inner timer does not have yet (the inner timer is still dormant, so that advance fires nothing and
        /// a waiter who took it would then wait for a timer a whole delay later). The record is hidden until armed.
        /// </summary>
        [Fact(Timeout = 30_000)]
        public void AChangeShowsNoDueTimeBeforeItsInnerTimerHasIt()
        {
            var time = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var due = TimeSpan.FromSeconds(10);
            var fired = 0;
            using var timer = time.CreateTimer(_ => fired++, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var shownInGap = true;
            var advancedInGap = false;
            time.BetweenRecordAndInnerTimer = () =>
            {
                if (!advancedInGap)
                {
                    advancedInGap = true;
                    time.Advance(due);
                    // Due at (or after) a second before now would include a due time equal to now, which the record must not show.
                    shownInGap = time.HasTimerDueAfter(TimeSpan.FromSeconds(-1));
                }
            };

            timer.Change(due, Timeout.InfiniteTimeSpan);

            advancedInGap.ShouldBeTrue();
            shownInGap.ShouldBeFalse("the record showed a due time the inner timer did not hold");
            fired.ShouldBe(0, "the gap advance reached a dormant inner timer");
            time.HasTimerDueAfter(TimeSpan.FromSeconds(5)).ShouldBeTrue("re-armed from the clock as it now is");
            time.Advance(due);
            fired.ShouldBe(1);
        }
    }
}
