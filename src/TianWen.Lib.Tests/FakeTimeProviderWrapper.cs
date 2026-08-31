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
    /// loop: it advances only while the loop is parked at a <see cref="SleepAsync"/> waiter, and
    /// waits while the loop is doing CPU work. Fake time therefore tracks the loop's real progress
    /// instead of racing wall-clock, which is starvation-proof under CI thread-pool load. The old
    /// unconditional <c>Advance</c> + <c>Task.Delay(1)</c> pump burned its whole fake-time budget in
    /// a few wall-clock seconds, so under load the loop could not process its frames before the pump
    /// gave up -- the <c>loopTask.IsCompleted == false</c> flake.
    /// </summary>
    /// <param name="loopTask">The session-loop task to drive to completion.</param>
    /// <param name="increment">Fake-time step per advance.</param>
    /// <param name="maxFakeTime">Safety cap on total fake time pumped.</param>
    /// <param name="onIteration">Optional 1-based per-iteration hook, run after each advance, for
    ///   injecting conditions mid-run (clouds, focus drift, ...). Sync bodies return
    ///   <see cref="ValueTask.CompletedTask"/>.</param>
    /// <param name="cancellationToken">Cancelled by the test's <c>[Fact(Timeout)]</c>, which bounds
    ///   a genuine hang (the loop never re-parking).</param>
    /// <returns>Total fake time pumped.</returns>
    public async Task<TimeSpan> PumpUntilCompletedAsync(
        Task loopTask,
        TimeSpan increment,
        TimeSpan maxFakeTime,
        Func<int, ValueTask>? onIteration = null,
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
        while (pumped < maxFakeTime && !loopTask.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            // Pace to the loop: advance only once it is parked at a SleepAsync waiter; while it is
            // doing CPU work (no waiter) wait for it to re-park rather than racing wall-clock.
            await WaitForFirstWaiterAsync(loopTask, cancellationToken);
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
