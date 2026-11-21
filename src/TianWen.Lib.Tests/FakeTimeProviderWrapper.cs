using Microsoft.Extensions.Time.Testing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Tests;

/// <summary>
/// Test <see cref="ITimeProvider"/> that wraps <see cref="FakeTimeProvider"/>.
/// <see cref="SleepAsync"/> auto-advances fake time (unless <see cref="ExternalTimePump"/> is set),
/// enabling deterministic time-dependent tests without a pump loop.
/// </summary>
public sealed class FakeTimeProviderWrapper : ITimeProvider
{
    private readonly FakeTimeProvider _fake;

    /// <summary>The fake clock as a <see cref="TimeProvider"/> that records when each of its timers is next due.</summary>
    private readonly RecordingTimeProvider _recording;

    /// <summary>
    /// Every <see cref="SleepAsync"/> parked under the external pump, with its target in UTC ticks, keyed per park so
    /// two sleeps to the same instant are two entries. With <see cref="RecordingTimeProvider"/>'s timers it is what
    /// the pump steps to (<see cref="NextStep"/>).
    /// </summary>
    private readonly ConcurrentDictionary<long, Park> _parkedSleeps = new();
    private long _nextParkId;

    public FakeTimeProviderWrapper(DateTimeOffset? now = null, TimeSpan? autoAdvanceAmount = null)
    {
        _fake = now is { }
            ? new FakeTimeProvider(now.Value) { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero }
            : new FakeTimeProvider() { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero };
        _recording = new RecordingTimeProvider(_fake);
    }

    /// <summary>
    /// The finest step <see cref="PumpUntilCompletedAsync"/> takes unless a test says otherwise: the floor that keeps
    /// a crowd of tiny sleeps from turning the pump into a busy spin. A waiter due sooner than this wakes up to this
    /// much late, which is the one place a step still overshoots.
    /// </summary>
    public static readonly TimeSpan DefaultFinestStep = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The coarsest step <see cref="PumpUntilCompletedAsync"/> takes unless a test says otherwise: the cap for work
    /// parked on nothing the wrapper sees (a poll between awaits that sleeps on no clock), and the step when nothing
    /// at all is due. Five seconds is what nearly every pumped test chose for itself before the pump stepped to
    /// the next due waiter.
    /// </summary>
    public static readonly TimeSpan DefaultCoarsestStep = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When true, <see cref="SleepAsync"/> waits for the fake time to advance (driven by an external pump)
    /// rather than advancing time itself. This prevents concurrent Advance calls from racing.
    /// </summary>
    public bool ExternalTimePump { get; set; }

    /// <summary>
    /// Test seam: run on the creating thread after a timer's due time is recorded and before the inner timer is armed,
    /// the window an <see cref="Advance"/> from another thread can land in (#2194). Tests only.
    /// </summary>
    internal Action? BetweenRecordAndInnerTimer { get => _recording.Seam; set => _recording.Seam = value; }

    /// <summary>Whether a live one-shot timer is due after <paramref name="atLeast"/> from now (the look of <see cref="WaitForTimerDueAfterAsync"/>).</summary>
    internal bool HasTimerDueAfter(TimeSpan atLeast) => _recording.HasOneShotDueAfter(_fake.GetUtcNow().UtcTicks + atLeast.Ticks);

    /// <summary>
    /// How long <see cref="PumpUntilCompletedAsync"/> waits for a <see cref="SleepAsync"/> waiter to appear, or for a
    /// released one to leave its park, before advancing anyway. Pacing is an optimisation; liveness is not, and a
    /// session loop has phases with no waiter at all.
    /// </summary>
    private static readonly TimeSpan PacingBound = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long the pump gives a released sleep to park again before the next step: short, because a sleep that wakes
    /// to a timer or to the end of its loop never parks again and every advance that released one pays it.
    /// </summary>
    private static readonly TimeSpan SettleBound = TimeSpan.FromMilliseconds(2);

    /// <summary>The least wall-clock time between two advances (see the dwell in <see cref="PumpUntilCompletedAsync"/>).</summary>
    private static readonly TimeSpan MinimumDwell = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// How many advances of this provider's pumps waited the whole re-park bound without the loop parking again
    /// (see the <c>settleBound</c> of <see cref="PumpUntilAsync"/>), and the real time spent in those re-park waits
    /// altogether. A pump whose bound is long pays it on every such advance, so this is what tells a backstop that
    /// never fires from one that sets the test's wall-clock time (#1910). Read once the pump has returned.
    /// </summary>
    public int ReParkWaitsRunOut { get; private set; }

    /// <inheritdoc cref="ReParkWaitsRunOut"/>
    public TimeSpan ReParkWaited { get; private set; }

    /// <summary>
    /// Waits until <paramref name="done"/> holds or <paramref name="bound"/> has passed, YIELDING between looks, never
    /// sleeping on a timer: a 1 ms <c>Task.Delay</c> is about 11 ms on Windows even with the timer resolution raised,
    /// and two of them per advance were most of what a pumped run cost (#1122: 22 ms of a 22 ms advance, measured).
    /// Returns whether it held.
    /// </summary>
    private static async ValueTask<bool> WaitUntilAsync(Func<bool> done, TimeSpan bound, CancellationToken cancellationToken)
    {
        var start = global::System.Diagnostics.Stopwatch.GetTimestamp();
        while (!done())
        {
            if (cancellationToken.IsCancellationRequested
                || global::System.Diagnostics.Stopwatch.GetElapsedTime(start) >= bound)
            {
                return false;
            }

            await Task.Yield();
        }

        return true;
    }

    /// <summary>
    /// Number of <see cref="SleepAsync"/> calls currently parked inside the
    /// <see cref="ExternalTimePump"/> wait loop. The external pump should wait
    /// for this to become &gt; 0 before advancing fake time on the first
    /// iteration -- otherwise the pump can rip through the observation window
    /// before the session-loop task even gets scheduled, leaving it to read
    /// <see cref="GetUtcNow"/> at a post-window time and exit without imaging.
    /// See <see cref="WaitForFirstWaiterAsync"/> for the idiomatic await.
    /// </summary>
    public int WaiterCount => Volatile.Read(ref _waiterCount);
    private int _waiterCount;

    /// <summary>
    /// Blocks until at least one task is parked in <see cref="SleepAsync"/>'s
    /// external-pump wait loop, OR the supplied <paramref name="loopTask"/>
    /// (the work the pump is meant to drive) has already completed, OR
    /// <paramref name="cancellationToken"/> fires. Use this in place of a
    /// fixed-duration <c>Task.Delay</c> warm-up before pumping fake time --
    /// it eliminates the CI-runner contention race where a 50 ms warm-up
    /// occasionally wasn't long enough for the Task.Run continuation to
    /// schedule + reach its first SleepAsync.
    /// </summary>
    public async Task WaitForFirstWaiterAsync(Task loopTask, CancellationToken cancellationToken = default)
    {
        while (WaiterCount == 0
            && !loopTask.IsCompleted
            && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1, cancellationToken);
        }
    }

    /// <summary>
    /// Waits until a live one-shot timer is due at least <paramref name="atLeast"/> from now, YIELDING between looks: a
    /// deadline test sets its deadline far past every other timer, waits here until the code under test has made it (a
    /// deadline built on this clock, never on the wall clock), then advances the clock past it ONCE.
    /// </summary>
    /// <param name="atLeast">The least distance from now the awaited timer is due at.</param>
    /// <param name="cancellationToken">The test's token; its cancellation (the fact's timeout) is the only end of a wait that never comes.</param>
    /// <param name="description">What is awaited ("attempt 2's handshake deadline"), named in the failure of a wait that never comes.</param>
    public async Task WaitForTimerDueAfterAsync(TimeSpan atLeast, CancellationToken cancellationToken, string? description = null)
    {
        try
        {
            while (true)
            {
                // Taken BEFORE the look: a timer recorded between the look and the wait has already ended this task,
                // so the wait returns at once and the look is made again. Nothing is polled.
                var change = _recording.NextChange;
                if (_recording.HasOneShotDueAfter(_fake.GetUtcNow().UtcTicks + atLeast.Ticks))
                {
                    return;
                }

                await change.WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw NeverCame(ex, description, $"a one-shot timer due at least {atLeast} from now", cancellationToken);
        }
    }

    /// <summary>The clock's now and every live timer with its due time relative to now, for a failure message of a wait on something else (a fake device's write).</summary>
    public string DescribeLiveTimers()
        => $"Fake now is {_fake.GetUtcNow():O}. Live timers: {_recording.DescribeTimers(_fake.GetUtcNow().UtcTicks)}";

    /// <summary>
    /// The failure of a wait whose token ended: it names what was awaited and lists every live timer with its due time
    /// relative to now. Built only on this path, once the test's own token has ended the wait: the wait has no bound of
    /// its own (a wall-clock timeout inside a test is not allowed), so there is no earlier moment at which "it never came"
    /// is known, and a message written before that would describe a wait that may still succeed.
    /// </summary>
    private OperationCanceledException NeverCame(OperationCanceledException inner, string? description, string shape, CancellationToken token)
        => new($"Waited for {description ?? "a timer"} ({shape}) and the test's token ended first. "
            + $"Fake now is {_fake.GetUtcNow():O}. Live timers: {_recording.DescribeTimers(_fake.GetUtcNow().UtcTicks)}", inner, token);

    /// <summary>
    /// <see cref="WaitForTimerDueAfterAsync"/> for a timer due no later than <paramref name="atMost"/> from now as well.
    /// A test whose code under test keeps ANOTHER live deadline further out (a reader's silence timer, set past the
    /// advance so that the advance cannot wake it) needs this: "a timer due after half the deadline" is then true of
    /// that other deadline before the one under test has even been made, and an advance taken on that look fires nothing.
    /// </summary>
    public async Task WaitForTimerDueWithinAsync(TimeSpan atLeast, TimeSpan atMost, CancellationToken cancellationToken, string? description = null)
    {
        try
        {
            while (true)
            {
                // As in WaitForTimerDueAfterAsync. An advance is a change too: it moves the window this look is
                // taken through, so a timer that was further out than atMost can come into it with no timer made.
                var change = _recording.NextChange;
                var now = _fake.GetUtcNow().UtcTicks;
                if (_recording.HasOneShotDueAfter(now + atLeast.Ticks, now + atMost.Ticks))
                {
                    return;
                }

                await change.WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw NeverCame(ex, description, $"a one-shot timer due between {atLeast} and {atMost} from now", cancellationToken);
        }
    }

    /// <summary>
    /// Drives <paramref name="loopTask"/> (a session loop started via <c>Task.Run</c> with
    /// <see cref="ExternalTimePump"/> = true) to completion by advancing fake time, PACED to the
    /// loop: it advances only while something is parked at a <see cref="SleepAsync"/> waiter, and
    /// waits while the loop is doing CPU work rather than racing wall-clock.
    /// <para>
    /// <b><paramref name="maxFakeTime"/> bounds a STALL, not the run</b>, and supplying
    /// <paramref name="progress"/> is what makes that true. The waiter pacing above is necessary but
    /// NOT sufficient, because <see cref="WaiterCount"/> is global: a fake guider's capture loop and
    /// a fake camera sit parked in <see cref="SleepAsync"/> more or less permanently, so "is anyone
    /// waiting?" answers yes whether or not the loop being driven has caught up. The loop's own tick
    /// is a <see cref="System.Threading.PeriodicTimer"/>, which registers no waiter AND coalesces --
    /// a tick that fires while its continuation is still queued is dropped, never queued behind the
    /// last one. So every advance the loop does not observe is budget spent for nothing, and how
    /// many of those there are is a property of the thread pool. Measured on one machine, one test,
    /// with nothing but scheduling changing: 30 minutes of observation cost 33 to 50 minutes of
    /// budget. A CI runner running four collections in parallel is free to be an order worse, and
    /// when it is, a perfectly healthy loop trips a fake-time cap that reads exactly like a hang.
    /// That was the <c>loopTask.IsCompleted == false</c> failure, and it is not flakiness: the cap
    /// was measuring the runner.
    /// </para>
    /// <para>
    /// With <paramref name="progress"/> supplied the budget resets every time the loop moves, so a
    /// slow runner merely takes longer instead of failing, while a loop that has genuinely stopped
    /// still trips it -- which is the only thing the cap was ever meant to catch. A real hang, where
    /// the loop never re-parks at all, stays bounded by the test's own <c>[Fact(Timeout)]</c>.
    /// </para>
    /// <para>
    /// <b>Each advance goes to the next instant anything is waiting for</b> (<see cref="NextStep"/>): the earliest
    /// target of a parked <see cref="SleepAsync"/> or due time of a one-shot timer made on this clock (a fake
    /// camera's exposure ending, a slew arriving, a <c>Task.Delay</c>), bounded below by <paramref name="finestStep"/>
    /// and above by <paramref name="coarsestStep"/>. So a waiter wakes at its own target rather than at the end of a
    /// step the test chose (#1122): a 100 ms focuser poll no longer oversleeps a 30 s step, and a whole run no
    /// longer pays a minute for a 1 s one. A periodic timer, the imaging loop's tick among them, is a poll and is
    /// coalesced within the coarsest step, as it always was (<see cref="RecordingTimeProvider.EarliestDueAfter"/>
    /// says why). A test sets neither bound unless it has a reason to.
    /// </para>
    /// <para>
    /// <b>What one advance releases, and in what order.</b> Timers due by the new instant fire first, inside the advance.
    /// Then every parked sleep it reached is released, the earliest target first and for one target in the order they
    /// parked (<see cref="ReleaseDueParks"/>). They resume on the pool, so sleeps released together run concurrently:
    /// the order is one of release, not of execution.
    /// </para>
    /// <para>
    /// <b>A sleep that parks late can be stepped past.</b> It is never lost: its target is read from the clock as it
    /// parks, and the next advance that reaches it releases it. But the step is chosen from the parks the pump can see,
    /// and after an advance it waits only a bounded real time (the settle bound, 2 ms unless given) for what it released
    /// to park again. A continuation a busy pool runs later than that parks after the next step was chosen, and wakes
    /// up to one coarsest step late. <see cref="ReParkWaitsRunOut"/> counts the advances whose wait ran out.
    /// </para>
    /// </summary>
    /// <param name="loopTask">The session-loop task to drive to completion.</param>
    /// <param name="maxFakeTime">Fake time the loop may go WITHOUT progressing before this throws
    ///   (the whole run when no <paramref name="progress"/> is supplied).</param>
    /// <param name="onIteration">Optional 1-based per-iteration hook, run after each advance, for
    ///   injecting conditions mid-run (clouds, focus drift, ...). Sync bodies return
    ///   <see cref="ValueTask.CompletedTask"/>.</param>
    /// <param name="progress">Monotonic counter of the DRIVEN loop's own progress -- for a session
    ///   loop, <c>Session.ImagingLoopTicks</c>. Pass it wherever one exists; without it the cap is
    ///   back to measuring the thread pool.</param>
    /// <param name="finestStep">The finest step, <see cref="DefaultFinestStep"/> unless given.</param>
    /// <param name="coarsestStep">The coarsest step, <see cref="DefaultCoarsestStep"/> unless given.</param>
    /// <param name="cancellationToken">Cancelled by the test's <c>[Fact(Timeout)]</c>, which bounds
    ///   a genuine hang (the loop never re-parking).</param>
    /// <returns>Total fake time pumped.</returns>
    /// <exception cref="TimeoutException">The loop stopped progressing for
    ///   <paramref name="maxFakeTime"/> of fake time. Every caller treats a non-completed loop as a
    ///   failure, so it is reported here, where the counters that say WHICH failure it was are still
    ///   in hand, rather than as a bare <c>IsCompleted</c> assertion downstream that cannot tell a
    ///   stalled loop from a starved one.</exception>
    public Task<TimeSpan> PumpUntilCompletedAsync(
        Task loopTask,
        TimeSpan maxFakeTime,
        Func<int, ValueTask>? onIteration = null,
        Func<long>? progress = null,
        TimeSpan? finestStep = null,
        TimeSpan? coarsestStep = null,
        CancellationToken cancellationToken = default)
        => PumpCoreAsync(() => loopTask.IsCompleted, maxFakeTime, onIteration, progress, finestStep, coarsestStep, cancellationToken);

    /// <summary>
    /// The same cooperative pump as <see cref="PumpUntilCompletedAsync"/> for a loop that never completes
    /// (a guide loop parks at Guiding): advances paced to the loop, with the same progress probe, until
    /// <paramref name="condition"/> holds. The condition is read between advances, after the loop has been
    /// given its turn, so it never races a half-processed frame (#1910). Returns the fake time pumped.
    /// <paramref name="settleBound"/> is how long, in real time, an advance waits for the loop to park at a new
    /// <c>SleepAsync</c> after the advance released one (the 2 ms default unless given). The waiter count belongs to
    /// this provider alone, so unlike the pool's process-wide queue it measures this loop and nothing else, and the
    /// wait ends the moment the count is back. It is not always back: an advance that released two parks, one of which
    /// ends without parking again, pays the whole bound, so a long bound costs wall-clock time on every such advance
    /// (<see cref="ReParkWaitsRunOut"/> counts them). The pool-quiet wait keeps its 2 ms bound.
    /// <paramref name="advance"/> is how the clock is moved when the caller must not let the pump do it inline: a timer
    /// an advance fires can run the code under test on the advancing thread, and that code may block there (a test that
    /// holds a session at an event). It is given the step and returns when the clock has moved; null advances inline.
    /// </summary>
    public Task<TimeSpan> PumpUntilAsync(
        Func<bool> condition,
        TimeSpan maxFakeTime,
        Func<int, ValueTask>? onIteration = null,
        Func<long>? progress = null,
        TimeSpan? finestStep = null,
        TimeSpan? coarsestStep = null,
        CancellationToken cancellationToken = default,
        TimeSpan? settleBound = null,
        Func<TimeSpan, Task>? advance = null)
        => PumpCoreAsync(condition, maxFakeTime, onIteration, progress, finestStep, coarsestStep, cancellationToken, settleBound, advance);

    private async Task<TimeSpan> PumpCoreAsync(
        Func<bool> isDone,
        TimeSpan maxFakeTime,
        Func<int, ValueTask>? onIteration = null,
        Func<long>? progress = null,
        TimeSpan? finestStep = null,
        TimeSpan? coarsestStep = null,
        CancellationToken cancellationToken = default,
        TimeSpan? settleBound = null,
        Func<TimeSpan, Task>? advance = null)
    {
        var settle = settleBound ?? SettleBound;
        var finest = finestStep ?? DefaultFinestStep;
        var coarsest = coarsestStep ?? DefaultCoarsestStep;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(finest, TimeSpan.Zero, nameof(finestStep));
        ArgumentOutOfRangeException.ThrowIfLessThan(coarsest, finest, nameof(coarsestStep));

        // The pump no longer waits on a timer (#1122: it yields, and a parked sleep is released by the
        // advance that reaches it), but the code under test may, and the parked sleeps keep a 50 ms
        // backstop. Before #1122 every wait here asked for 1 ms and got the Windows scheduling quantum,
        // ~15.7 ms, and holding the resolution up took the functional suite from 3m05 to 1m20. Treat
        // it as removing a floor, never as a fix for a slow test.
        using var _ = WindowsTimerResolution.Raise();

        var pumped = TimeSpan.Zero;
        var iteration = 0;
        var startProgress = progress?.Invoke() ?? 0L;
        var lastProgress = startProgress;
        var stalledFor = TimeSpan.Zero;
        while (!isDone() && !cancellationToken.IsCancellationRequested)
        {
            // Pace to the loop: advance only once it is parked at a SleepAsync waiter; while it is
            // doing CPU work (no waiter) wait for it to re-park rather than racing wall-clock.
            //
            // BOUNDED, because pacing must never become the stop condition. A session loop has whole
            // phases with nothing parked in SleepAsync -- a slew poll, a goto-completion hook, the gap
            // between two observations -- and an unbounded wait there spins forever WITHOUT ever
            // reaching the budget check below, so the run hangs until the test's [Fact(Timeout)]
            // instead of failing with a diagnosis. Waiting a short while and then advancing anyway
            // costs the paced case nothing (a waiter appears at once) and keeps the loop live for the
            // phases that have no waiter at all.
            await WaitUntilAsync(() => WaiterCount > 0 || isDone(), PacingBound, cancellationToken);

            // And until the pool has no queued work. What a timer woke (the imaging loop's PeriodicTimer), and a loop
            // just started with Task.Run, are parked on nothing the pump can see, and a parked device keeps the waiter
            // count above zero regardless. The old pump's 11 ms delays let such work run by accident; without this a
            // loop was overtaken by a hundred advances before it ran once (FakeTimePumpTests' healthy loop saw no
            // tick, #1017's shape). Bounded short: other tests' work shares the pool.
            await WaitUntilAsync(() => ThreadPool.PendingWorkItemCount == 0 || isDone(), SettleBound, cancellationToken);

            if (isDone() || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var waitersBefore = WaiterCount;
            var advancedAt = global::System.Diagnostics.Stopwatch.GetTimestamp();
            var step = NextStep(finest, coarsest);
            if (advance is null)
            {
                Advance(step);
            }
            else
            {
                await advance(step);
            }
            pumped += step;
            iteration++;

            // Let what this advance released see the instant it was released at: the advance released every sleep
            // due by now, and the clock does not move on until each has left its park. The waiter pacing above
            // cannot do this, since a parked device keeps the count above zero, and a step that lands exactly on a
            // 100 ms poll is worth nothing if the poll then reads the time of the step after it.
            await WaitUntilAsync(() => !HasReleasedParks() || isDone(), PacingBound, cancellationToken);

            if (onIteration is not null)
            {
                await onIteration(iteration);
            }

            // And give what they woke to its turn: a poll that sleeps again parks a new target, which the next step
            // must see or it steps past it. Bounded short, since a sleep that wakes to a timer or to the end of its
            // loop never parks again, and every advance that released one pays the bound. So does an advance that
            // released two parks of which one ends without parking (one axis's pulse wait while the other's runs on),
            // which a guide loop does every few seconds of fake time (#1910): the count never climbs back.
            var reParkStart = global::System.Diagnostics.Stopwatch.GetTimestamp();
            if (!await WaitUntilAsync(() => WaiterCount >= waitersBefore || isDone(), settle, cancellationToken))
            {
                ReParkWaitsRunOut++;
            }
            ReParkWaited += global::System.Diagnostics.Stopwatch.GetElapsedTime(reParkStart);

            // And never less than a short dwell per advance, yielding. Nothing the pump can observe says that a loop
            // just started, or still being compiled, has reached its first wait: measured, a healthy loop on a
            // PeriodicTimer was overtaken by all hundred advances of a budget, in 18 ms, before it ticked once. The old
            // pump's two 11 ms delays were this dwell by accident; this is it on purpose, at a twentieth of the cost.
            await WaitUntilAsync(() => isDone()
                || global::System.Diagnostics.Stopwatch.GetElapsedTime(advancedAt) >= MinimumDwell, MinimumDwell, cancellationToken);

            // Charge the budget only while the loop is NOT moving. The probe is read AFTER the wait
            // above, so a loop whose continuation merely had to wait its turn on the pool counts as
            // progress rather than as a stall -- which is the whole distinction this exists to make.
            if (progress is null)
            {
                stalledFor = pumped;
            }
            else
            {
                var current = progress();
                if (current != lastProgress)
                {
                    lastProgress = current;
                    stalledFor = TimeSpan.Zero;
                }
                else
                {
                    stalledFor += step;
                }
            }

            if (stalledFor >= maxFakeTime)
            {
                throw new TimeoutException(
                    $"Pumped {pumped} of fake time over {iteration} advances and the loop " +
                    (progress is null
                        ? "never completed. No progress probe was supplied, so this cap measured the RUN, and a "
                          + "run's fake-time cost depends on how often the thread pool let the loop observe an "
                          + "advance. Pass progress: () => session.ImagingLoopTicks before reading this as a hang."
                        : $"made no progress for the last {stalledFor} of it (probe {startProgress} -> "
                          + $"{lastProgress}). It is parked but not advancing: a stall, not a starved runner."));
            }
        }
        return pumped;
    }

    /// <summary>
    /// The step to the earliest instant anything is waiting for, strictly after now: a parked <see cref="SleepAsync"/>'s
    /// target or a timer's next due time, clamped to [<paramref name="finest"/>, <paramref name="coarsest"/>], and the
    /// coarsest when nothing is due at all. An instant already reached is skipped: its waiter has been released and is
    /// on its way out.
    /// </summary>
    internal TimeSpan NextStep(TimeSpan finest, TimeSpan coarsest)
    {
        var now = _fake.GetUtcNow().UtcTicks;
        var next = _recording.EarliestDueAfter(now);
        foreach (var (_, park) in _parkedSleeps)
        {
            if (park.Target > now && park.Target < next)
            {
                next = park.Target;
            }
        }

        if (next == long.MaxValue)
        {
            return coarsest;
        }

        var step = TimeSpan.FromTicks(next - now);
        return step < finest ? finest : step > coarsest ? coarsest : step;
    }

    /// <summary>Whether a parked sleep is due by now and has not yet left its park.</summary>
    private bool HasReleasedParks()
    {
        var now = _fake.GetUtcNow().UtcTicks;
        foreach (var (_, park) in _parkedSleeps)
        {
            if (park.Target <= now)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Releases every parked sleep whose target the clock has reached: the earliest target first, and for one target in
    /// the order the sleeps parked.
    /// </summary>
    /// <remarks>
    /// That is the order they are RELEASED in, and so the order their continuations are queued to the pool. It does not
    /// make them run one after another: a release completes its sleep asynchronously
    /// (<c>RunContinuationsAsynchronously</c>), so two sleeps one advance releases resume on two pool threads at once,
    /// and a test must not rely on which of them gets anywhere first. Only "a timer before a sleep" is a real order:
    /// timers fire inside the clock's own advance, before any park is released. Before #1414 the release followed the
    /// dictionary's enumeration, which is unspecified.
    /// </remarks>
    private void ReleaseDueParks()
    {
        if (DueParks(_fake.GetUtcNow().UtcTicks) is { } due)
        {
            foreach (var (_, park) in due)
            {
                park.Release();
            }
        }
    }

    /// <summary>The parked sleeps due by <paramref name="now"/>, in the order they are released; null for none.</summary>
    private List<(long Id, Park Park)>? DueParks(long now)
    {
        List<(long Id, Park Park)>? due = null;
        foreach (var (id, park) in _parkedSleeps)
        {
            if (park.Target <= now)
            {
                (due ??= []).Add((id, park));
            }
        }

        due?.Sort(static (a, b) => a.Park.Target != b.Park.Target ? a.Park.Target.CompareTo(b.Park.Target) : a.Id.CompareTo(b.Id));
        return due;
    }

    /// <summary>
    /// The sleeps an advance to <paramref name="instant"/> would release, as the number each was parked under (1 for
    /// this clock's first park), in the order of release. For the test of that order; nothing is released.
    /// </summary>
    internal long[] ParksAnAdvanceToWouldRelease(DateTimeOffset instant)
        => DueParks(instant.UtcTicks) is { } due ? [.. due.ConvertAll(static d => d.Id)] : [];

    /// <summary>
    /// Advances the fake time provider by the specified duration, firing its due timers (inside the advance) and
    /// releasing every parked <see cref="SleepAsync"/> it has reached.
    /// Only for use by the external time pump (test thread).
    /// </summary>
    public void Advance(TimeSpan duration)
    {
        _fake.Advance(duration);
        // Now moved, and with it what "due within" means: wake the timer waits to look again.
        _recording.NoteChange();
        ReleaseDueParks();
    }

    /// <summary>
    /// How often a parked <see cref="SleepAsync"/> looks at the clock itself, as a backstop for a clock moved without
    /// <see cref="Advance"/>; the advance that reaches its target releases it at once.
    /// </summary>
    private static readonly TimeSpan BackstopPoll = TimeSpan.FromMilliseconds(50);

    /// <summary>A parked <see cref="SleepAsync"/>: its target, and the signal the advance that reaches it gives.</summary>
    private sealed class Park(long target)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Target { get; } = target;

        public Task Released => _released.Task;

        public void Release() => _released.TrySetResult();
    }

    public DateTimeOffset GetUtcNow() => _fake.GetUtcNow();

    public long GetTimestamp() => _fake.GetTimestamp();

    public long TimestampFrequency => _fake.TimestampFrequency;

    public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => _recording.CreateTimer(callback, state, dueTime, period);

    /// <summary>
    /// Sleeps in fake time -- and throws <see cref="OperationCanceledException"/> on a cancelled token,
    /// exactly as the real <c>Task.Delay(duration, timeProvider, ct)</c> does. That second half used to
    /// be missing from the auto-advance path, which simply advanced and returned, so a background loop
    /// that had been cancelled kept running to its next natural exit: a <c>FakeGuider</c> capture loop
    /// cancelled by <c>StopCaptureAsync</c> finished its frame while the next loop was already started
    /// on the same camera, and the two released each other's frames
    /// (<c>DeviceOwnershipTests.AFinishedRunGivesTheRigBack</c>, 6 of 9 runs in isolation). A sleep
    /// that ignores its token is not a faster fake; it is a different contract.
    /// </summary>
    public async ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ExternalTimePump)
        {
            // Wait until the external pump has advanced time past our target.
            // Increment WaiterCount around the poll so the pump can detect that
            // at least one caller is parked before it starts advancing time
            // (see WaitForFirstWaiterAsync). Interlocked because the pump task
            // and any number of session worker tasks can park concurrently.
            //
            // The target is registered for the pump to step to (NextStep) BEFORE the sleep counts as parked: the pump
            // advances once it sees a waiter, and a waiter whose target it cannot yet see is stepped over (a 100 ms
            // sleep woke at the next 5 s tick when the count came first).
            //
            // Released by the advance that passes the target (ReleaseDueParks), never by polling the clock: a 1 ms
            // poll is about 11 ms on Windows, so a parked sleep took that long to notice each advance (#1122). A look
            // every BackstopPoll remains for a clock moved some other way than through Advance.
            var target = _fake.GetUtcNow() + duration;
            var id = Interlocked.Increment(ref _nextParkId);
            var park = new Park(target.UtcTicks);
            _parkedSleeps[id] = park;
            Interlocked.Increment(ref _waiterCount);
            try
            {
                while (_fake.GetUtcNow() < target)
                {
                    await Task.WhenAny(park.Released, Task.Delay(BackstopPoll, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _waiterCount);
                _parkedSleeps.TryRemove(id, out _);
            }
        }
        else
        {
            Advance(duration);
        }

        // A timer callback fired inside Advance (or the pump) may have cancelled us mid-sleep; the real
        // sleep surfaces that as cancellation too, not as a normal return.
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The fake clock for BCL interop (a <see cref="PeriodicTimer"/>, a <c>Task.Delay</c>, a timed
    /// <see cref="CancellationTokenSource"/>). It is the underlying <see cref="FakeTimeProvider"/> in every respect but
    /// one: it records when each timer it creates is next due, so the pump can step to a one-shot one. Before #1122 a
    /// timer made here went straight to the inner clock, and the pump could see none of them.
    /// </summary>
    public TimeProvider System => _recording;

    /// <summary>
    /// A <see cref="TimeProvider"/> over the fake clock that knows each live timer's next due time. Every timer is
    /// still the inner clock's own; this only wraps its callback, to move the due time on as it fires, and its
    /// <see cref="ITimer.Change"/> and dispose, to keep the record in step.
    /// </summary>
    private sealed class RecordingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private readonly ConcurrentDictionary<RecordedTimer, byte> _timers = new();

        /// <summary>Test seam, run once between a timer's record and its inner timer (see <see cref="FakeTimeProviderWrapper.BetweenRecordAndInnerTimer"/>).</summary>
        public Action? Seam { get; set; }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new RecordedTimer(this, callback, state);
            // Made dormant, then armed by Arm: the inner timer exists before anything can see the record.
            timer.Inner = inner.CreateTimer(static s => ((RecordedTimer)s!).Fire(), timer, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            timer.Arm(dueTime, period);
            // A zero due time fires the callback inside Arm, before the record below. That callback cannot dispose
            // THIS timer and leave a disposed one recorded (#2197 asked): the only reference to it is the one returned
            // here, which nobody holds yet, so a dispose can only come after the record.
            _timers[timer] = 0;
            NoteChange();
            return timer;
        }

        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Ends at the next change to what the timer waits look at: a timer recorded, a due time recorded or changed,
        /// or the clock advanced. A waiter takes it BEFORE it looks, so a change that lands between its look and its
        /// wait is not missed (<see cref="FakeTimeProviderWrapper.WaitForTimerDueAfterAsync"/>).
        /// </summary>
        public Task NextChange => Volatile.Read(ref _changed).Task;

        /// <summary>Ends the current <see cref="NextChange"/> and starts the next.</summary>
        public void NoteChange()
            => Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

        /// <summary>The clock's now, in UTC ticks.</summary>
        private long Now => inner.GetUtcNow().UtcTicks;

        /// <summary>
        /// The earliest due time, in UTC ticks, of a live ONE-SHOT timer strictly after <paramref name="now"/>;
        /// <see cref="long.MaxValue"/> when there is none.
        /// </summary>
        /// <remarks>
        /// A periodic timer is a POLL, and is left out on purpose. Its callback fires once per period inside an
        /// advance however far the advance goes, and a <see cref="PeriodicTimer"/> coalesces ticks by design, so it
        /// is observed at least once per coarsest step, as every test observed it before. A one-shot timer is an
        /// EVENT someone waits for (a fake camera's exposure ending, a slew arriving, a <c>Task.Delay</c>), and is
        /// stepped to. Stepping to periodic ticks was measured and is unaffordable: the fake mount's axis timer runs
        /// every 100 ms and the SkyWatcher's every 50 ms for the whole night, which is tens of thousands of advances
        /// per fake hour, and the 52 pumped session tests went from four minutes to past ten without finishing.
        /// </remarks>
        public long EarliestDueAfter(long now)
        {
            var next = long.MaxValue;
            foreach (var (timer, _) in _timers)
            {
                if (timer.IsPeriodic)
                {
                    continue;
                }

                var due = timer.NextDue;
                if (due > now && due < next)
                {
                    next = due;
                }
            }

            return next;
        }

        /// <summary>
        /// Whether a live one-shot timer is due after <paramref name="ticks"/> (UTC ticks), and no later than
        /// <paramref name="atMost"/> when that is given, and will fire at all.
        /// </summary>
        public bool HasOneShotDueAfter(long ticks, long atMost = long.MaxValue - 1)
        {
            foreach (var (timer, _) in _timers)
            {
                if (!timer.IsPeriodic && timer.NextDue > ticks && timer.NextDue <= atMost && timer.NextDue != long.MaxValue)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Every live timer, one-shot or periodic, with its due time (or period) relative to <paramref name="now"/> (UTC ticks).</summary>
        public string DescribeTimers(long now)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var (timer, _) in _timers)
            {
                var due = timer.NextDue;
                var when = due == long.MaxValue ? "never due" : $"due in {TimeSpan.FromTicks(due - now)}";
                parts.Add(timer.IsPeriodic ? $"periodic ({when})" : $"one-shot ({when})");
            }

            return parts.Count == 0 ? "none" : string.Join("; ", parts);
        }

        private void Forget(RecordedTimer timer) => _timers.TryRemove(timer, out _);

        private long _fires;

        /// <summary>How many times a timer made here has fired, periodic ones included.</summary>
        public long Fires => Interlocked.Read(ref _fires);

        private sealed class RecordedTimer(RecordingTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {

            private long _nextDue = long.MaxValue;

            /// <summary>In ticks; zero for a timer that fires once.</summary>
            private long _period;

            public ITimer? Inner { get; set; }

            public long NextDue => Volatile.Read(ref _nextDue);

            public bool IsPeriodic => Volatile.Read(ref _period) > 0;

            public void Schedule(TimeSpan dueTime, TimeSpan period, long now)
            {
                Volatile.Write(ref _period, period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks);
                Volatile.Write(ref _nextDue, dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : now + dueTime.Ticks);
            }

            /// <summary>
            /// Fired at its due time: a periodic timer is next due one period on, which is how
            /// <see cref="FakeTimeProvider"/> moves its own wake-up (an advance past several periods fires it once per
            /// period, and this follows each), and a one-shot is never due again.
            /// </summary>
            public void Fire()
            {
                var period = Volatile.Read(ref _period);
                var due = NextDue;
                Volatile.Write(ref _nextDue, period > 0 && due != long.MaxValue ? due + period : long.MaxValue);
                Interlocked.Increment(ref owner._fires);
                Interlocked.Increment(ref _fired);
                callback(state);
            }

            private long _fired;

            /// <summary>
            /// Records a due time and arms the inner timer for it so that the two agree even when an
            /// <see cref="FakeTimeProvider.Advance"/> on another thread lands in between (#2194). Recording first and
            /// arming after left a gap: an advance in it moved the clock past the recorded due time, fired nothing
            /// (the inner timer was not armed yet), and the inner timer was then due a whole delay after the advance
            /// while the record showed the old due time, so a test that waited for that due time never saw it.
            /// <para>
            /// Chosen over a lock held across <c>Advance</c>: an advance runs timer callbacks on its own thread and a
            /// test may block one there (see the <c>advance</c> parameter of <c>PumpUntilAsync</c>), which would
            /// deadlock every thread that makes or changes a timer. Instead each pass reads the clock, records against
            /// that reading, arms, and reads the clock again. If it did not move, record and inner timer were made
            /// from the same instant. If it moved, the pass is repeated from the new instant, unless the timer fired
            /// meanwhile (an advance that reached it, which the fire already accounted for). The loop retries only
            /// when an advance actually happened, so it neither spins nor polls. While a pump on another thread keeps
            /// advancing, a timer made at that moment is therefore re-based once for each advance that lands during
            /// its arming, and comes due that much later than its first reading said: bounded by those advances.
            /// </para>
            /// <para>
            /// Known limit, accepted: <c>firedBefore</c> is read before the pass, so on a <c>Change</c> of a LIVE timer an
            /// advance that fires its OLDER arming just before <c>Inner.Change</c> counts as "fired meanwhile". The record
            /// then stays <c>never due</c> while the inner timer is armed, so a pump or a
            /// <see cref="WaitForTimerDueAfterAsync"/> is blind to that timer (the pump falls back to its coarse step).
            /// A timer created dormant and then changed (the Gemini deadline) has no older arming and cannot hit it, and
            /// the window is a few instructions wide. Reading the count only after <c>Inner.Change</c> would not close
            /// it, since a fire could still land between the read and the change. Likewise a periodic timer that fires
            /// while hidden keeps <c>long.MaxValue</c> (<see cref="Fire"/> leaves a hidden due time alone), so
            /// <c>DescribeTimers</c> shows it as <c>periodic (never due)</c>; harmless, it is a description only.
            /// </para>
            /// </summary>
            /// <returns>Whether the inner timer took the change: false once it is disposed, as <see cref="ITimer.Change"/> answers.</returns>
            public bool Arm(TimeSpan dueTime, TimeSpan period)
            {
                var first = true;
                var firedBefore = Interlocked.Read(ref _fired);
                // Hidden first: no waiter sees a due time until the inner timer holds it, so an Advance taken on a due
                // time the record shows has that timer armed for it (this holds for Change on a live timer as well,
                // whose old due time must not stay shown while the new one is being armed).
                Volatile.Write(ref _nextDue, long.MaxValue);
                while (true)
                {
                    var before = owner.Now;
                    if (first)
                    {
                        first = false;
                        owner.Seam?.Invoke();
                    }

                    var taken = Inner?.Change(dueTime, period) ?? false;
                    if (Interlocked.Read(ref _fired) != firedBefore)
                    {
                        // An advance reached the timer meanwhile; Fire already accounted for it.
                        Volatile.Write(ref _period, period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks);
                        return taken;
                    }

                    if (owner.Now == before)
                    {
                        Schedule(dueTime, period, before);
                        if (Interlocked.Read(ref _fired) != firedBefore && _period == 0)
                        {
                            // Fired between the check and the publish: a one-shot is spent.
                            Volatile.Write(ref _nextDue, long.MaxValue);
                        }

                        // A due time is on show: wake the waits that look for one.
                        owner.NoteChange();
                        return taken;
                    }
                }
            }

            /// <summary>
            /// As <see cref="ITimer.Change"/> answers: false for a disposed timer, which is not armed again and stays
            /// out of the record. It used to answer true always, whatever the inner timer said.
            /// </summary>
            public bool Change(TimeSpan dueTime, TimeSpan period)
                => !IsDisposed && Arm(dueTime, period);

            private int _disposed;

            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public void Dispose()
            {
                Volatile.Write(ref _disposed, 1);
                owner.Forget(this);
                Inner?.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Volatile.Write(ref _disposed, 1);
                owner.Forget(this);
                return Inner?.DisposeAsync() ?? ValueTask.CompletedTask;
            }
        }
    }
}
