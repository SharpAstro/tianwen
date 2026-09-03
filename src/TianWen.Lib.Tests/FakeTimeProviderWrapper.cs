using Microsoft.Extensions.Time.Testing;
using System;
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

    public FakeTimeProviderWrapper(DateTimeOffset? now = null, TimeSpan? autoAdvanceAmount = null)
    {
        _fake = now is { }
            ? new FakeTimeProvider(now.Value) { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero }
            : new FakeTimeProvider() { AutoAdvanceAmount = autoAdvanceAmount ?? TimeSpan.Zero };
    }

    /// <summary>
    /// When true, <see cref="SleepAsync"/> waits for the fake time to advance (driven by an external pump)
    /// rather than advancing time itself. This prevents concurrent Advance calls from racing.
    /// </summary>
    public bool ExternalTimePump { get; set; }

    /// <summary>
    /// How many 1 ms polls <see cref="PumpUntilCompletedAsync"/> will wait for a
    /// <see cref="SleepAsync"/> waiter to appear before advancing anyway. Pacing is an optimisation;
    /// liveness is not, and a session loop has phases with no waiter at all.
    /// </summary>
    private const int MaxWaiterWaitPolls = 50;

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
    /// </summary>
    /// <param name="loopTask">The session-loop task to drive to completion.</param>
    /// <param name="increment">Fake-time step per advance.</param>
    /// <param name="maxFakeTime">Fake time the loop may go WITHOUT progressing before this throws
    ///   (the whole run when no <paramref name="progress"/> is supplied).</param>
    /// <param name="onIteration">Optional 1-based per-iteration hook, run after each advance, for
    ///   injecting conditions mid-run (clouds, focus drift, ...). Sync bodies return
    ///   <see cref="ValueTask.CompletedTask"/>.</param>
    /// <param name="progress">Monotonic counter of the DRIVEN loop's own progress -- for a session
    ///   loop, <c>Session.ImagingLoopTicks</c>. Pass it wherever one exists; without it the cap is
    ///   back to measuring the thread pool.</param>
    /// <param name="cancellationToken">Cancelled by the test's <c>[Fact(Timeout)]</c>, which bounds
    ///   a genuine hang (the loop never re-parking).</param>
    /// <returns>Total fake time pumped.</returns>
    /// <exception cref="TimeoutException">The loop stopped progressing for
    ///   <paramref name="maxFakeTime"/> of fake time. Every caller treats a non-completed loop as a
    ///   failure, so it is reported here, where the counters that say WHICH failure it was are still
    ///   in hand, rather than as a bare <c>IsCompleted</c> assertion downstream that cannot tell a
    ///   stalled loop from a starved one.</exception>
    public async Task<TimeSpan> PumpUntilCompletedAsync(
        Task loopTask,
        TimeSpan increment,
        TimeSpan maxFakeTime,
        Func<int, ValueTask>? onIteration = null,
        Func<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Both waits below -- this loop's trailing delay, and every SleepAsync parked under it -- ask
        // for 1 ms and get the Windows scheduling quantum instead, ~15.7 ms. Hold the resolution up
        // for the pumped run so the number in the code is the number that happens: it takes the
        // functional suite from 3m05 to 1m20. It buys nothing where the pump is genuinely waiting on
        // the LOOP rather than on its own timer (see the coupling note in SessionObservationLoopTests),
        // so treat it as removing a floor, never as a fix for a slow test.
        using var _ = WindowsTimerResolution.Raise();

        var pumped = TimeSpan.Zero;
        var iteration = 0;
        var startProgress = progress?.Invoke() ?? 0L;
        var lastProgress = startProgress;
        var stalledFor = TimeSpan.Zero;
        while (!loopTask.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            // Pace to the loop: advance only once it is parked at a SleepAsync waiter; while it is
            // doing CPU work (no waiter) wait for it to re-park rather than racing wall-clock.
            //
            // BOUNDED, because pacing must never become the stop condition. A session loop has whole
            // phases with nothing parked in SleepAsync -- a slew poll, a goto-completion hook, the gap
            // between two observations -- and an unbounded wait there spins forever WITHOUT ever
            // reaching the budget check below, so the run hangs until the test's [Fact(Timeout)]
            // instead of failing with a diagnosis. Waiting a short while and then advancing anyway
            // costs the paced case nothing (a waiter appears in a poll or two) and keeps the loop
            // live for the phases that have no waiter at all.
            for (var waited = 0;
                 WaiterCount == 0 && waited < MaxWaiterWaitPolls
                    && !loopTask.IsCompleted && !cancellationToken.IsCancellationRequested;
                 waited++)
            {
                await Task.Delay(1, cancellationToken);
            }

            if (loopTask.IsCompleted || cancellationToken.IsCancellationRequested)
            {
                break;
            }
            Advance(increment);
            pumped += increment;
            iteration++;
            if (onIteration is not null)
            {
                await onIteration(iteration);
            }
            await Task.Delay(1, cancellationToken);

            // Charge the budget only while the loop is NOT moving. The probe is read AFTER the delay
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
                    stalledFor += increment;
                }
            }

            if (stalledFor >= maxFakeTime)
            {
                throw new TimeoutException(
                    $"Pumped {pumped} of fake time over {iteration} advances of {increment} and the loop " +
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
    /// Advances the fake time provider by the specified duration.
    /// Only for use by the external time pump (test thread).
    /// </summary>
    public void Advance(TimeSpan duration) => _fake.Advance(duration);

    public DateTimeOffset GetUtcNow() => _fake.GetUtcNow();

    public long GetTimestamp() => _fake.GetTimestamp();

    public long TimestampFrequency => _fake.TimestampFrequency;

    public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => _fake.CreateTimer(callback, state, dueTime, period);

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
            Interlocked.Increment(ref _waiterCount);
            try
            {
                var target = _fake.GetUtcNow() + duration;
                while (_fake.GetUtcNow() < target && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1, cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _waiterCount);
            }
        }
        else
        {
            _fake.Advance(duration);
        }

        // A timer callback fired inside Advance (or the pump) may have cancelled us mid-sleep; the real
        // sleep surfaces that as cancellation too, not as a normal return.
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Returns the underlying <see cref="FakeTimeProvider"/> for BCL interop.</summary>
    public TimeProvider System => _fake;
}
