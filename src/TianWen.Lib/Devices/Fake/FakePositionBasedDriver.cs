using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Can be used as a base class for focusers, filter wheels
/// </summary>
/// <param name="fakeDevice"></param>
/// <param name="external"></param>
internal abstract class FakePositionBasedDriver(FakeDevice fakeDevice, IServiceProvider serviceProvider) : FakeDeviceDriverBase(fakeDevice, serviceProvider)
{
    /// <summary>
    /// How fast the fake turns: 200 steps a second. The previous 100 ms a step (10 steps a second) made the GUI's focuser
    /// jog feel laggy: a <c>&gt;</c> click fires a +10 jog that took a full second to walk to its target one step at a
    /// time, so the user saw +1 and took the button to increment by 1. Real focusers run on the order of 100 to 500
    /// steps a second.
    /// </summary>
    internal const int StepsPerSecond = 200;

    /// <summary>
    /// How often the position is refreshed, one step at the nominal rate. It is a poll and not a step: a callback moves the
    /// position by the time that elapsed since the move began, since a timer on the real clock does not fire on time (Windows
    /// ticks every 15.6 ms, so a step per 5 ms callback ran at about 64 steps a second, #2022). The tests' fake clock fires
    /// the timer exactly on its period, so for them one callback is still one step.
    /// </summary>
    private static readonly TimeSpan TickPeriod = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / StepsPerSecond);

    protected volatile int _position;
    protected volatile bool _isMoving;

    private ITimer? _movingTimer;

    /// <summary>
    /// Sets position (absolute)
    /// </summary>
    /// <param name="position"></param>
    /// <returns>true iff position is accepted and moving started</returns>
    /// <remarks>Does not validate inputs (subclass responsibility)</remarks>
    protected virtual Task BeginSetPositionAsync(int position, CancellationToken cancellationToken = default)
    {
        var currentPosition = _position;

        if (currentPosition != position)
        {
            _isMoving = true;

            var state = new MovingState(position, TimeProvider.GetTimestamp());

            var movingTimer = TimeProvider.CreateTimer(MovingTimerCallback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            Interlocked.Exchange(ref _movingTimer, movingTimer)?.Dispose();

            movingTimer.Change(TickPeriod, TickPeriod);
        }

        return Task.CompletedTask;
    }

    protected virtual Task BeginStopMovingAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _movingTimer, null)?.Dispose();
        _isMoving = false;
        return Task.CompletedTask;
    }

    private void MovingTimerCallback(object? state)
    {
        // A Halt clears _isMoving and disposes the timer, but a callback already on its way still gets here.
        if (state is not MovingState move || !_isMoving)
        {
            return;
        }

        var currentPosition = _position;
        var remaining = (long)move.End - currentPosition;

        // Never past the target: a late callback owing more steps than are left takes only what is left.
        var steps = Math.Min(move.TakeEarnedSteps(TimeProvider), Math.Abs(remaining));
        var nextPosition = currentPosition + (int)(Math.Sign(remaining) * steps);

        if (steps > 0 && Interlocked.CompareExchange(ref _position, nextPosition, currentPosition) != currentPosition)
        {
            // Something else moved the position under this callback; the next one takes the move up from where it now is.
            return;
        }

        if (nextPosition == move.End)
        {
            _isMoving = false;
            Interlocked.Exchange(ref _movingTimer, null)?.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            Interlocked.Exchange(ref _movingTimer, null)?.Dispose();
        }
    }

    /// <summary>One move: where it ends, when it began on the driver's clock, and how many of the steps it has earned are taken.</summary>
    private sealed class MovingState(int end, long startTimestamp)
    {
        private long _stepsTaken;

        public int End { get; } = end;

        /// <summary>
        /// The steps this move has earned by now and not yet taken, by the time that elapsed since it began at
        /// <see cref="StepsPerSecond"/>, rounded to the nearest step. The count is cumulative from the start and never per
        /// callback, so the fraction of a step one callback arrived short of (or past) is carried into the next, and the
        /// rate over the whole move is exactly the nominal one however late the ticks are. Lock-free: a callback that
        /// loses the exchange to another reads again, and the steps are shared out between them, never taken twice.
        /// </summary>
        public long TakeEarnedSteps(ITimeProvider clock)
        {
            var elapsedTicks = Math.Max(0L, clock.GetTimestamp() - startTimestamp);
            var frequency = clock.TimestampFrequency;
            var earned = (elapsedTicks * StepsPerSecond + frequency / 2) / frequency;

            var taken = Volatile.Read(ref _stepsTaken);
            while (earned > taken)
            {
                var seen = Interlocked.CompareExchange(ref _stepsTaken, earned, taken);
                if (seen == taken)
                {
                    return earned - taken;
                }

                taken = seen;
            }

            return 0;
        }
    }
}
